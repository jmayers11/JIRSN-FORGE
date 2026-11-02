using Forge.Kernel.Access;
using Forge.Kernel.Commands;

namespace Forge.Resources.Contracts;

/// <summary>One part of Forge a plant may turn off when it does not use it (owner decision 2026-11-01).</summary>
public sealed record SiteFeatureDefinition(string Code, string Name, string Description);

/// <summary>A feature and whether this plant uses it. A feature nobody has switched is on.</summary>
public sealed record SiteFeatureState(string Code, string Name, string Description, bool Enabled, string? UpdatedBy, DateTimeOffset? UpdatedAt);

public interface ISiteFeatureLookup
{
    Task<IReadOnlyList<SiteFeatureState>> ListFeaturesAsync(CancellationToken ct);
}

/// <summary>
/// The parts of Forge a company can turn off (owner decision 2026-11-01: add them, and give the company the ability to disable them
/// if not needed). Each starts on. Turning one off stops its commands and checks; nothing recorded is deleted, and turning it on again
/// carries on from where it was.
/// </summary>
public static class SiteFeatures
{
    public const string Receivables = "receivables";
    public const string SupplierInvoices = "supplier-invoices";
    public const string ShippingCarriers = "shipping-carriers";
    public const string Backflush = "backflush";

    public static readonly IReadOnlyList<SiteFeatureDefinition> All =
    [
        new(Receivables, "Customer payments and credit holds",
            "Billing clerks record customer payments; open balances and aging; customers over their credit limit or past due wait for a credit release before new orders are approved or anything ships."),
        new(SupplierInvoices, "Supplier invoices and three-way match",
            "Accounts payable enters supplier invoices; each is matched to the purchase order and what was received, and differences beyond the plant's tolerance wait for a purchasing manager."),
        new(ShippingCarriers, "Shipping carriers",
            "Rates, labels and tracking through UPS, FedEx, USPS, ShipEngine or EasyPost, plus bills of lading and tracking numbers entered by hand for freight."),
        new(Backflush, "Automatic material deduction (backflush)",
            "Reporting production at an operation deducts the components that operation uses, for parts set to backflush. A shortage is flagged to inventory; production is never stopped."),
    ];

    public static SiteFeatureDefinition Find(string code) =>
        All.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? throw new CommandRejectedException("unknown_feature", $"There is no feature '{code}'. Choose {string.Join(", ", All.Select(f => f.Code))}.");

    /// <summary>Whether the plant uses a feature, read inside the caller's transaction.</summary>
    public static async Task<bool> IsOnAsync(this IKernelUnitOfWork uow, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var feature = Find(code);
        return (await uow.Store<ISiteFeatureLookup>().ListFeaturesAsync(ct)).FirstOrDefault(f => f.Code == feature.Code)?.Enabled ?? true;
    }

    /// <summary>Refuses a command of a feature the plant has turned off.</summary>
    public static async Task RequireOnAsync(this IKernelUnitOfWork uow, string code, CancellationToken ct)
    {
        if (!await uow.IsOnAsync(code, ct))
            throw new CommandRejectedException("feature_disabled", $"{Find(code).Name} is turned off for this plant. A security administrator can turn it on under Settings → Features.");
    }
}
