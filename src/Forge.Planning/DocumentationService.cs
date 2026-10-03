using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Programs.Contracts;

namespace Forge.Planning;

public sealed record PurchaseLineDocumentation(int LineNumber, string PartNumber, string? ItemRevision, IReadOnlyList<DocumentApplicability> Documents, string Summary);

public sealed record PurchaseDocumentation(long PoNumber, string Supplier, IReadOnlyList<PurchaseLineDocumentation> Lines);

/// <summary>
/// Applicability for orders and purchase orders (V2.3): the documents the item revision is built to and
/// the documents attached to follow, each resolved by the applicability engine. Documents the reader
/// may not see are shown as restricted, never named, but still counted: hiding one must not make work
/// look ready.
/// </summary>
public sealed class DocumentationService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public const string Restricted = "Restricted document";

    public async Task<OrderDocumentation> OrderAsync(ForgePrincipal principal, long orderNumber, CancellationToken ct)
    {
        Demand(principal, Permissions.OrdersRead);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
        {
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        }

        return await ForOrderAsync(uow, order, time.GetUtcNow(), principal, ct);
    }

    public async Task<PurchaseDocumentation> PurchaseOrderAsync(ForgePrincipal principal, long poNumber, CancellationToken ct)
    {
        Demand(principal, Permissions.PurchasingRead);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var po = await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(poNumber, ct)
            ?? throw new CommandRejectedException("purchase_order_not_found", $"Purchase order {poNumber} does not exist.");
        var now = time.GetUtcNow();
        var engineering = uow.Store<IEngineeringLookup>();
        var cache = new Cache(uow.Store<IDocumentLookup>());
        var lines = new List<PurchaseLineDocumentation>();
        foreach (var line in po.Lines.Where(l => l.Status != PurchaseLineStatus.Cancelled))
        {
            var revision = await engineering.GetEffectiveRevisionAsync(line.PartNumber, now, ct);
            if (revision is null)
            {
                lines.Add(new PurchaseLineDocumentation(line.LineNumber, line.PartNumber, null, [], $"{line.PartNumber} has no released revision; no documents are defined for it."));
                continue;
            }

            var facts = new ApplicabilityFacts(null, line.PartNumber, revision.Revision, null, null, line.PromisedDate);
            var needs = (await cache.RequirementsAsync(line.PartNumber, revision.Revision, ct)).Where(r => r.RemovedAt is null)
                .Select(r => new DocumentNeed(r.DocumentNumber, r.DocumentRevision, r.OperationSequence, $"{line.PartNumber} rev {revision.Revision}", r.AddedAt));
            var docs = await EvaluateAsync(cache, needs, facts, now, null, principal, ct);
            lines.Add(new PurchaseLineDocumentation(line.LineNumber, line.PartNumber, revision.Revision, docs, Summarize(docs)));
        }

        return new PurchaseDocumentation(po.PoNumber, po.Supplier, lines);
    }

    /// <summary>The documentation of one order, in an open read transaction (shared by readiness and reports).</summary>
    public static async Task<OrderDocumentation> ForOrderAsync(IKernelUnitOfWork uow, OrderInfo order, DateTimeOffset now, ForgePrincipal? viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(order);
        var item = await uow.Store<IEngineeringLookup>().FindItemAsync(order.PartNumber, ct);
        var itemClass = item?.Class ?? ItemClass.Unclassified;
        var documents = uow.Store<IDocumentLookup>();
        var enforced = (await documents.ListPoliciesAsync(ct)).FirstOrDefault(p => p.ItemClass == itemClass)?.Enforced ?? false;
        var customer = order.ProgramId is { } programId ? (await uow.Store<IProgramsLookup>().FindProgramAsync(programId, ct))?.Customer : null;
        var facts = new ApplicabilityFacts(order.OrderNumber, order.PartNumber, order.Revision, order.ProgramId, customer, order.DueDate);
        var cache = new Cache(documents);

        var needs = new List<DocumentNeed>();
        needs.AddRange((await cache.RequirementsAsync(order.PartNumber, order.Revision, ct)).Where(r => r.RemovedAt is null)
            .Select(r => new DocumentNeed(r.DocumentNumber, r.DocumentRevision, r.OperationSequence, r.OperationSequence is { } s ? $"{order.PartNumber} rev {order.Revision}, op {s}" : $"{order.PartNumber} rev {order.Revision}", r.AddedAt)));
        needs.AddRange((await documents.ListLinksAsync(DocumentTargetType.ProductionOrder, order.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), null, false, ct))
            .Where(l => l.Purpose == DocumentPurpose.Follow)
            .Select(l => new DocumentNeed(l.DocumentNumber, l.Revision, null, "Attached to the order", l.LinkedAt)));

        // What the order references: what applied at release, or at its latest documentation reissue.
        var releasedAt = order.Status is OrderStatus.Planned or OrderStatus.FirmPlanned ? null : order.ReleasedAt;
        if (releasedAt is not null)
        {
            var issues = await documents.ListIssuesAsync(order.OrderNumber, ct);
            if (issues.Count > 0 && issues[^1].IssuedAt > releasedAt)
            {
                releasedAt = issues[^1].IssuedAt;
            }
        }

        var results = await EvaluateAsync(cache, needs, facts, now, releasedAt, viewer, ct);
        return new OrderDocumentation(order.OrderNumber, order.PartNumber, order.Revision, itemClass, enforced, results, Summarize(results));
    }

    private static async Task<List<DocumentApplicability>> EvaluateAsync(Cache cache, IEnumerable<DocumentNeed> needs, ApplicabilityFacts facts, DateTimeOffset now, DateTimeOffset? releasedAt, ForgePrincipal? viewer, CancellationToken ct)
    {
        var results = new List<DocumentApplicability>();
        foreach (var need in needs)
        {
            var document = await cache.DocumentAsync(need.DocumentNumber, ct);
            if (document is null)
            {
                continue;
            }

            var rules = await cache.RulesAsync(document.Number, ct);
            var result = ApplicabilityEngine.Evaluate(document, need, rules, facts, now, releasedAt);
            if (viewer is not null && !DocumentAccess.CanSee(viewer, document))
            {
                result = result with
                {
                    DocumentNumber = Restricted,
                    Title = string.Empty,
                    PinnedRevision = null,
                    AtRelease = null,
                    Applicable = null,
                    Explanation = $"A document you are not cleared to see is {Describe(result.State)}.",
                };
            }

            results.Add(result);
        }

        return results.OrderBy(r => r.OperationSequence ?? 0).ThenBy(r => r.DocumentNumber, StringComparer.Ordinal).ToList();
    }

    public static string Describe(DocumentState state) => state switch
    {
        DocumentState.Current => "current",
        DocumentState.Pinned => "pinned to its revision",
        DocumentState.LegitimatelySuperseded => "superseded after release (work in progress continues)",
        DocumentState.Mismatch => "a mismatch",
        DocumentState.Missing => "missing",
        DocumentState.NotReleased => "not released",
        DocumentState.Obsolete => "obsolete",
        _ => "waiting for review",
    };

    private static string Summarize(IReadOnlyList<DocumentApplicability> documents)
    {
        if (documents.Count == 0)
        {
            return "No documents are required.";
        }

        var problems = documents.Where(d => d.Outcome != EvidenceOutcome.Pass).ToList();
        return problems.Count == 0
            ? $"{documents.Count} document(s); all apply and match."
            : $"{documents.Count} document(s); {string.Join(", ", problems.GroupBy(p => p.State).Select(g => $"{g.Count()} {Describe(g.Key)}"))}.";
    }

    private void Demand(ForgePrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of("Documentation"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }

        var engineering = authorizer.Authorize(principal, Permissions.EngineeringRead, ResourceRef.Of("Documentation"));
        if (!engineering.IsPermitted)
        {
            throw new ForgeAuthorizationException(engineering);
        }
    }

    /// <summary>Documents, rules and requirements read once per evaluation.</summary>
    private sealed class Cache(IDocumentLookup lookup)
    {
        private readonly Dictionary<string, DocumentInfo?> documents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<ApplicabilityRuleInfo>> rules = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), IReadOnlyList<DocumentRequirementInfo>> requirements = [];

        public async Task<DocumentInfo?> DocumentAsync(string number, CancellationToken ct)
        {
            if (!documents.TryGetValue(number, out var document))
            {
                document = await lookup.FindDocumentAsync(number, ct);
                documents[number] = document;
            }

            return document;
        }

        public async Task<IReadOnlyList<ApplicabilityRuleInfo>> RulesAsync(string number, CancellationToken ct)
        {
            if (!rules.TryGetValue(number, out var list))
            {
                list = await lookup.ListRulesAsync(number, true, ct);
                rules[number] = list;
            }

            return list;
        }

        public async Task<IReadOnlyList<DocumentRequirementInfo>> RequirementsAsync(string part, string revision, CancellationToken ct)
        {
            if (!requirements.TryGetValue((part, revision), out var list))
            {
                list = await lookup.ListRequirementsAsync(part, revision, true, ct);
                requirements[(part, revision)] = list;
            }

            return list;
        }
    }
}
