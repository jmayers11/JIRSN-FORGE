using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning.Engines;
using Forge.Resources.Contracts;

namespace Forge.Planning;

/// <param name="RequiredBy">Its own date; leave empty and give <paramref name="LeadDays"/> to follow the parent goal.</param>
public sealed record CreateGoal(GoalKind Kind, string Title, long? ParentGoalId, long? OrderNumber, long? DemandId, DateOnly? RequiredBy, int? LeadDays, Guid? ProgramId = null);

public sealed record ChangeGoal(long GoalId, string Title, DateOnly? RequiredBy, int? LeadDays);

public sealed record CloseGoal(long GoalId);

public sealed record ReforecastGoals;

public sealed record GoalChanged(long GoalId, string Change) : IDomainEvent
{
    public static string EventType => nameof(GoalChanged);

    public static int SchemaVersion => 1;
}

public sealed record GoalsReforecast(int Goals, int AtRisk, int Late, int BehindPlan) : IDomainEvent
{
    public static string EventType => nameof(GoalsReforecast);

    public static int SchemaVersion => 1;
}

/// <summary>A goal evaluated now, with what earlier reforecasts said about it.</summary>
public sealed record GoalView(GoalAttainment Goal, GoalStatus Status, IReadOnlyList<GoalForecast> History);

/// <summary>
/// Goals and their attainment (V3.7, V3 brief A, AW; spec §42). Planners and program managers set production and
/// delivery goals and break them down; children's required-by dates follow their parents. A reforecast schedules
/// the remaining work from now at finite capacity and records, per goal, what it projected; behind plan (against
/// the approved schedule) is kept apart from at risk (against the required-by date).
/// </summary>
public sealed class GoalService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public const string SourceContext = "Planning";
    private const string Entity = "Goal";
    private const int ReforecastDays = 62;

    public Task<CommandResult> CreateAsync(CreateGoal c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(CreateGoal), Permissions.GoalsManage, ResourceRef.Of(Entity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IGoalStore>();
            var goals = await store.ListAsync(true, ct);
            RequireDates(c.ParentGoalId, c.RequiredBy, c.LeadDays);
            Guid? program = c.ProgramId;
            if (c.ParentGoalId is { } parentId)
            {
                var parent = goals.FirstOrDefault(g => g.GoalId == parentId) ?? throw new CommandRejectedException("goal_not_found", $"Goal {parentId} does not exist.");
                if (parent.Status != GoalStatus.Open)
                {
                    throw new CommandRejectedException("goal_closed", $"Goal {parentId} is closed.");
                }

                if (parent.Kind == GoalKind.Production && c.Kind == GoalKind.Delivery)
                {
                    throw new CommandRejectedException("kind_mismatch", "A production goal breaks down into production goals only; a delivery depends on production, not the other way round.");
                }

                program ??= parent.ProgramId;
            }

            if (c.OrderNumber is { } orderNumber)
            {
                if (c.Kind != GoalKind.Production)
                {
                    throw new CommandRejectedException("kind_mismatch", "Only a production goal follows a production order.");
                }

                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
                if (order is null || !Visible(context.Principal, order.ProgramId))
                {
                    throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
                }

                program = Same(program, order.ProgramId);
            }

            if (c.DemandId is { } demandId)
            {
                if (c.Kind != GoalKind.Delivery)
                {
                    throw new CommandRejectedException("kind_mismatch", "Only a delivery goal follows a demand line.");
                }

                var d = await uow.Store<IDemandStore>().FindAsync(demandId, ct);
                if (d is null || !Visible(context.Principal, d.ProgramId))
                {
                    throw new CommandRejectedException("demand_not_found", $"Demand {demandId} does not exist.");
                }

                program = Same(program, d.ProgramId);
            }

            if (program is { } p && !Visible(context.Principal, p))
            {
                throw new CommandRejectedException("program_not_found", "Choose a program you have access to.");
            }

            var goal = new GoalInfo(0, c.Kind, BusinessCode.RequireName(c.Title, "Title"), c.ParentGoalId, c.OrderNumber, c.DemandId, c.RequiredBy, c.LeadDays, program,
                GoalStatus.Open, now, context.Actor, now, context.Actor);
            var id = await store.InsertAsync(goal, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{c.Kind} goal {key} “{goal.Title}” set{(c.RequiredBy is { } r ? $", required by {r:yyyy-MM-dd}" : $", {c.LeadDays} day(s) before goal {c.ParentGoalId}")}."),
                [pipeline.Event(new GoalChanged(id, "Created"), context, SourceContext, Entity, key, program is { } pid ? new ProgramId(pid) : null)],
                new AuditChange(Entity, key, null, CanonicalJson.Serialize(goal with { GoalId = id }), program is { } ap ? new ProgramId(ap) : null));
        }, ct);
    }

    public Task<CommandResult> ChangeAsync(ChangeGoal c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.GoalId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ChangeGoal), Permissions.GoalsManage, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var current = await GoalAsync(uow, context, c.GoalId, ct);
            RequireDates(current.ParentGoalId, c.RequiredBy, c.LeadDays);
            var changed = current with { Title = BusinessCode.RequireName(c.Title, "Title"), RequiredBy = c.RequiredBy, LeadDays = c.LeadDays };
            if (changed == current)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, "Nothing to change."));
            }

            changed = changed with { ChangedAt = now, ChangedBy = context.Actor };
            await uow.Store<IGoalStore>().UpdateAsync(changed, ct);
            var children = (await uow.Store<IGoalStore>().ListAsync(false, ct)).Count(g => g.ParentGoalId == c.GoalId && g.LeadDays is not null);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Goal {key} changed{(children > 0 && current.RequiredBy != changed.RequiredBy ? $"; {children} goal(s) below it move with it" : string.Empty)}."),
                [pipeline.Event(new GoalChanged(c.GoalId, "Changed"), context, SourceContext, Entity, key, Program(current))],
                new AuditChange(Entity, key, CanonicalJson.Serialize(current), CanonicalJson.Serialize(changed), Program(current)));
        }, ct);
    }

    public Task<CommandResult> CloseAsync(CloseGoal c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.GoalId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(CloseGoal), Permissions.GoalsManage, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var current = await GoalAsync(uow, context, c.GoalId, ct);
            if (current.Status == GoalStatus.Closed)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, $"Goal {key} is already closed."));
            }

            var open = (await uow.Store<IGoalStore>().ListAsync(false, ct)).Count(g => g.ParentGoalId == c.GoalId);
            if (open > 0)
            {
                throw new CommandRejectedException("goal_has_children", $"Goal {key} has {open} open goal(s) below it; close those first.");
            }

            var closed = current with { Status = GoalStatus.Closed, ChangedAt = now, ChangedBy = context.Actor };
            await uow.Store<IGoalStore>().UpdateAsync(closed, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Goal {key} closed; its forecasts stay on record."),
                [pipeline.Event(new GoalChanged(c.GoalId, "Closed"), context, SourceContext, Entity, key, Program(current))],
                new AuditChange(Entity, key, CanonicalJson.Serialize(current), CanonicalJson.Serialize(closed), Program(current)));
        }, ct);
    }

    /// <summary>Schedules the remaining work from now and records what it projects for every open goal.</summary>
    public Task<CommandResult> ReforecastAsync(ReforecastGoals c, CommandContext context, CancellationToken ct)
    {
        var spec = new CommandSpec(SourceContext, nameof(ReforecastGoals), Permissions.GoalsManage, ResourceRef.Of(Entity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var goals = await EvaluateAsync(uow, time, false, ct);
            var store = uow.Store<IGoalStore>();
            foreach (var g in goals)
            {
                await store.InsertForecastAsync(new GoalForecast(0, g.GoalId, now, g.RequiredBy, g.Projected, g.State, g.BehindPlan, g.Attainment, context.Actor), ct);
            }

            var (risk, late, behind) = (goals.Count(g => g.State == GoalState.AtRisk), goals.Count(g => g.State == GoalState.Late), goals.Count(g => g.BehindPlan));
            var summary = $"{goals.Count} goal(s) reforecast: {late} late, {risk} at risk, {behind} behind plan.";
            return new CommandOutcome(
                CommandResult.Ok(now.ToString("O", CultureInfo.InvariantCulture), summary),
                [pipeline.Event(new GoalsReforecast(goals.Count, risk, late, behind), context, SourceContext, Entity, "reforecast")],
                new AuditChange(Entity, "reforecast", null, CanonicalJson.Serialize(new { summary })));
        }, ct);
    }

    public async Task<IReadOnlyList<GoalView>> ListAsync(ForgePrincipal principal, bool includeClosed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, Permissions.ScheduleRead, ResourceRef.Of(Entity));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var all = await uow.Store<IGoalStore>().ListAsync(includeClosed, ct);
        var history = (await uow.Store<IGoalStore>().ListForecastsAsync(null, ct)).ToLookup(f => f.GoalId);
        var status = all.ToDictionary(g => g.GoalId, g => g.Status);
        return (await EvaluateAsync(uow, time, includeClosed, ct, principal))
            .Where(g => Visible(principal, g.ProgramId))
            .Select(g => new GoalView(g, status[g.GoalId], history[g.GoalId].OrderByDescending(f => f.At).Take(20).ToList()))
            .ToList();
    }

    /// <summary>Every open goal evaluated now (shared with the goal-attainment report).</summary>
    internal static async Task<IReadOnlyList<GoalAttainment>> EvaluateAsync(IKernelUnitOfWork uow, TimeProvider time, bool includeClosed, CancellationToken ct, ForgePrincipal? viewer = null)
    {
        var goals = await uow.Store<IGoalStore>().ListAsync(includeClosed, ct);
        if (goals.Count == 0)
        {
            return [];
        }

        var execution = uow.Store<IExecutionLookup>();
        var orders = new Dictionary<long, OrderInfo>();
        foreach (var n in goals.Where(g => g.OrderNumber is not null).Select(g => g.OrderNumber!.Value).Distinct())
        {
            if (await execution.FindOrderAsync(n, ct) is { } order)
            {
                orders[n] = order;
            }
        }

        var demand = new Dictionary<long, GoalDemand>();
        if (goals.Any(g => g.DemandId is not null))
        {
            var shipped = (await uow.Store<IInventoryLookup>().ListShippedByDemandAsync(ct)).ToLookup(d => d.DemandId);
            foreach (var d in (await uow.Store<IDemandStore>().ListAsync(true, ct)).Where(d => goals.Any(g => g.DemandId == d.DemandId)))
            {
                demand[d.DemandId] = new GoalDemand(d.DemandId, d.PartNumber, d.Quantity, shipped[d.DemandId].Where(s => s.PartNumber == d.PartNumber).Sum(s => s.Shipped), d.NeedDate);
            }
        }

        var risk = orders.Count == 0 ? new Dictionary<long, GoalRiskLine>() : await RiskAsync(uow, time, ct, viewer);
        var today = await MaterialData.TodayAsync(uow, time, ct);
        return GoalAttainmentEngine.Evaluate(goals, risk, orders, demand, today, await uow.SiteTimeZoneAsync(ct));
    }

    /// <summary>
    /// Goal risk per released order: behind plan against the approved schedule, projection from a fresh
    /// finite-capacity schedule of the remaining work (the reforecast).
    /// </summary>
    public static async Task<Dictionary<long, GoalRiskLine>> RiskAsync(IKernelUnitOfWork uow, TimeProvider time, CancellationToken ct, ForgePrincipal? viewer = null)
    {
        var snapshot = await SnapshotLoader.LoadAsync(uow, time, ct);
        var lookup = uow.Store<IExecutionLookup>();
        var released = new List<OrderInfo>();
        foreach (var summary in (await lookup.ListOrdersAsync(ct)).Where(o => o.Status == OrderStatus.Released).OrderBy(o => o.OrderNumber))
        {
            released.Add((await lookup.FindOrderAsync(summary.OrderNumber, ct))!);
        }

        if (released.Count == 0)
        {
            return new Dictionary<long, GoalRiskLine>();
        }

        var material = (await MaterialPlanningService.RunMaterialAsync(uow, time, ct, viewer)).Output;
        var quality = await ScheduleQuality.LoadAsync(uow, released, ct);
        var reforecast = EngineRunner.Run(new SchedulingEngine(), snapshot, new ScheduleRequest(snapshot.AsOf, snapshot.AsOf.AddDays(ReforecastDays), released, material, quality), snapshot.AsOf).Output;
        var plan = (await uow.Store<IPlanningStore>().LatestAsync(ScheduleStatus.Approved, true, ct))?.Result;
        var actuals = released.SelectMany(o => o.Operations.Select(op => new GoalActualOperation(o.OrderNumber, op.Sequence, op.Status, op.CompletedAt))).ToList();
        var result = new GoalRiskEngine().Evaluate(new GoalRiskSnapshot(plan, actuals, reforecast), new GoalRiskRequest(snapshot.AsOf), snapshot.AsOf).Output;
        if (viewer is null) return result.Lines.ToDictionary(l => l.OrderNumber);
        var access = await MaterialSourceVisibility.LoadAsync(uow, viewer, ct, authorizer: new ForgeAuthorizer(time));
        return result.Lines.ToDictionary(l => l.OrderNumber, l => l with { Evidence = l.Evidence.Select(access.Text).ToArray(), MissedOperations = l.MissedOperations.Select(access.Text).ToArray() });
    }

    private static void RequireDates(long? parent, DateOnly? requiredBy, int? leadDays)
    {
        if ((requiredBy is null) == (leadDays is null))
        {
            throw new CommandRejectedException("invalid_dates", "Give a goal either its own required-by date or a number of days before its parent's.");
        }

        if (parent is null && requiredBy is null)
        {
            throw new CommandRejectedException("invalid_dates", "A goal with nothing above it has its own required-by date.");
        }

        if (leadDays is < 0 or > 3650)
        {
            throw new CommandRejectedException("invalid_dates", "Lead days are 0 to 3650.");
        }
    }

    private static async Task<GoalInfo> GoalAsync(IKernelUnitOfWork uow, CommandContext context, long goalId, CancellationToken ct)
    {
        var goal = (await uow.Store<IGoalStore>().ListAsync(true, ct)).FirstOrDefault(g => g.GoalId == goalId);
        return goal is null || !Visible(context.Principal, goal.ProgramId)
            ? throw new CommandRejectedException("goal_not_found", $"Goal {goalId} does not exist.")
            : goal;
    }

    private static Guid? Same(Guid? chosen, Guid? linked) =>
        chosen is { } a && linked is { } b && a != b
            ? throw new CommandRejectedException("program_mismatch", "The goal's program differs from the program of what it follows.")
            : chosen ?? linked;

    private static ProgramId? Program(GoalInfo g) => g.ProgramId is { } p ? new ProgramId(p) : null;

    private static bool Visible(ForgePrincipal principal, Guid? program) => program is not { } p || principal.Programs.Contains(new ProgramId(p));
}
