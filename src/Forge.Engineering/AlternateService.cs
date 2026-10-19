using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Engineering;

public sealed record ProposeAlternate(
    string PrimaryPart,
    string AlternatePart,
    AlternateKind Kind,
    decimal Factor,
    Guid? ProgramId,
    string? Customer,
    string? ParentPart,
    string? AssetCode,
    string? MachineCode,
    int? OperationSequence,
    string? SupplierCode,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? QualityNotes,
    string? Evidence,
    string? ChangeNumber);

public sealed record ApproveAlternate(long AlternateId);

public sealed record RejectAlternate(long AlternateId);

public sealed record ExpireAlternate(long AlternateId);

public sealed record SetSubstitutionPolicy(string PartNumber, string Revision, int LineNumber, SubstitutionPolicy Policy);

public sealed record AlternateChanged(long AlternateId, string PrimaryPart, string AlternatePart, string Change) : IDomainEvent
{
    public static string EventType => nameof(AlternateChanged);

    public static int SchemaVersion => 1;
}

public sealed record SubstitutionPolicyChanged(string PartNumber, string Revision, int LineNumber, string Policy) : IDomainEvent
{
    public static string EventType => nameof(SubstitutionPolicyChanged);

    public static int SchemaVersion => 1;
}

public interface IAlternateStore
{
    Task<long> InsertAlternateAsync(AlternateInfo alternate, CancellationToken ct);

    Task UpdateAlternateAsync(AlternateInfo alternate, CancellationToken ct);

    Task InsertLinePolicyAsync(string part, string revision, int line, SubstitutionPolicy policy, string actor, DateTimeOffset at, string reason, CancellationToken ct);
}

/// <summary>
/// Controlled alternates (V2.6). Anyone with propose authority records a relationship as Proposed — never
/// usable by MRP, issue or readiness. A different person with the matching authority (engineering, or
/// maintenance for maintenance-only alternates) approves or rejects it; approved relationships can be
/// expired. What was proposed never changes: a different relationship is proposed instead.
/// </summary>
public sealed class AlternateService(CommandPipeline pipeline, ISiteClock siteClock)
{
    public const string SourceContext = EngineeringService.SourceContext;
    private const string Entity = "Alternate";

    /// <summary>The authority that decides a kind of alternate.</summary>
    public static string ApproverPermission(AlternateKind kind) =>
        kind == AlternateKind.MaintenanceOnly ? Permissions.MaintenanceAlternatesApprove : Permissions.AlternatesApprove;

    public Task<CommandResult> ProposeAsync(ProposeAlternate c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var program = c.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        var spec = new CommandSpec(SourceContext, nameof(ProposeAlternate), Permissions.AlternatesPropose, new ResourceRef(Entity, null, program));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var engineering = uow.Store<IEngineeringLookup>();
            var primary = BusinessCode.Require(c.PrimaryPart, "Primary part");
            var alternate = BusinessCode.Require(c.AlternatePart, "Alternate part");
            if (primary == alternate)
            {
                throw new CommandRejectedException("same_part", "A part is not an alternate for itself.");
            }

            var primaryItem = await engineering.FindItemAsync(primary, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {primary} does not exist.");
            var alternateItem = await engineering.FindItemAsync(alternate, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {alternate} does not exist.");
            if (c.Factor <= 0 || c.Factor > 10_000)
            {
                throw new CommandRejectedException("invalid_factor", "The quantity conversion is how many alternate units replace one primary unit: above zero, at most 10,000.");
            }

            if (primaryItem.UnitOfMeasure != alternateItem.UnitOfMeasure && c.Factor == 1)
            {
                throw new CommandRejectedException("conversion_required", $"{primary} is counted in {primaryItem.UnitOfMeasure} and {alternate} in {alternateItem.UnitOfMeasure}: state the conversion.");
            }

            var today = siteClock.DateAt(now);
            if (c.ValidTo is { } to && (to < today || (c.ValidFrom is { } from && to < from)))
            {
                throw new CommandRejectedException("invalid_dates", "The end date is in the past or before the start date.");
            }

            if (c.Kind == AlternateKind.Temporary && c.ValidTo is null)
            {
                throw new CommandRejectedException("end_date_required", "A temporary alternate needs an end date.");
            }

            if (c.Kind == AlternateKind.ProgramSpecific && c.ProgramId is null)
            {
                throw new CommandRejectedException("program_required", "A program-specific alternate names its program.");
            }

            var supplier = string.IsNullOrWhiteSpace(c.SupplierCode) ? null : c.SupplierCode.Trim().ToUpperInvariant();
            if (c.Kind == AlternateKind.SupplierSpecific && supplier is null)
            {
                throw new CommandRejectedException("supplier_required", "A supplier-specific alternate names its supplier.");
            }

            string? parent = null;
            if (!string.IsNullOrWhiteSpace(c.ParentPart))
            {
                parent = BusinessCode.Require(c.ParentPart, "Parent part");
                if ((await uow.Store<IEngineeringStore>().ListBomEdgesAsync(ct)).All(e => e.Parent != parent || e.Component != primary))
                {
                    throw new CommandRejectedException("not_in_bom", $"No bill of materials of {parent} uses {primary}.");
                }
            }

            var change = string.IsNullOrWhiteSpace(c.ChangeNumber) ? null : c.ChangeNumber.Trim().ToUpperInvariant();
            if (change is not null && await uow.Store<IChangeLookup>().FindChangeAsync(change, ct) is null)
            {
                throw new CommandRejectedException("change_not_found", $"Engineering change {change} does not exist.");
            }

            var info = new AlternateInfo(0, primary, alternate, c.Kind, AlternateStatus.Proposed, c.Factor, c.ProgramId, Optional(c.Customer, 200), parent, Code(c.AssetCode), Code(c.MachineCode),
                c.OperationSequence, supplier, c.ValidFrom, c.ValidTo, Optional(c.QualityNotes, 2000), Optional(c.Evidence, 2000), change, context.Actor, now, null, null, null, null, null, null);
            var open = (await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct))
                .FirstOrDefault(x => x.Status is AlternateStatus.Proposed or AlternateStatus.Approved && Same(x, info));
            if (open is not null)
            {
                throw new CommandRejectedException("alternate_exists", $"Relationship {open.AlternateId} already says this ({open.Status}).");
            }

            var id = await uow.Store<IAlternateStore>().InsertAlternateAsync(info, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Alternate {id}: {alternate} for {primary} ({Alternates.Label(c.Kind)}, {c.Factor:0.######} per 1, {info.Conditions}) proposed. It cannot be used until someone else with authority approves it."),
                [pipeline.Event(new AlternateChanged(id, primary, alternate, "Proposed"), context, SourceContext, Entity, key, program)],
                new AuditChange(Entity, key, null, CanonicalJson.Serialize(info with { AlternateId = id }), program));
        }, ct);
    }

    public Task<CommandResult> ApproveAsync(ApproveAlternate c, AlternateKind kind, CommandContext context, CancellationToken ct) =>
        DecideAsync(nameof(ApproveAlternate), c?.AlternateId ?? 0, c!, kind, context, false, (a, now) =>
        {
            if (a.Status != AlternateStatus.Proposed)
            {
                throw new CommandRejectedException("invalid_status", $"Alternate {a.AlternateId} is {a.Status}; only a proposed alternate is approved.");
            }

            if (a.ValidTo is { } to && to < siteClock.DateAt(now))
            {
                throw new CommandRejectedException("invalid_dates", $"Alternate {a.AlternateId} ended {to:yyyy-MM-dd}; propose a new one.");
            }

            return (a with { Status = AlternateStatus.Approved, DecidedBy = context.Actor, DecidedAt = now, DecisionReason = context.Reason }, "Approved",
                $"Alternate {a.AlternateId} approved: {a.AlternatePart} may stand in for {a.PrimaryPart} ({a.Conditions}).");
        }, ct);

    public Task<CommandResult> RejectAsync(RejectAlternate c, AlternateKind kind, CommandContext context, CancellationToken ct) =>
        DecideAsync(nameof(RejectAlternate), c?.AlternateId ?? 0, c!, kind, context, true, (a, now) =>
            a.Status != AlternateStatus.Proposed
                ? throw new CommandRejectedException("invalid_status", $"Alternate {a.AlternateId} is {a.Status}; only a proposed alternate is rejected.")
                : (a with { Status = AlternateStatus.Rejected, DecidedBy = context.Actor, DecidedAt = now, DecisionReason = context.Reason }, "Rejected", $"Alternate {a.AlternateId} rejected."),
            ct);

    public Task<CommandResult> ExpireAsync(ExpireAlternate c, AlternateKind kind, CommandContext context, CancellationToken ct) =>
        DecideAsync(nameof(ExpireAlternate), c?.AlternateId ?? 0, c!, kind, context, true, (a, now) =>
            a.Status is not (AlternateStatus.Proposed or AlternateStatus.Approved)
                ? throw new CommandRejectedException("invalid_status", $"Alternate {a.AlternateId} is {a.Status}.")
                : (a with { Status = AlternateStatus.Expired, ExpiredBy = context.Actor, ExpiredAt = now, ExpiredReason = context.Reason }, "Expired",
                    $"Alternate {a.AlternateId} expired: {a.AlternatePart} is no longer used for {a.PrimaryPart}. Stock already issued keeps its record."),
            ct);

    public Task<CommandResult> SetPolicyAsync(SetSubstitutionPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var revision = (c.Revision ?? string.Empty).Trim().ToUpperInvariant();
        var key = $"{part}:{revision}/{c.LineNumber.ToString(CultureInfo.InvariantCulture)}";
        var spec = new CommandSpec(SourceContext, nameof(SetSubstitutionPolicy), Permissions.AlternatesApprove, ResourceRef.Of("BomLinePolicy", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var detail = await uow.Store<IEngineeringLookup>().GetRevisionDetailAsync(part, revision, ct)
                ?? throw new CommandRejectedException("revision_not_found", $"{part} has no revision {revision}.");
            var line = detail.Bom.FirstOrDefault(l => l.LineNumber == c.LineNumber)
                ?? throw new CommandRejectedException("line_not_found", $"{part} rev {revision} has no BOM line {c.LineNumber}.");
            if (line.Policy == c.Policy)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, $"Line {c.LineNumber} already {c.Policy}."));
            }

            await uow.Store<IAlternateStore>().InsertLinePolicyAsync(part, revision, c.LineNumber, c.Policy, context.Actor, now, context.Reason!, ct);
            var what = c.Policy switch
            {
                SubstitutionPolicy.Automatic => "MRP substitutes approved equivalents automatically, up to the shortage",
                SubstitutionPolicy.Suggest => "MRP suggests approved alternates, never substitutes",
                _ => "MRP never considers alternates",
            };
            return new CommandOutcome(
                CommandResult.Ok(key, $"{part} rev {revision} line {c.LineNumber} ({line.ComponentPartNumber}): {what}."),
                [pipeline.Event(new SubstitutionPolicyChanged(part, revision, c.LineNumber, c.Policy.ToString()), context, SourceContext, "BomLinePolicy", key)],
                new AuditChange("BomLinePolicy", key, line.Policy.ToString(), c.Policy.ToString()));
        }, ct);
    }

    private Task<CommandResult> DecideAsync<TCommand>(
        string commandType,
        long alternateId,
        TCommand command,
        AlternateKind kind,
        CommandContext context,
        bool reasonRequired,
        Func<AlternateInfo, DateTimeOffset, (AlternateInfo Updated, string Change, string Message)> decide,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var key = alternateId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, ApproverPermission(kind), ResourceRef.Of(Entity, key), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var current = await uow.Store<IAlternateLookup>().FindAlternateAsync(alternateId, ct);
            if (current is null || current.Kind != kind || (current.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
            {
                throw new CommandRejectedException("alternate_not_found", $"Alternate {alternateId} does not exist.");
            }

            if (current.ProposedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You proposed this alternate; someone else with authority decides it.");
            }

            var (updated, change, message) = decide(current, now);
            await uow.Store<IAlternateStore>().UpdateAlternateAsync(updated, ct);
            var program = current.ProgramId is { } pid ? new ProgramId(pid) : (ProgramId?)null;
            return new CommandOutcome(
                CommandResult.Ok(key, message),
                [pipeline.Event(new AlternateChanged(current.AlternateId, current.PrimaryPart, current.AlternatePart, change), context, SourceContext, Entity, key, program)],
                new AuditChange(Entity, key, CanonicalJson.Serialize(current), CanonicalJson.Serialize(updated), program));
        }, ct);
    }

    private static bool Same(AlternateInfo a, AlternateInfo b) =>
        a.PrimaryPart == b.PrimaryPart && a.AlternatePart == b.AlternatePart && a.Kind == b.Kind && a.ProgramId == b.ProgramId && a.Customer == b.Customer && a.ParentPart == b.ParentPart
        && a.AssetCode == b.AssetCode && a.MachineCode == b.MachineCode && a.OperationSequence == b.OperationSequence && a.SupplierCode == b.SupplierCode;

    private static string? Code(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}
