using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Quality;

public sealed record DefineGauge(string Code, string Description, DateOnly CalibrationDue, string? Certificate);

public sealed record RecordCalibration(string Code, DateOnly CalibratedOn, DateOnly DueDate, string? Certificate);

public sealed record NewCharacteristic(string Name, CharacteristicKind Kind, decimal? Nominal, decimal? Lower, decimal? Upper, string? Unit);

/// <summary>Replaces the inspection point after an operation of a part.</summary>
/// <param name="Revision">The item revision this plan is for; null for every revision.</param>
public sealed record SetInspectionPlan(string PartNumber, int Sequence, string Title, IReadOnlyList<NewCharacteristic> Characteristics, string? Revision = null);

public sealed record RemoveInspectionPlan(string PartNumber, int Sequence, string? Revision = null);

/// <summary>What the inspector found for one characteristic: a measurement, or accepted/rejected.</summary>
public sealed record Finding(int Number, decimal? Measured, bool? Accepted);

public sealed record RecordInspection(long OrderNumber, int Sequence, decimal Quantity, string? GaugeCode, string? Notes, IReadOnlyList<Finding> Findings, decimal? RejectedQuantity);

public sealed record RaiseNcr(string PartNumber, long? OrderNumber, int? Sequence, string? LotNumber, decimal Quantity, string Defect, string Description);

public sealed record DispositionNcr(long NcrId, Disposition Disposition, string Note);

public sealed record DecideUseAsIs(long NcrId, bool Approve);

public sealed record CloseNcr(long NcrId, string Note);

public sealed record OpenCapa(long? NcrId, string Title, string RootCause, string Action, DateOnly DueDate);

public sealed record ImplementCapa(long CapaId, string Note);

public sealed record VerifyCapa(long CapaId);

public sealed record InspectionRecorded(long InspectionId, long OrderNumber, int Sequence, string Result, long? NcrId) : IDomainEvent
{
    public static string EventType => nameof(InspectionRecorded);

    public static int SchemaVersion => 1;
}

public sealed record NcrChanged(long NcrId, string PartNumber, long? OrderNumber, string Status) : IDomainEvent
{
    public static string EventType => nameof(NcrChanged);

    public static int SchemaVersion => 1;
}

public sealed record QualityRecordChanged(string Kind, string Id, string Change) : IDomainEvent
{
    public static string EventType => nameof(QualityRecordChanged);

    public static int SchemaVersion => 1;
}

public interface IQualityStore
{


    Task SavePlanAsync(InspectionPlanInfo plan, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> DeletePlanAsync(string partNumber, int sequence, string? revision, CancellationToken ct);

    Task<long> InsertInspectionAsync(InspectionInfo inspection, CancellationToken ct);

    Task<long> InsertNcrAsync(NcrInfo ncr, CancellationToken ct);

    Task UpdateNcrAsync(NcrInfo ncr, DateTimeOffset at, string actor, CancellationToken ct);

    Task<long> InsertCapaAsync(CapaInfo capa, CancellationToken ct);

    Task<CapaInfo?> FindCapaAsync(long capaId, CancellationToken ct);

    Task UpdateCapaAsync(CapaInfo capa, DateTimeOffset at, CancellationToken ct);
}

/// <summary>
/// Quality (spec §45-46). Inspections are done by someone who did not do the work, with a calibrated
/// gauge for anything measured, and are never changed afterwards. A failed inspection raises a
/// nonconformance; accepting nonconforming material "use as is" needs a second person, and so does
/// verifying a corrective action. Open nonconformances and uninspected operations hold the order.
/// </summary>
public sealed class QualityService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork, IAcceptanceSourceAccess acceptanceSources, IForgeAuthorizer authorizer, ICapaLegacyGovernanceGuard? capaGovernance = null)
{
    public const string SourceContext = "Quality";
    private const string GaugeType = "GAUGE";

    public Task<CommandResult> DefineGaugeAsync(DefineGauge c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Gauge");
        var spec = new CommandSpec(SourceContext, nameof(DefineGauge), Permissions.QualityPlan, ResourceRef.Of("Gauge", code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            // V3.8: a gauge is a tool of the Gauge kind.
            var gauge = new GaugeInfo(code, BusinessCode.RequireName(c.Description, "Gauge"), c.CalibrationDue, Trim(c.Certificate, 100), true);
            var tools = uow.Store<IToolStore>();
            await tools.InsertToolTypeAsync(new ToolTypeInfo(GaugeType, "Gauge", ToolKind.Gauge, null, null, null), now, context.Actor, ct);
            var tool = new ToolInfo(code, GaugeType, gauge.Description, null, null, ToolCustody.InHouse, null, ToolCondition.Good, [], [], 0, 0, c.CalibrationDue, gauge.Certificate, ToolStatus.Available, now, context.Actor);
            if (!await tools.InsertToolAsync(tool, now, context.Actor, ct))
            {
                throw new CommandRejectedException("code_exists", $"Gauge {code} already exists.");
            }

            await tools.InsertToolEventAsync(new ToolEvent(0, code, ToolEventKind.Registered, now, context.Actor, Condition: ToolCondition.Good, DueOn: c.CalibrationDue, Certificate: gauge.Certificate), ct);

            return Outcome(context, "Gauge", code, "Defined", $"Gauge {code} calibrated until {c.CalibrationDue:yyyy-MM-dd}.", null, gauge);
        }, ct);
    }

    public Task<CommandResult> RecordCalibrationAsync(RecordCalibration c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Gauge");
        var spec = new CommandSpec(SourceContext, nameof(RecordCalibration), Permissions.QualityPlan, ResourceRef.Of("Gauge", code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.DueDate <= c.CalibratedOn || c.CalibratedOn > (await uow.SiteDateAsync(now, ct)))
            {
                throw new CommandRejectedException("invalid_calibration", "Calibration date cannot be in the future and the next due date must come after it.");
            }

            await uow.Store<IToolStore>().LockToolAsync(code, ct);
            var lookup = uow.Store<IToolLookup>();
            var gauge = GaugeInfo.From(await lookup.ListToolsAsync(ct), await lookup.ListToolTypesAsync(ct)).FirstOrDefault(g => g.Code == code)
                ?? throw new CommandRejectedException("gauge_not_found", $"Gauge {code} does not exist.");
            var tool = (await lookup.FindToolAsync(code, ct))!;
            await uow.Store<IToolStore>().UpdateToolAsync(tool with { CalibrationDue = c.DueDate, Certificate = Trim(c.Certificate, 100), ChangedAt = now, ChangedBy = context.Actor }, ct);
            await uow.Store<IToolStore>().InsertToolEventAsync(new ToolEvent(0, code, ToolEventKind.Calibrated, now, context.Actor, CalibratedOn: c.CalibratedOn, DueOn: c.DueDate, Certificate: Trim(c.Certificate, 100)), ct);
            return Outcome(context, "Gauge", code, "Calibrated", $"Gauge {code} calibrated {c.CalibratedOn:yyyy-MM-dd}, due again {c.DueDate:yyyy-MM-dd}.", gauge, gauge with { CalibrationDue = c.DueDate, Certificate = Trim(c.Certificate, 100) });
        }, ct);
    }

    public Task<CommandResult> SetInspectionPlanAsync(SetInspectionPlan c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var planRevision = string.IsNullOrWhiteSpace(c.Revision) ? null : BusinessCode.Require(c.Revision, "Revision");
        var id = planRevision is null ? $"{part}:{c.Sequence}" : $"{part}:{c.Sequence}:{planRevision}";
        var spec = new CommandSpec(SourceContext, nameof(SetInspectionPlan), Permissions.QualityPlan, ResourceRef.Of("InspectionPlan", id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var title = BusinessCode.RequireName(c.Title, "Inspection");
            var engineering = uow.Store<IEngineeringLookup>();
            if (await engineering.FindItemAsync(part, ct) is null)
            {
                throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            }

            // A plan for one revision checks that revision's routing; a plan for every revision, the routing in force.
            var revision = planRevision is null
                ? await engineering.GetEffectiveRevisionAsync(part, now, ct)
                : (await engineering.ListRevisionsAsync(part, ct)).FirstOrDefault(r => r.Revision == planRevision)
                    ?? throw new CommandRejectedException("revision_not_found", $"{part} has no revision {planRevision}.");
            var detail = revision is null ? null : await engineering.GetRevisionDetailAsync(part, revision.Revision, ct);
            if (detail is null || detail.Routing.All(o => o.Sequence != c.Sequence))
            {
                throw new CommandRejectedException("unknown_operation", planRevision is null
                    ? $"{part} has no operation {c.Sequence} in its released routing."
                    : $"{part} rev {planRevision} has no operation {c.Sequence}.");
            }

            if (c.Characteristics is null || c.Characteristics.Count is 0 or > 99)
            {
                throw new CommandRejectedException("characteristics_required", "An inspection checks 1 to 99 characteristics.");
            }

            var characteristics = c.Characteristics.Select((x, i) =>
            {
                var name = BusinessCode.RequireName(x.Name, "Characteristic");
                if (x.Kind == CharacteristicKind.Variable && x.Lower is null && x.Upper is null)
                {
                    throw new CommandRejectedException("limits_required", $"{name}: a measured characteristic needs a lower or upper limit.");
                }

                if (x.Lower is { } lo && x.Upper is { } hi && lo > hi)
                {
                    throw new CommandRejectedException("invalid_limits", $"{name}: the lower limit is above the upper limit.");
                }

                return x.Kind == CharacteristicKind.Attribute
                    ? new CharacteristicInfo(i + 1, name, x.Kind, null, null, null, null)
                    : new CharacteristicInfo(i + 1, name, x.Kind, x.Nominal, x.Lower, x.Upper, Trim(x.Unit, 20));
            }).ToList();

            var previous = (await uow.Store<IQualityLookup>().ListPlansAsync(part, ct)).FirstOrDefault(p => p.Sequence == c.Sequence && p.Revision == planRevision);
            var plan = new InspectionPlanInfo(part, c.Sequence, title, characteristics, planRevision);
            await uow.Store<IQualityStore>().SavePlanAsync(plan, now, context.Actor, ct);
            return Outcome(context, "InspectionPlan", id, previous is null ? "Defined" : "Changed", $"Inspection after {part}{(planRevision is null ? string.Empty : " rev " + planRevision)} operation {c.Sequence}: {characteristics.Count} characteristic(s).", previous, plan);
        }, ct);
    }

    public Task<CommandResult> RemoveInspectionPlanAsync(RemoveInspectionPlan c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var planRevision = string.IsNullOrWhiteSpace(c.Revision) ? null : BusinessCode.Require(c.Revision, "Revision");
        var id = planRevision is null ? $"{part}:{c.Sequence}" : $"{part}:{c.Sequence}:{planRevision}";
        var spec = new CommandSpec(SourceContext, nameof(RemoveInspectionPlan), Permissions.QualityPlan, ResourceRef.Of("InspectionPlan", id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, _) =>
        {
            var previous = (await uow.Store<IQualityLookup>().ListPlansAsync(part, ct)).FirstOrDefault(p => p.Sequence == c.Sequence && p.Revision == planRevision)
                ?? throw new CommandRejectedException("plan_not_found", $"No inspection after {part}{(planRevision is null ? string.Empty : " rev " + planRevision)} operation {c.Sequence}.");
            await uow.Store<IQualityStore>().DeletePlanAsync(part, c.Sequence, planRevision, ct);
            return Outcome(context, "InspectionPlan", id, "Removed", $"Inspection after {part} operation {c.Sequence} removed.", previous, null);
        }, ct);
    }

    public Task<CommandResult> RecordInspectionAsync(RecordInspection c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = $"{c.OrderNumber}/{c.Sequence}";
        var spec = new CommandSpec(SourceContext, nameof(RecordInspection), Permissions.QualityInspect, ResourceRef.Of("Inspection", id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(c.OrderNumber, ct);
            if (order is null || (order.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
            {
                throw new CommandRejectedException("order_not_found", $"Order {c.OrderNumber} does not exist.");
            }

            var op = order.Operations.FirstOrDefault(o => o.Sequence == c.Sequence)
                ?? throw new CommandRejectedException("operation_not_found", $"Order {order.OrderNumber} has no operation {c.Sequence}.");
            if (op.Status == OperationStatus.Pending)
            {
                throw new CommandRejectedException("not_started", $"Operation {c.Sequence} has not been worked yet.");
            }

            if (c.Quantity <= 0 || c.Quantity > order.Quantity || c.RejectedQuantity is < 0 || c.RejectedQuantity > c.Quantity)
            {
                throw new CommandRejectedException("invalid_quantity", $"Inspect between 1 and {order.Quantity}; the rejected quantity cannot exceed what was inspected.");
            }

            // The plan for the revision the order builds (V2.3).
            var plan = InspectionPlans.For(await uow.Store<IQualityLookup>().ListPlansAsync(order.PartNumber, ct), order.PartNumber, order.Revision).FirstOrDefault(x => x.Sequence == c.Sequence)
                ?? throw new CommandRejectedException("no_inspection_plan", $"{order.PartNumber} rev {order.Revision} has no inspection after operation {c.Sequence}.");

            // Independence (spec §45): the inspector did not work this operation.
            var labor = await uow.Store<IExecutionLookup>().ListLaborAsync(now, now, order.OrderNumber, ct);
            var reports = await uow.Store<IExecutionLookup>().ListReportsAsync(now, now, order.OrderNumber, ct);
            var me = context.Principal.PersonId.Value;
            if (labor.Any(l => l.Sequence == c.Sequence && l.PersonId == me) || reports.Any(r => r.Sequence == c.Sequence && r.PersonId == me))
            {
                throw new CommandRejectedException("separation_of_duties", "You worked on this operation; a different person must inspect it.");
            }

            string? gauge = null;
            if (plan.Characteristics.Any(x => x.Kind == CharacteristicKind.Variable))
            {
                gauge = string.IsNullOrWhiteSpace(c.GaugeCode) ? throw new CommandRejectedException("gauge_required", "Measured characteristics need the gauge used.") : BusinessCode.Require(c.GaugeCode, "Gauge");
                var lookup = uow.Store<IToolLookup>();
                var info = GaugeInfo.From(await lookup.ListToolsAsync(ct), await lookup.ListToolTypesAsync(ct)).FirstOrDefault(g => g.Code == gauge)
                    ?? throw new CommandRejectedException("gauge_not_found", $"Gauge {gauge} does not exist.");
                if (!info.InCalibrationOn((await uow.SiteDateAsync(now, ct))))
                {
                    throw new CommandRejectedException("gauge_out_of_calibration", $"Gauge {gauge} was due for calibration {info.CalibrationDue:yyyy-MM-dd}. Use a calibrated gauge.");
                }

                // V3.8: judged as a tool at the moment of use (custody, condition, service), and the use is counted.
                await ToolUse.RecordAsync(uow, gauge, null, null, $"inspection {c.OrderNumber}/{c.Sequence}", now, context.Actor, ct);
            }

            var results = new List<CharacteristicResult>();
            foreach (var x in plan.Characteristics)
            {
                var f = (c.Findings ?? []).FirstOrDefault(f => f.Number == x.Number)
                    ?? throw new CommandRejectedException("finding_required", $"Record characteristic {x.Number} ({x.Name}).");
                if (x.Kind == CharacteristicKind.Variable)
                {
                    var measured = f.Measured ?? throw new CommandRejectedException("measurement_required", $"{x.Name}: enter the measurement.");
                    var pass = (x.Lower is null || measured >= x.Lower) && (x.Upper is null || measured <= x.Upper);
                    results.Add(new CharacteristicResult(x.Number, x.Name, x.Kind, x.Lower, x.Upper, measured, null, pass));
                }
                else
                {
                    var accepted = f.Accepted ?? throw new CommandRejectedException("decision_required", $"{x.Name}: accept or reject.");
                    results.Add(new CharacteristicResult(x.Number, x.Name, x.Kind, null, null, null, accepted, accepted));
                }
            }

            var store = uow.Store<IQualityStore>();
            var passed = results.All(r => r.Pass);
            long? ncrId = null;
            if (!passed)
            {
                var failed = results.Where(r => !r.Pass).Select(r => r.Kind == CharacteristicKind.Variable ? $"{r.Name} {r.Measured} outside {r.Lower}-{r.Upper}" : $"{r.Name} rejected").ToList();
                ncrId = await store.InsertNcrAsync(new NcrInfo(
                    0, order.PartNumber, order.OrderNumber, c.Sequence, null, c.RejectedQuantity is > 0 ? c.RejectedQuantity.Value : c.Quantity,
                    "Failed inspection", string.Join("; ", failed) + (string.IsNullOrWhiteSpace(c.Notes) ? string.Empty : ". " + c.Notes.Trim()),
                    NcrStatus.Open, null, null, context.Actor, now, null, null, null, null, null), ct);
            }

            var inspection = new InspectionInfo(0, order.OrderNumber, c.Sequence, order.PartNumber, c.Quantity, passed ? InspectionResult.Pass : InspectionResult.Fail, gauge, Trim(c.Notes, 500), context.Actor, now, results, ncrId);
            var inspectionId = await store.InsertInspectionAsync(inspection, ct);
            var program = order.ProgramId is { } pid ? new ProgramId(pid) : (ProgramId?)null;
            var events = new List<EventEnvelope> { pipeline.Event(new InspectionRecorded(inspectionId, order.OrderNumber, c.Sequence, inspection.Result.ToString(), ncrId), context, SourceContext, "Inspection", id, program) };
            if (ncrId is { } n)
            {
                events.Add(pipeline.Event(new NcrChanged(n, order.PartNumber, order.OrderNumber, nameof(NcrStatus.Open)), context, SourceContext, "Ncr", n.ToString(System.Globalization.CultureInfo.InvariantCulture), program));
            }

            var key = inspectionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, passed
                    ? $"Order {order.OrderNumber} operation {c.Sequence} passed inspection."
                    : $"Order {order.OrderNumber} operation {c.Sequence} failed inspection; NCR {ncrId} raised and the order is held until it is dispositioned."),
                events,
                new AuditChange("Inspection", key, null, CanonicalJson.Serialize(inspection with { InspectionId = inspectionId }), program));
        }, ct);
    }

    public Task<CommandResult> RaiseNcrAsync(RaiseNcr c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(RaiseNcr), Permissions.QualityInspect, ResourceRef.Of("Ncr"));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) is null)
            {
                throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            }

            if (c.OrderNumber is { } number)
            {
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(number, ct);
                if (order is null || order.PartNumber != part || (order.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
                {
                    throw new CommandRejectedException("order_not_found", $"Order {number} for {part} does not exist.");
                }

                if (c.Sequence is { } seq && order.Operations.All(o => o.Sequence != seq))
                {
                    throw new CommandRejectedException("operation_not_found", $"Order {number} has no operation {seq}.");
                }
            }

            if (c.Quantity <= 0 || c.Quantity > 100_000_000)
            {
                throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero.");
            }

            var ncr = new NcrInfo(0, part, c.OrderNumber, c.OrderNumber is null ? null : c.Sequence, Trim(c.LotNumber, 40)?.ToUpperInvariant(), c.Quantity,
                BusinessCode.RequireName(c.Defect, "Defect"), BusinessCode.RequireName(c.Description, "Description"), NcrStatus.Open, null, null, context.Actor, now, null, null, null, null, null);
            var ncrId = await uow.Store<IQualityStore>().InsertNcrAsync(ncr, ct);
            var id = ncrId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"NCR {id} raised: {c.Quantity} {part}, {ncr.Defect}." + (c.OrderNumber is null ? string.Empty : $" Order {c.OrderNumber} is held until it is dispositioned.")),
                [pipeline.Event(new NcrChanged(ncrId, part, c.OrderNumber, nameof(NcrStatus.Open)), context, SourceContext, "Ncr", id)],
                new AuditChange("Ncr", id, null, CanonicalJson.Serialize(ncr with { NcrId = ncrId })));
        }, ct);
    }

    public Task<CommandResult> DispositionAsync(DispositionNcr c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return NcrAsync(nameof(DispositionNcr), c.NcrId, c, context, Permissions.QualityDisposition, ncr =>
        {
            if (ncr.Status != NcrStatus.Open)
            {
                throw new CommandRejectedException("not_open", $"NCR {ncr.NcrId} is {ncr.Status}.");
            }

            var note = BusinessCode.RequireName(c.Note, "Disposition note");
            return c.Disposition is Disposition.UseAsIs or Disposition.Repair
                ? (ncr with { Status = NcrStatus.AwaitingApproval, Disposition = c.Disposition, DispositionNote = note, DispositionedBy = context.Actor }, $"{Words(c.Disposition)} proposed; a quality manager must approve it")
                : (ncr with { Status = NcrStatus.Dispositioned, Disposition = c.Disposition, DispositionNote = note, DispositionedBy = context.Actor }, $"dispositioned {Words(c.Disposition)}");
        }, ct);
    }

    public Task<CommandResult> DecideUseAsIsAsync(DecideUseAsIs c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return NcrAsync(nameof(DecideUseAsIs), c.NcrId, c, context, Permissions.QualityApprove, ncr =>
        {
            if (ncr.Status != NcrStatus.AwaitingApproval)
            {
                throw new CommandRejectedException("not_awaiting_approval", $"NCR {ncr.NcrId} is {ncr.Status}.");
            }

            if (ncr.DispositionedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You proposed this disposition; a different person must approve it.");
            }

            var words = Words(ncr.Disposition ?? Disposition.UseAsIs);
            return c.Approve
                ? (ncr with { Status = NcrStatus.Dispositioned, ApprovedBy = context.Actor }, $"{words} approved")
                : (ncr with { Status = NcrStatus.Open, Disposition = null, DispositionNote = null, DispositionedBy = null }, $"{words} rejected; disposition it again");
        }, ct);
    }

    public Task<CommandResult> CloseNcrAsync(CloseNcr c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return NcrAsync(nameof(CloseNcr), c.NcrId, c, context, Permissions.QualityDisposition, ncr => ncr.Status == NcrStatus.Dispositioned
            ? (ncr with { Status = NcrStatus.Closed, ClosedBy = context.Actor, ClosureNote = BusinessCode.RequireName(c.Note, "Closure note") }, "closed")
            : throw new CommandRejectedException("not_dispositioned", $"NCR {ncr.NcrId} is {ncr.Status}; it is closed after its disposition is decided."), ct);
    }

    public async Task<CommandResult> OpenCapaAsync(OpenCapa c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ResourceRef resource;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
            resource = await ResourceForCapaSourceAsync(read, c.NcrId, null, ct);
        var spec = new CommandSpec(SourceContext, nameof(OpenCapa), Permissions.QualityDisposition, resource);
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await QualityAuthorityAsync(uow, context.Principal, Permissions.QualityDisposition, "Capa", null, c.NcrId, await ResourcesForCapaSourceAsync(uow, c.NcrId, null, ct), ct);
            if (await ResourceForCapaSourceAsync(uow, c.NcrId, null, ct) != resource)
                throw new CommandRejectedException("capa_context_changed", "The corrective action's source context changed. Review it and retry.");

            var capa = new CapaInfo(0, c.NcrId, BusinessCode.RequireName(c.Title, "Title"), BusinessCode.RequireName(c.RootCause, "Root cause"), BusinessCode.RequireName(c.Action, "Action"),
                c.DueDate, CapaStatus.Open, context.Actor, now, null, null, null, null);
            var capaId = await uow.Store<IQualityStore>().InsertCapaAsync(capa, ct);
            var id = capaId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Outcome(context, "Capa", id, "Opened", $"Corrective action {id} opened, due {c.DueDate:yyyy-MM-dd}.", null, capa with { CapaId = capaId }, resource);
        }, ct, async (uow, _) => await QualityAuthorityAsync(uow, context.Principal, Permissions.QualityDisposition, "Capa", null, c.NcrId, await ResourcesForCapaSourceAsync(uow, c.NcrId, null, ct), ct));
    }

    public Task<CommandResult> ImplementCapaAsync(ImplementCapa c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return CapaAsync(nameof(ImplementCapa), c.CapaId, c, context, Permissions.QualityDisposition, false, capa => capa.Status == CapaStatus.Open
            ? capa with { Status = CapaStatus.Implemented, ImplementedBy = context.Actor, ImplementationNote = BusinessCode.RequireName(c.Note, "Implementation note") }
            : throw new CommandRejectedException("not_open", $"Corrective action {capa.CapaId} is {capa.Status}."), ct);
    }

    public Task<CommandResult> VerifyCapaAsync(VerifyCapa c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return CapaAsync(nameof(VerifyCapa), c.CapaId, c, context, Permissions.QualityApprove, true, capa => capa.Status != CapaStatus.Implemented
            ? throw new CommandRejectedException("not_implemented", $"Corrective action {capa.CapaId} is {capa.Status}; it is verified after it is implemented.")
            : capa.ImplementedBy == context.Actor
                ? throw new CommandRejectedException("separation_of_duties", "You implemented this action; a different person must verify it worked.")
                : capa with { Status = CapaStatus.Verified, VerifiedBy = context.Actor }, ct);
    }

    private async Task<CommandResult> NcrAsync<TCommand>(string commandType, long ncrId, TCommand command, CommandContext context, string permission, Func<NcrInfo, (NcrInfo Next, string Message)> change, CancellationToken ct)
    {
        var id = ncrId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ResourceRef resource;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var ncr = await read.Store<IQualityLookup>().FindNcrAsync(ncrId, ct);
            resource = ncr is null ? ResourceRef.Of("Ncr", id) : await ResourceForNcrAsync(read, ncr, ct);
        }
        var spec = new CommandSpec(SourceContext, commandType, permission, resource, ReasonRequired: true);
        return await pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var ncr = await uow.Store<IQualityLookup>().FindNcrAsync(ncrId, ct)
                ?? throw new CommandRejectedException("ncr_not_found", $"NCR {id} does not exist.");
            await QualityAuthorityAsync(uow, context.Principal, permission, "Ncr", ncrId, ncrId, await ResourcesForNcrAsync(uow, ncr, ct), ct);
            if (await ResourceForNcrAsync(uow, ncr, ct) != resource)
                throw new CommandRejectedException("ncr_context_changed", "The NCR's operational context changed. Review its current source records and retry.");
            var (next, message) = change(ncr);
            await uow.Store<IQualityStore>().UpdateNcrAsync(next, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"NCR {id}: {message}."),
                [pipeline.Event(new NcrChanged(ncrId, ncr.PartNumber, ncr.OrderNumber, next.Status.ToString()), context, SourceContext, "Ncr", id, resource.Program, resource.Markings)],
                new AuditChange("Ncr", id, CanonicalJson.Serialize(ncr), CanonicalJson.Serialize(next), resource.Program, resource.Markings));
        }, ct, async (uow, _) =>
        {
            var ncr = await uow.Store<IQualityLookup>().FindNcrAsync(ncrId, ct) ?? throw new CommandRejectedException("ncr_not_found", $"NCR {id} does not exist.");
            return await QualityAuthorityAsync(uow, context.Principal, permission, "Ncr", ncrId, ncrId, await ResourcesForNcrAsync(uow, ncr, ct), ct);
        });
    }

    /// <summary>The NCR's recorded order and operation supply its current authorization context.</summary>
    public static async Task<ResourceRef> ResourceForNcrAsync(IKernelUnitOfWork uow, NcrInfo ncr, CancellationToken ct)
    {
        var bound = await uow.Store<IAcceptanceLookup>().FindInspectionForNcrAsync(ncr.NcrId, ct);
        if (bound is not null) return AcceptanceSubjectResources.Of(bound.Subject, "Ncr", ncr.NcrId);
        var order = ncr.OrderNumber is { } number ? await uow.Store<IExecutionLookup>().FindOrderAsync(number, ct) : null;
        var workCenter = ncr.Sequence is { } sequence ? order?.Operations.FirstOrDefault(operation => operation.Sequence == sequence)?.WorkCenterCode : null;
        string? facility = null;
        if (workCenter is not null)
        {
            var resources = uow.Store<IResourcesLookup>();
            var center = await resources.FindWorkCenterAsync(workCenter, ct);
            if (center is not null)
                facility = (await resources.FindSafetyAreaAsync(center.SafetyAreaCode, ct))?.SiteCode;
        }
        return new ResourceRef("Ncr", ncr.NcrId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            order?.ProgramId is { } program ? new ProgramId(program) : null,
            FacilityId: facility, WorkCenterId: workCenter,
            ContractId: order?.ContractId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>All captured product-owner scopes of a child NCR; current owner/source access is checked separately.</summary>
    public static async Task<IReadOnlyList<ResourceRef>> ResourcesForNcrAsync(IKernelUnitOfWork uow, NcrInfo ncr, CancellationToken ct)
    {
        var bound = await uow.Store<IAcceptanceLookup>().FindInspectionForNcrAsync(ncr.NcrId, ct);
        return bound is null ? [await ResourceForNcrAsync(uow, ncr, ct)] : AcceptanceSubjectResources.All(bound.Subject, "Ncr", ncr.NcrId);
    }

    /// <summary>A linked corrective action retains its NCR's current operational access context.</summary>
    public static async Task<ResourceRef> ResourceForCapaAsync(IKernelUnitOfWork uow, CapaInfo capa, CancellationToken ct)
    {
        var original = await ResourceForCapaSourceAsync(uow, capa.NcrId, capa.CapaId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        if (!capa.Governed) return original;
        var governed = await uow.Store<IAuditCapaLookup>().FindCapaAsync(capa.CapaId, ct)
            ?? throw new CommandRejectedException("capa_governance_unavailable", "The governed source is unavailable.");
        var resource = Forge.Quality.Audit.QmsAuditService.Resource(governed);
        return resource with { Markings = resource.Markings | original.Markings |
            governed.Sources.SelectMany(s => s.Sources).Aggregate(DataMarking.None, (marks, source) => marks | source.Resource.Markings) };
    }

    public static Task<IReadOnlyList<ResourceRef>> ResourcesForCapaAsync(IKernelUnitOfWork uow, CapaInfo capa, CancellationToken ct) =>
        ResourcesForCapaSourceAsync(uow, capa.NcrId, capa.CapaId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

    private static async Task<IReadOnlyList<ResourceRef>> ResourcesForCapaSourceAsync(IKernelUnitOfWork uow, long? ncrId, string? id, CancellationToken ct)
    {
        if (ncrId is not { } number) return [ResourceRef.Of("Capa", id)];
        var ncr = await uow.Store<IQualityLookup>().FindNcrAsync(number, ct) ?? throw new CommandRejectedException("ncr_not_found", "The corrective action's source NCR does not exist.");
        return (await ResourcesForNcrAsync(uow, ncr, ct)).Select(resource => resource with { Type = "Capa", Id = id }).ToArray();
    }

    private async Task<CommandAuthority> QualityAuthorityAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string permission, string type, long? id, long? ncrId, IReadOnlyList<ResourceRef> fallback, CancellationToken ct)
    {
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) ?? principal with { IsActive = false };
        IReadOnlyList<ResourceRef> resources = fallback;
        if (ncrId is { } source)
        {
            await acceptanceSources.RequireNcrSourceReadAsync(uow, principal, source, ct);
            resources = await acceptanceSources.ResourcesForNcrAsync(uow, source, type, id, ct) ?? fallback;
        }
        foreach (var resource in resources)
        {
            var decision = authorizer.Authorize(principal, permission, resource);
            if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision, resource);
        }
        return new(resources[0], ncrId?.ToString(System.Globalization.CultureInfo.InvariantCulture), resources.Skip(1).ToArray());
    }

    private static async Task<ResourceRef> ResourceForCapaSourceAsync(IKernelUnitOfWork uow, long? ncrId, string? id, CancellationToken ct)
    {
        if (ncrId is not { } number) return ResourceRef.Of("Capa", id);
        var ncr = await uow.Store<IQualityLookup>().FindNcrAsync(number, ct)
            ?? throw new CommandRejectedException("ncr_not_found", "The corrective action's source NCR does not exist.");
        return (await ResourceForNcrAsync(uow, ncr, ct)) with { Type = "Capa", Id = id };
    }

    private async Task<CommandResult> CapaAsync<TCommand>(string commandType, long capaId, TCommand command, CommandContext context, string permission, bool reasonRequired, Func<CapaInfo, CapaInfo> change, CancellationToken ct)
    {
        var id = capaId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ResourceRef resource;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var source = await read.Store<IQualityStore>().FindCapaAsync(capaId, ct);
            resource = source is null ? ResourceRef.Of("Capa", id) : await ResourceForCapaAsync(read, source, ct);
        }
        var spec = new CommandSpec(SourceContext, commandType, permission, resource, reasonRequired);
        return await pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var store = uow.Store<IQualityStore>();
            var capa = await store.FindCapaAsync(capaId, ct)
                ?? throw new CommandRejectedException("capa_not_found", $"Corrective action {id} does not exist.");
            if (capaGovernance is not null) await capaGovernance.AssertLegacyAsync(uow, context, capaId, ct);
            else if (capa.Governed) throw new CommandRejectedException("capa_governance_unavailable", "The governed command guard is unavailable.");
            await QualityAuthorityAsync(uow, context.Principal, permission, "Capa", capaId, capa.NcrId, await ResourcesForCapaAsync(uow, capa, ct), ct);
            if (await ResourceForCapaAsync(uow, capa, ct) != resource)
                throw new CommandRejectedException("capa_context_changed", "The corrective action's source context changed. Review it and retry.");
            var next = change(capa);
            await store.UpdateCapaAsync(next, now, ct);
            return Outcome(context, "Capa", id, next.Status.ToString(), $"Corrective action {id}: {next.Status.ToString().ToLowerInvariant()}.", capa, next, resource);
        }, ct, async (uow, _) =>
        {
            if (capaGovernance is not null) await capaGovernance.AssertLegacyAsync(uow, context, capaId, ct);
            var capa = await uow.Store<IQualityStore>().FindCapaAsync(capaId, ct) ?? throw new CommandRejectedException("capa_not_found", $"Corrective action {id} does not exist.");
            if (capa.Governed) throw new CommandRejectedException("capa_governed", "Use the governed investigation and effectiveness workflow.");
            var currentResource = await ResourceForCapaAsync(uow, capa, ct);
            if (currentResource != resource) throw new CommandRejectedException("capa_context_changed", "Review the current source context and retry.");
            return await QualityAuthorityAsync(uow, context.Principal, permission, "Capa", capaId, capa.NcrId, await ResourcesForCapaAsync(uow, capa, ct), ct);
        });
    }

    private CommandOutcome Outcome(CommandContext context, string kind, string id, string change, string message, object? before, object? after, ResourceRef? resource = null) => new(
        CommandResult.Ok(id, message),
        [pipeline.Event(new QualityRecordChanged(kind, id, change), context, SourceContext, kind, id, resource?.Program, resource?.Markings ?? DataMarking.None)],
        new AuditChange(kind, id, before is null ? null : CanonicalJson.Serialize(before), after is null ? null : CanonicalJson.Serialize(after), resource?.Program, resource?.Markings ?? DataMarking.None));

    private static string Words(Disposition d) => d switch
    {
        Disposition.UseAsIs => "use as is",
        Disposition.Rework => "rework",
        Disposition.Scrap => "scrap",
        Disposition.Repair => "repair",
        _ => "return to supplier",
    };

    private static string? Trim(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}
