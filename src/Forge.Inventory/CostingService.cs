using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Inventory;

public sealed record SetCostingPolicy(CostingMethod Method, string? Currency, decimal? LaborRatePerHour, decimal? OverheadRatePerHour);

public sealed record SetStandardCost(string PartNumber, decimal Material, decimal Labor, decimal Overhead);

public sealed record CostingChanged(string Kind, string Key, string Message) : IDomainEvent
{
    public static string EventType => nameof(CostingChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// The costing policy and standard costs (Sprint 10 WW-S10-24, owner decision 2026-10-25: US GAAP practice, standard cost with full
/// absorption, kept by a Cost Accountant). Every change takes effect when it is recorded, needs a reason, and never rewrites what was
/// valued before. No ledger entry is posted here: Forge values quantities it already records.
/// </summary>
public sealed class CostingService(CommandPipeline pipeline)
{
    public const string SourceContext = "Inventory";
    private const decimal MaxAmount = 100_000_000m;

    public Task<CommandResult> SetPolicyAsync(SetCostingPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(SetCostingPolicy), Permissions.CostingManage, ResourceRef.Of("CostingPolicy", "plant"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (!Enum.IsDefined(c.Method))
                throw new CommandRejectedException("invalid_method", "Choose standard cost, weighted average or FIFO.");
            if (c.Method != CostingMethod.Standard)
                throw new CommandRejectedException("method_not_available",
                    "Weighted average and FIFO need actual costs recorded on receipts and production, which Forge does not keep yet. Standard cost stays in force.");
            var currency = Currency(c.Currency);
            if (c.LaborRatePerHour is < 0 or > 100_000 || c.OverheadRatePerHour is < 0 or > 100_000)
                throw new CommandRejectedException("invalid_rate", "Labor and overhead rates are per direct labor hour, from 0 to 100,000.");
            var store = uow.Store<ICostingStore>();
            var prior = Costing.PolicyAt(await store.ListPoliciesAsync(ct), now);
            var policy = new CostingPolicy(0, c.Method, currency, c.LaborRatePerHour, c.OverheadRatePerHour, now, context.Actor, context.Reason!);
            var id = await store.InsertPolicyAsync(policy, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            var message = $"Costing policy from {now:yyyy-MM-dd}: {Words(c.Method)} in {currency}, labor {Rate(c.LaborRatePerHour, currency)} and overhead {Rate(c.OverheadRatePerHour, currency)} per direct labor hour. Earlier valuations are unchanged.";
            return new CommandOutcome(CommandResult.Ok(key, message),
                [pipeline.Event(new CostingChanged("CostingPolicy", key, message), context, SourceContext, "CostingPolicy", key)],
                new AuditChange("CostingPolicy", key, prior is null ? null : CanonicalJson.Serialize(prior), CanonicalJson.Serialize(policy with { PolicyId = id })));
        }, ct);
    }

    public Task<CommandResult> SetStandardCostAsync(SetStandardCost c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(SetStandardCost), Permissions.CostingManage, ResourceRef.Of("StandardCost", part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) is null)
                throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            if (c.Material is < 0 or > MaxAmount || c.Labor is < 0 or > MaxAmount || c.Overhead is < 0 or > MaxAmount)
                throw new CommandRejectedException("invalid_cost", "Material, labor and overhead are non-negative unit amounts.");
            var store = uow.Store<ICostingStore>();
            var policy = Costing.PolicyAt(await store.ListPoliciesAsync(ct), now)
                ?? throw new CommandRejectedException("no_costing_policy", "Record a costing policy first.");
            var prior = Costing.StandardAt(await store.ListStandardCostsAsync(part, ct), part, now);
            var cost = new StandardCost(0, part, c.Material, c.Labor, c.Overhead, policy.Currency, now, context.Actor, context.Reason!);
            var id = await store.InsertStandardCostAsync(cost, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            var message = $"Standard cost of {part} from {now:yyyy-MM-dd}: {Money(cost.Total, policy.Currency)} a unit (material {Money(c.Material, policy.Currency)}, labor {Money(c.Labor, policy.Currency)}, overhead {Money(c.Overhead, policy.Currency)})"
                + (prior is null ? "." : $", was {Money(prior.Total, prior.Currency)}. Earlier valuations are unchanged.");
            return new CommandOutcome(CommandResult.Ok(key, message),
                [pipeline.Event(new CostingChanged("StandardCost", part, message), context, SourceContext, "StandardCost", part)],
                new AuditChange("StandardCost", part, prior is null ? null : CanonicalJson.Serialize(prior), CanonicalJson.Serialize(cost with { CostId = id })));
        }, ct);
    }

    private static string Currency(string? raw)
    {
        var text = string.IsNullOrWhiteSpace(raw) ? "USD" : raw.Trim().ToUpperInvariant();
        return text.Length == 3 && text.All(char.IsAsciiLetterUpper) ? text : throw new CommandRejectedException("invalid_currency", "Use a three-letter ISO currency code such as USD.");
    }

    private static string Words(CostingMethod method) => method switch
    {
        CostingMethod.Standard => "standard cost (material, labor and overhead)",
        CostingMethod.WeightedAverage => "weighted average",
        _ => "FIFO",
    };

    public static string Money(decimal amount, string currency) => $"{amount.ToString("#,##0.00##", CultureInfo.InvariantCulture)} {currency}";

    private static string Rate(decimal? rate, string currency) => rate is { } r ? Money(r, currency) : "not set";
}
