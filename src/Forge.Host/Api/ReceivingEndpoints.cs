using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Quality;
using Forge.Quality.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ReceivingInspectionRequest(decimal Accepted, decimal Rejected, string? Defect, ReceivingCharacteristicResult[]? Results, string? Reason);

public sealed record RejectionDispositionRequest(SupplierDisposition Disposition, string? Reason);

public sealed record RtvShipRequest(string? Carrier, string? Reason);

public sealed record RtvReplacementRequest(decimal Quantity, string? LotNumber, string LocationCode, bool? Inspect, string? Reason);

public sealed record CustomerReturnRequest(long ShipmentId, string PartNumber, string? LotNumber, decimal Quantity, string LocationCode, string? Reason);

public sealed record CustomerReturnDispositionRequest(CustomerReturnDisposition Disposition, string? Reason);

public sealed record FaiDeterminationRequest(string PartNumber, string Revision, string SupplierCode, DeterminationState State, string? Reason);

public sealed record FaiRequest(string PartNumber, string Revision, string SupplierCode, bool Passed, ReceivingCharacteristicResult[]? Results, string DocumentNumber, string DocumentRevision, string? Reason);

/// <summary>A receipt into inspection and where it stands, with its FAI status.</summary>
public sealed record ReceiptView(InventoryTransaction Receipt, string? SupplierCode, decimal Accepted, decimal Rejected, decimal Remaining, FaiStatus FaiStatus, string FaiSubject, IReadOnlyList<ReceivingInspection> Inspections);

/// <summary>V3.11: receiving inspection with partial acceptance and FAI, supplier dispositions, returns to vendor and customer returns.</summary>
internal static class ReceivingEndpoints
{
    public static void MapReceivingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/inventory").RequireAuthorization();
        api.MapGet("/receiving", (HttpContext http, int? take, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var views = new List<ReceiptView>();
                foreach (var receipt in await uow.Store<IReceivingLookup>().ListInspectionReceiptsAsync(take ?? 100, http.RequestAborted))
                {
                    var state = await ReceivingQueries.ReceiptStateAsync(uow, receipt, http.RequestAborted);
                    var (fai, subject) = await ReceivingQueries.FaiStatusAsync(uow, receipt, state.SupplierCode, http.RequestAborted);
                    views.Add(new ReceiptView(receipt, state.SupplierCode, state.Accepted, state.Rejected, state.Remaining, fai, subject, state.Inspections));
                }

                return views;
            }));
        api.MapPost("/receiving/{receipt:long}/inspections", (HttpContext http, long receipt, ReceivingInspectionRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.InspectReceiptAsync(new InspectReceipt(receipt, b.Accepted, b.Rejected, b.Defect, b.Results), c, ct)));

        api.MapGet("/rejections", (HttpContext http, bool? open, CancellationToken _) =>
            ReadStore<IReceivingLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListRejectionsAsync(open ?? false, ct)));
        api.MapPost("/rejections/{id:long}/disposition", (HttpContext http, long id, RejectionDispositionRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DispositionRejectionAsync(new DispositionRejection(id, b.Disposition), c, ct)));
        api.MapPost("/rejections/{id:long}/approve", (HttpContext http, long id, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveUseAsIsAsync(new ApproveUseAsIs(id), c, ct)));

        api.MapGet("/rtvs", (HttpContext http, bool? open, CancellationToken _) =>
            ReadStore<IReceivingLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListRtvsAsync(open ?? false, ct)));
        api.MapPost("/rtvs/{id:long}/ship", (HttpContext http, long id, RtvShipRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ShipReturnToVendorAsync(new ShipReturnToVendor(id, b.Carrier), c, ct)));
        api.MapPost("/rtvs/{id:long}/replacement", (HttpContext http, long id, RtvReplacementRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReceiveReplacementAsync(new ReceiveReplacement(id, b.Quantity, b.LotNumber, b.LocationCode, b.Inspect ?? true), c, ct)));
        api.MapPost("/rtvs/{id:long}/credit", (HttpContext http, long id, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreditReturnToVendorAsync(new CreditReturnToVendor(id), c, ct)));

        api.MapGet("/customer-returns", (HttpContext http, long? shipmentId, CancellationToken _) =>
            ReadStore<IReceivingLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListCustomerReturnsAsync(shipmentId, ct)));
        api.MapPost("/customer-returns", (HttpContext http, CustomerReturnRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReceiveCustomerReturnAsync(new ReceiveCustomerReturn(b.ShipmentId, b.PartNumber, b.LotNumber, b.Quantity, b.LocationCode), c, ct)));
        api.MapPost("/customer-returns/{id:long}/disposition", (HttpContext http, long id, CustomerReturnDispositionRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DispositionCustomerReturnAsync(new DispositionCustomerReturn(id, b.Disposition), c, ct)));

        var quality = app.MapGroup("/api/quality").RequireAuthorization();
        quality.MapGet("/determinations", (HttpContext http, string? subject, CancellationToken _) =>
            ReadStore<IDeterminationLookup>(http, Permissions.QualityRead, async (l, ct) => await l.ListDeterminationsAsync(null, subject, ct)));
        quality.MapPost("/fai-determinations", (HttpContext http, FaiDeterminationRequest b, DeterminationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DetermineFaiAsync(new DetermineFai(b.PartNumber, b.Revision, b.SupplierCode, b.State), c, ct)));
        quality.MapGet("/fai", (HttpContext http, string? subject, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var visible = new List<FaiRecord>();
                foreach (var fai in await uow.Store<IDeterminationLookup>().ListFaisAsync(subject, http.RequestAborted))
                {
                    var document = await uow.Store<Forge.Engineering.Contracts.IDocumentLookup>().FindDocumentAsync(fai.DocumentNumber, http.RequestAborted);
                    if (document is not null && Forge.Engineering.Contracts.DocumentAccess.CanSee(principal, document))
                    {
                        visible.Add(fai);
                    }
                }

                return visible;
            }));
        quality.MapPost("/fai", (HttpContext http, FaiRequest b, DeterminationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordFaiAsync(new RecordFai(b.PartNumber, b.Revision, b.SupplierCode, b.Passed, b.Results ?? [], b.DocumentNumber, b.DocumentRevision), c, ct)));
    }
}


