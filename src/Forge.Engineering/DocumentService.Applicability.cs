using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Engineering;

/// <summary>Adds a document an item revision is built to. On a released item revision a reason is required.</summary>
public sealed record AddDocumentRequirement(string PartNumber, string Revision, string DocumentNumber, string? DocumentRevision, int? OperationSequence, string? Note);

public sealed record RemoveDocumentRequirement(long RequirementId);

/// <summary>This revision of the document applies when the condition holds. Order rules are deviations and need a reference.</summary>
public sealed record AddApplicabilityRule(
    string DocumentNumber,
    string Revision,
    ApplicabilityCondition Condition,
    Guid? ProgramId,
    string? Customer,
    DateOnly? DueFrom,
    DateOnly? DueTo,
    long? OrderNumber,
    string? Reference,
    string? Note);

public sealed record WithdrawApplicabilityRule(long RuleId);

/// <summary>Turns the documentation readiness dimension on or off for an item class.</summary>
public sealed record SetDocumentationEnforcement(ItemClass ItemClass, bool Enforced);

/// <summary>Reissues a released order's documentation: the order then references the revisions that apply now.</summary>
public sealed record ReissueOrderDocumentation(long OrderNumber);

public sealed record OrderDocumentationReissued(long OrderNumber, long IssueId) : IDomainEvent
{
    public static string EventType => nameof(OrderDocumentationReissued);

    public static int SchemaVersion => 1;
}

public sealed record DocumentRequirementChanged(long RequirementId, string PartNumber, string Revision, string DocumentNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(DocumentRequirementChanged);

    public static int SchemaVersion => 1;
}

public sealed record ApplicabilityRuleChanged(long RuleId, string DocumentNumber, string Revision, string Condition, string Change) : IDomainEvent
{
    public static string EventType => nameof(ApplicabilityRuleChanged);

    public static int SchemaVersion => 1;
}

public sealed record DocumentationPolicyChanged(string ItemClass, bool Enforced) : IDomainEvent
{
    public static string EventType => nameof(DocumentationPolicyChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Applicability (V2.3): which documents an item revision is built to, the rules that choose a document
/// revision for some orders, and whether documentation blocks readiness for an item class. Date
/// effectivity stays the default; rules are exceptions recorded as data with who, when and why.
/// </summary>
public sealed partial class DocumentService
{
    private const string RequirementEntity = "DocumentRequirement";
    private const string RuleEntity = "ApplicabilityRule";
    private const string PolicyEntity = "DocumentationPolicy";

    public Task<CommandResult> AddRequirementAsync(AddDocumentRequirement c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var itemRevision = BusinessCode.Require(c.Revision, "Revision");
        var number = Number(c.DocumentNumber);
        var spec = new CommandSpec(SourceContext, nameof(AddDocumentRequirement), Permissions.DocumentsManage, ResourceRef.Of(RequirementEntity, $"{part}/{itemRevision}"));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var detail = await uow.Store<IEngineeringLookup>().GetRevisionDetailAsync(part, itemRevision, ct)
                ?? throw new CommandRejectedException("revision_not_found", $"{part} has no revision {itemRevision}.");
            if (detail.Revision.Status == RevisionStatus.Released && string.IsNullOrWhiteSpace(context.Reason))
            {
                throw new CommandRejectedException("reason_required", $"{part} rev {itemRevision} is released; give a reason for adding a document to it.");
            }

            var document = await VisibleDocumentAsync(uow, number, context, ct);
            if (document.Authority == DocumentAuthority.Reference)
            {
                throw new CommandRejectedException("reference_not_authority", $"{number} is a reference document; an item is never built to one.");
            }

            string? pinned = null;
            if (!string.IsNullOrWhiteSpace(c.DocumentRevision))
            {
                pinned = Revision(c.DocumentRevision);
                if (document.Revisions.All(r => r.Revision != pinned))
                {
                    throw new CommandRejectedException("revision_not_found", $"{number} has no revision {pinned}.");
                }
            }

            if (c.OperationSequence is { } sequence && detail.Routing.All(o => o.Sequence != sequence))
            {
                throw new CommandRejectedException("unknown_operation", $"{part} rev {itemRevision} has no operation {sequence}.");
            }

            var requirement = new DocumentRequirementInfo(0, part, itemRevision, number, pinned, c.OperationSequence, Optional(c.Note, 500), context.Actor, now, null, null, null);
            var id = await uow.Store<IDocumentStore>().InsertRequirementAsync(requirement, ct);
            if (id == 0)
            {
                throw new CommandRejectedException("requirement_exists", $"{part} rev {itemRevision} already requires {number}{(c.OperationSequence is { } s ? $" at operation {s}" : string.Empty)}.");
            }

            var key = id.ToString(CultureInfo.InvariantCulture);
            var scope = c.OperationSequence is { } op ? $"operation {op} of {part} rev {itemRevision}" : $"{part} rev {itemRevision}";
            return new CommandOutcome(
                CommandResult.Ok(key, $"{scope} is now built to {number}{(pinned is null ? " (the revision that applies)" : " rev " + pinned)}."),
                [pipeline.Event(new DocumentRequirementChanged(id, part, itemRevision, number, "Added"), context, SourceContext, RequirementEntity, key, null, document.Markings)],
                new AuditChange(RequirementEntity, key, null, CanonicalJson.Serialize(requirement with { RequirementId = id })));
        }, ct);
    }

    public Task<CommandResult> RemoveRequirementAsync(RemoveDocumentRequirement c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.RequirementId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(RemoveDocumentRequirement), Permissions.DocumentsManage, ResourceRef.Of(RequirementEntity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var requirement = (await uow.Store<IDocumentLookup>().ListRequirementsAsync(null, null, true, ct)).FirstOrDefault(r => r.RequirementId == c.RequirementId)
                ?? throw new CommandRejectedException("requirement_not_found", $"Requirement {c.RequirementId} does not exist.");
            var document = await VisibleDocumentAsync(uow, requirement.DocumentNumber, context, ct);
            if (requirement.RemovedAt is not null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, "Already removed."));
            }

            await uow.Store<IDocumentStore>().RemoveRequirementAsync(requirement.RequirementId, now, context.Actor, context.Reason!, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{requirement.PartNumber} rev {requirement.Revision} is no longer built to {requirement.DocumentNumber}. The requirement stays in its history."),
                [pipeline.Event(new DocumentRequirementChanged(requirement.RequirementId, requirement.PartNumber, requirement.Revision, requirement.DocumentNumber, "Removed"), context, SourceContext, RequirementEntity, key, null, document.Markings)],
                new AuditChange(RequirementEntity, key, CanonicalJson.Serialize(requirement), CanonicalJson.Serialize(requirement with { RemovedBy = context.Actor, RemovedAt = now, RemovedReason = context.Reason })));
        }, ct);
    }

    public Task<CommandResult> AddRuleAsync(AddApplicabilityRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var number = Number(c.DocumentNumber);
        var revision = Revision(c.Revision);
        var spec = new CommandSpec(SourceContext, nameof(AddApplicabilityRule), Permissions.DocumentsRelease, ResourceRef.Of(RuleEntity, number), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var document = await VisibleDocumentAsync(uow, number, context, ct);
            if (document.Authority == DocumentAuthority.Reference)
            {
                throw new CommandRejectedException("reference_not_authority", $"{number} is a reference document; it never applies.");
            }

            var target = document.Revisions.FirstOrDefault(r => r.Revision == revision)
                ?? throw new CommandRejectedException("revision_not_found", $"{number} has no revision {revision}.");
            if (target.Status is not (DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable or DocumentRevisionStatus.Superseded))
            {
                throw new CommandRejectedException("revision_not_released", $"Revision {revision} is {target.Status}; a rule can only select a released or applicable revision.");
            }

            Guid? programId = null;
            string? customer = null;
            DateOnly? from = null, to = null;
            long? orderNumber = null;
            string? reference = Optional(c.Reference, 100);
            string description;
            switch (c.Condition)
            {
                case ApplicabilityCondition.Program:
                    var program = await VisibleTargetOrNullAsync(uow, context.Principal, DocumentTargetType.Program, c.ProgramId?.ToString() ?? string.Empty, ct)
                        ?? throw new CommandRejectedException("program_not_found", "Choose a program you have access to.");
                    programId = program.ProgramId;
                    description = $"orders for {program.Label}";
                    break;
                case ApplicabilityCondition.Customer:
                    customer = Optional(c.Customer, 200) ?? throw new CommandRejectedException("customer_required", "Name the customer.");
                    description = $"orders for customer {customer}";
                    break;
                case ApplicabilityCondition.DueDate:
                    (from, to) = (c.DueFrom, c.DueTo);
                    if (from is null && to is null)
                    {
                        throw new CommandRejectedException("dates_required", "Give the first or last due date (or both).");
                    }

                    if (from is { } f && to is { } t && t < f)
                    {
                        throw new CommandRejectedException("invalid_dates", "The last due date is before the first.");
                    }

                    description = $"orders due {(from is null ? "until" : $"from {from:yyyy-MM-dd}")}{(from is not null && to is not null ? " to" : string.Empty)}{(to is null ? " onwards" : $" {to:yyyy-MM-dd}")}";
                    break;
                case ApplicabilityCondition.Order:
                    var order = await VisibleTargetOrNullAsync(uow, context.Principal, DocumentTargetType.ProductionOrder, c.OrderNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, ct)
                        ?? throw new CommandRejectedException("order_not_found", $"Order {c.OrderNumber} does not exist.");
                    orderNumber = long.Parse(order.Id, CultureInfo.InvariantCulture);
                    if (reference is null)
                    {
                        throw new CommandRejectedException("reference_required", "An order rule is a deviation: give the deviation or concession number.");
                    }

                    description = $"{order.Label} under {reference}";
                    break;
                default:
                    throw new CommandRejectedException("invalid_condition", "Unknown condition.");
            }

            if (document.ProgramId is { } owner && programId is { } chosen && owner != chosen)
            {
                throw new CommandRejectedException("program_mismatch", $"{number} belongs to another program.");
            }

            var rule = new ApplicabilityRuleInfo(0, number, revision, c.Condition, programId, customer, from, to, orderNumber, reference, Optional(c.Note, 500), context.Actor, now, null, null, null);
            var id = await uow.Store<IDocumentStore>().InsertRuleAsync(rule, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{number} rev {revision} now applies to {description}."),
                [pipeline.Event(new ApplicabilityRuleChanged(id, number, revision, c.Condition.ToString(), "Added"), context, SourceContext, RuleEntity, key, Program(programId), document.Markings)],
                new AuditChange(RuleEntity, key, null, CanonicalJson.Serialize(rule with { RuleId = id }), Program(programId)));
        }, ct);
    }

    public Task<CommandResult> WithdrawRuleAsync(WithdrawApplicabilityRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.RuleId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(WithdrawApplicabilityRule), Permissions.DocumentsRelease, ResourceRef.Of(RuleEntity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var rule = (await uow.Store<IDocumentLookup>().ListRulesAsync(null, true, ct)).FirstOrDefault(r => r.RuleId == c.RuleId)
                ?? throw new CommandRejectedException("rule_not_found", $"Rule {c.RuleId} does not exist.");
            var document = await VisibleDocumentAsync(uow, rule.DocumentNumber, context, ct);
            if (rule.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p)))
            {
                throw new CommandRejectedException("rule_not_found", $"Rule {c.RuleId} does not exist.");
            }

            if (rule.WithdrawnAt is not null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, "Already withdrawn."));
            }

            await uow.Store<IDocumentStore>().WithdrawRuleAsync(rule.RuleId, now, context.Actor, context.Reason!, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Rule {rule.RuleId} for {rule.DocumentNumber} rev {rule.Revision} withdrawn. Date effectivity decides again where no other rule does."),
                [pipeline.Event(new ApplicabilityRuleChanged(rule.RuleId, rule.DocumentNumber, rule.Revision, rule.Condition.ToString(), "Withdrawn"), context, SourceContext, RuleEntity, key, Program(rule.ProgramId), document.Markings)],
                new AuditChange(RuleEntity, key, CanonicalJson.Serialize(rule), CanonicalJson.Serialize(rule with { WithdrawnBy = context.Actor, WithdrawnAt = now, WithdrawnReason = context.Reason }), Program(rule.ProgramId)));
        }, ct);
    }

    public Task<CommandResult> ReissueAsync(ReissueOrderDocumentation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.OrderNumber.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ReissueOrderDocumentation), Permissions.OrdersRelease, ResourceRef.Of("OrderDocumentation", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var order = await VisibleTargetOrNullAsync(uow, context.Principal, DocumentTargetType.ProductionOrder, key, ct)
                ?? throw new CommandRejectedException("order_not_found", $"Order {c.OrderNumber} does not exist.");
            var id = await uow.Store<IDocumentStore>().InsertIssueAsync(c.OrderNumber, now, context.Actor, context.Reason!, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{order.Label}: documentation reissued. The order now references the revisions that apply now."),
                [pipeline.Event(new OrderDocumentationReissued(c.OrderNumber, id), context, SourceContext, "OrderDocumentation", key, Program(order.ProgramId))],
                new AuditChange("OrderDocumentation", key, null, CanonicalJson.Serialize(new { IssueId = id, IssuedAt = now }), Program(order.ProgramId)));
        }, ct);
    }

    public Task<CommandResult> SetEnforcementAsync(SetDocumentationEnforcement c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(SetDocumentationEnforcement), Permissions.DocumentsRelease, ResourceRef.Of(PolicyEntity, c.ItemClass.ToString()), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var current = (await uow.Store<IDocumentLookup>().ListPoliciesAsync(ct)).FirstOrDefault(p => p.ItemClass == c.ItemClass);
            if ((current?.Enforced ?? false) == c.Enforced)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(c.ItemClass.ToString(), $"Documentation is already {(c.Enforced ? "enforced" : "not enforced")} for {c.ItemClass} items."));
            }

            await uow.Store<IDocumentStore>().SetPolicyAsync(c.ItemClass, c.Enforced, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(c.ItemClass.ToString(), c.Enforced
                    ? $"Documentation now blocks readiness for {c.ItemClass} items: a mismatched, missing or unreleased document stops the operation."
                    : $"Documentation no longer blocks readiness for {c.ItemClass} items; it is shown as not evaluated."),
                [pipeline.Event(new DocumentationPolicyChanged(c.ItemClass.ToString(), c.Enforced), context, SourceContext, PolicyEntity, c.ItemClass.ToString())],
                new AuditChange(PolicyEntity, c.ItemClass.ToString(), CanonicalJson.Serialize(new { Enforced = current?.Enforced ?? false }), CanonicalJson.Serialize(new { c.Enforced })));
        }, ct);
    }
}
