using System.Globalization;
using System.Text.RegularExpressions;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Commands;
using Forge.Procurement.Contracts;
using Forge.Planning.Engines;
using Forge.Planning.Analyst;
using Forge.Planning.Reporting;

namespace Forge.Planning;

/// <summary>Current source access for display copies. Allocation keys and recorded engine bytes stay authoritative.</summary>
public sealed class MaterialSourceVisibility
{
    public const string Restricted = "Restricted program work";
    private static readonly Regex OrderReference = new(@"\b(?:Production\s+)?Order\s+(?<id>\d+)(?:\s+\([^)]*\))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KeyReference = new(@"\bWO(?<id>\d+)(?:/[A-Za-z0-9._:-]+)?\b", RegexOptions.CultureInvariant);
    private static readonly Regex LinkReference = new(@"(?<![\w-])order:(?<id>\d+)(?:/\d+)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PurchaseReference = new(@"\bPO\s*(?<id>\d+)(?!\d)(?:\s+line\s+\d+\s*\([^)]*\)|/\d+)?", RegexOptions.CultureInvariant);
    private static readonly Regex PurchaseLink = new(@"\b(?:po|purchase-order):(?<id>\d+)(?:/\d+)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MaterialOwner = new(@"^Order (?<id>\d+):", RegexOptions.CultureInvariant);
    private static readonly Regex RestrictedMetadata = new(@"Restricted program work (?:Planned|FirmPlanned), rev [^\s,;]+ line \d+", RegexOptions.CultureInvariant);
    private readonly HashSet<long> visible;
    private readonly HashSet<long> readableOrders;
    private readonly HashSet<long> visiblePurchases;
    private readonly IReadOnlyList<string> protectedPurchaseDescriptions;
    public IReadOnlyList<PurchaseOrderInfo> PurchaseOrders { get; }
    public IReadOnlyDictionary<long, IReadOnlyList<ResourceRef>> PurchaseResources { get; }

    public MaterialSourceVisibility(ForgePrincipal principal, IReadOnlyList<OrderInfo> headers,
        IReadOnlyList<PurchaseOrderInfo>? purchases = null, IReadOnlyDictionary<long, IReadOnlyList<ResourceRef>>? purchaseResources = null, IForgeAuthorizer? authorizer = null)
    {
        visible = headers.Where(o => o.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).Select(o => o.OrderNumber).ToHashSet();
        PurchaseOrders = purchases ?? [];
        PurchaseResources = purchaseResources ?? new Dictionary<long, IReadOnlyList<ResourceRef>>();
        var sourceAuthorizer = authorizer ?? new ForgeAuthorizer();
        readableOrders = headers.Where(o => sourceAuthorizer.Authorize(principal, Permissions.OrdersRead,
            new ResourceRef("ProductionOrder", o.OrderNumber.ToString(CultureInfo.InvariantCulture), o.ProgramId is { } p ? new ProgramId(p) : null,
                ContractId: o.ContractId?.ToString(CultureInfo.InvariantCulture))).IsPermitted)
            .Select(o => o.OrderNumber).ToHashSet();
        visiblePurchases = PurchaseResources.Where(p => p.Value.All(r => sourceAuthorizer.Authorize(principal, Permissions.PurchasingRead, r).IsPermitted)).Select(p => p.Key).ToHashSet();
        protectedPurchaseDescriptions = PurchaseOrders.Where(p => !CanSeePurchaseOrder(p.PoNumber)).SelectMany(po => po.Lines.Select(line => $"PO {po.PoNumber} line {line.LineNumber} ({po.Supplier})")).ToArray();
    }

    public static async Task<MaterialSourceVisibility> LoadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct,
        IReadOnlyList<OrderInfo>? headers = null, IForgeAuthorizer? authorizer = null)
    {
        headers ??= await uow.Store<IExecutionLookup>().ListOrdersAsync(ct);
        var purchases = await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct);
        var resources = new Dictionary<long, IReadOnlyList<ResourceRef>>();
        foreach (var po in purchases)
        {
            try { resources[po.PoNumber] = await PurchaseOrderAuthority.ResourcesAsync(uow, po, ct); }
            catch (CommandRejectedException) { /* Missing recorded sourcing is never disclosed as an unscoped PO. */ }
        }
        return new(principal, headers, purchases, resources, authorizer);
    }

    public bool CanSeeOrder(long number) => visible.Contains(number);
    public bool CanSeePurchaseOrder(long number) => visiblePurchases.Contains(number);

    public string Text(string value)
    {
        foreach (var description in protectedPurchaseDescriptions) value = value.Replace(description, Restricted, StringComparison.Ordinal);
        string Replace(Match match) => long.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && readableOrders.Contains(id)
            ? match.Value : Restricted;
        string ReplacePurchase(Match match) => long.TryParse(match.Groups["id"].Value, out var id) && visiblePurchases.Contains(id) ? match.Value : Restricted;
        return RestrictedMetadata.Replace(PurchaseLink.Replace(PurchaseReference.Replace(LinkReference.Replace(KeyReference.Replace(OrderReference.Replace(value, Replace), Replace), Replace), ReplacePurchase), ReplacePurchase), Restricted);
    }

    public bool HasRestrictedReference(string? value) => value is not null && Text(value) != value;

    public EvidenceNode Evidence(EvidenceNode value, bool material = false) => new(value.Check, value.Outcome, Text(value.Detail))
    {
        Inputs = new SortedDictionary<string, string>(value.Inputs.ToDictionary(p => p.Key, p => Text(p.Value)), StringComparer.Ordinal),
        RuleRef = value.RuleRef is null ? null : Text(value.RuleRef),
        Children = value.Children.Where(n => !material || n.Check != "OrderMaterial" || MaterialNodeVisible(n.Detail))
            .Select(n => Evidence(n, material)).ToArray(),
    };

    private bool MaterialNodeVisible(string detail)
    {
        var match = MaterialOwner.Match(detail);
        return !match.Success || long.TryParse(match.Groups["id"].Value, out var number) && CanSeeOrder(number);
    }

    public OrderMaterialStatus Material(OrderMaterialStatus value) => value with
    {
        Summary = Text(value.Summary),
        Lines = value.Lines.Select(l => l with
        {
            Allocations = l.Allocations.Select(a => a with { Source = Text(a.Source), Description = Text(a.Description) }).ToArray(),
        }).ToArray(),
    };

    /// <summary>Safe explanations for read calculations, retaining every source key and every demand row.</summary>
    public IReadOnlyList<OrderMaterialStatus> ForEvaluation(IReadOnlyList<OrderMaterialStatus> values) => values.Select(value => value with
    {
        Summary = Text(value.Summary),
        Lines = value.Lines.Select(l => l with { Allocations = l.Allocations.Select(a => a with { Description = Text(a.Description) }).ToArray() }).ToArray(),
    }).ToArray();

    public EngineRun<IReadOnlyList<OrderMaterialStatus>> Material(EngineRun<IReadOnlyList<OrderMaterialStatus>> value)
    {
        var output = value.Output.Where(o => CanSeeOrder(o.OrderNumber)).Select(Material).ToArray();
        var projected = Evidence(value.Evidence, true);
        var evidence = new EvidenceNode(projected.Check,
            output.All(o => o.State == MaterialState.Ready) ? EvidenceOutcome.Pass : EvidenceOutcome.Fail,
            $"{output.Length} released order(s): {output.Count(o => o.State == MaterialState.Ready)} ready, {output.Count(o => o.State == MaterialState.Expected)} waiting for receipts, {output.Count(o => o.State == MaterialState.Shortfall)} short.")
        {
            Inputs = projected.Inputs, RuleRef = projected.RuleRef, Children = projected.Children,
        };
        return value with { Output = output, Evidence = evidence };
    }

    public ScheduleResult Schedule(ScheduleResult value) => value with
    {
        Orders = value.Orders.Where(o => CanSeeOrder(o.OrderNumber)).Select(o => o with { Explanation = Text(o.Explanation) }).ToArray(),
        Operations = value.Operations.Where(o => CanSeeOrder(o.OrderNumber)).Select(o => o with { BindingDetail = Text(o.BindingDetail) }).ToArray(),
    };

    public ConstraintRecord Constraint(ConstraintRecord value) => value with
    {
        Key = Text(value.Key), Subject = Text(value.Subject), Finding = Text(value.Finding), Evidence = Text(value.Evidence),
        Alternatives = value.Alternatives.Select(Text).ToArray(),
        Resolution = value.Resolution is null ? null : Text(value.Resolution),
        AffectedOrders = value.AffectedOrders.Where(CanSeeOrder).ToArray(),
        AffectedOperations = value.AffectedOperations.Where(o => long.TryParse(o.Split('/')[0], out var n) && CanSeeOrder(n)).ToArray(),
    };

    public ReportRow Row(ReportRow value) => value with
    {
        Cells = value.Cells.Select(c => c is string text ? Text(text) : c).ToArray(),
        Trace = value.Trace.Select(Text).ToArray(),
    };

    public AnalystAnswer Answer(AnalystAnswer value) => value with
    {
        Headline = Text(value.Headline), Facts = value.Facts.Select(Fact).ToArray(),
        Rows = value.Rows?.Select(Row).ToArray(), Notes = value.Notes.Select(Text).ToArray(), Sources = value.Sources.Select(Text).ToArray(),
        Narrative = value.Narrative is null ? null : Text(value.Narrative),
        Brief = value.Brief is null ? null : value.Brief with
        {
            Headlines = value.Brief.Headlines.Select(Text).ToArray(),
            Sections = value.Brief.Sections.Select(s => s with { Facts = s.Facts.Select(Fact).ToArray(), Note = s.Note is null ? null : Text(s.Note) }).ToArray(),
        },
    };

    private AnalystFact Fact(AnalystFact value) => value with { Text = Text(value.Text), Trace = value.Trace is null ? null : Text(value.Trace) };

    public HorizonComparison Comparison(HorizonComparison value) => value with
    {
        Orders = value.Orders.Where(o => CanSeeOrder(o.OrderNumber)).Select(o => o with
        {
            Explanation = Text(o.Explanation),
            Operations = o.Operations.Select(op => op with { Explanation = Text(op.Explanation) }).ToArray(),
            Evidence = o.Evidence.Select(e => e with { Text = Text(e.Text), Trace = Text(e.Trace) }).ToArray(),
        }).ToArray(),
        SiteEvidence = value.SiteEvidence.Select(e => e with { Text = Text(e.Text), Trace = Text(e.Trace) }).ToArray(),
    };

    public EngineRun<MrpResult> Mrp(EngineRun<MrpResult> value)
    {
        var protectedSubstitutions = (value.Output.Substitutions ?? []).Where(s => HasRestrictedReference(s.RequirementKey)).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        bool ProtectedKey(string key) => HasRestrictedReference(key) || protectedSubstitutions.Contains(key);
        EvidenceNode ProjectEvidence(EvidenceNode node) => Evidence(node) with
        {
            Children = node.Children.Where(n => n.Check != "Substitution" || !protectedSubstitutions.Any(key => n.Detail.StartsWith(key + ":", StringComparison.Ordinal)))
                .Select(ProjectEvidence).ToArray(),
        };
        return value with
        {
        Output = value.Output with
        {
            Items = value.Output.Items.Select(i => i with
            {
                Requirements = i.Requirements.Select(r => r with { Key = ProtectedKey(r.Key) ? Restricted : Text(r.Key), Description = ProtectedKey(r.Key) ? Restricted : Text(r.Description), Line = ProtectedKey(r.Key) ? null : r.Line }).ToArray(),
                Receipts = i.Receipts.Select(r => r with { Key = Text(r.Key), Description = Text(r.Description) }).ToArray(),
            }).ToArray(),
            PlannedOrders = value.Output.PlannedOrders.Select(o => o with
            {
                Explanation = Text(o.Explanation),
                Pegging = o.Pegging.Select(p => p with { RequirementKey = ProtectedKey(p.RequirementKey) ? Restricted : Text(p.RequirementKey), Description = ProtectedKey(p.RequirementKey) ? Restricted : Text(p.Description) }).ToArray(),
            }).ToArray(),
            Exceptions = value.Output.Exceptions.Select(e => e with
            {
                Detail = e.Kind == "AlternateSuggested" && Text(e.Detail).Contains(Restricted, StringComparison.Ordinal)
                    ? Restricted + " requires source approval." : Text(e.Detail),
            }).ToArray(),
            Substitutions = value.Output.Substitutions?.Where(s => !HasRestrictedReference(s.RequirementKey))
                .Select(s => s with { Key = Text(s.Key), RequirementKey = Text(s.RequirementKey), Explanation = Text(s.Explanation) }).ToArray(),
        },
        Evidence = ProjectEvidence(value.Evidence),
        };
    }
}
