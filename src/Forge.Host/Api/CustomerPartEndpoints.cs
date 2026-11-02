using Forge.Kernel.Security;
using Forge.Sales;
using Forge.Sales.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CustomerPartRequest(string CustomerPart, string? PartNumber, string? Description, string? Reason);

/// <summary>Each customer's own part numbers for our parts (owner decisions 2026-11-01), used to read and write their EDI.</summary>
internal static class CustomerPartEndpoints
{
    public static void MapCustomerPartEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/sales").RequireAuthorization();
        api.MapGet("/customer-parts", (HttpContext http, string? customer, CancellationToken _) =>
            ReadStore<ICustomerPartLookup>(http, Permissions.SalesRead, async (s, ct) => await s.ListCustomerPartsAsync(string.IsNullOrWhiteSpace(customer) ? null : customer.Trim().ToUpperInvariant(), ct)));
        api.MapPost("/customers/{code}/parts", (HttpContext http, string code, CustomerPartRequest b, CustomerPartService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetAsync(new SetCustomerPart(code, b.CustomerPart, string.IsNullOrWhiteSpace(b.PartNumber) ? null : b.PartNumber, b.Description), c, ct)));
    }
}
