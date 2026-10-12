using Forge.Engineering.Contracts;
using Forge.Kernel.Identity;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Procurement;
using Forge.Procurement.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record RfqCreateRequest(long RequisitionId, string Title, IReadOnlyList<string> SupplierCodes, string? Reason);
public sealed record SupplierQuoteRequest(string SupplierCode, decimal Quantity, decimal UnitPrice, string Currency, DateOnly PromisedDate, DateOnly ValidThrough,
    string? ProposedAlternatePart, string OriginalDocumentNumber, string OriginalDocumentRevision, string? Note, string? Reason);
public sealed record SourceSelectionRequest(long QuoteId, string? Reason);
public sealed record RfqView(RfqInfo Rfq, RequisitionInfo Need);

public static class SourcingEndpoints
{
    public static void MapSourcingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/procurement").RequireAuthorization();
        api.MapGet("/rfqs", async (HttpContext http, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            BusinessSourceReadAccess.Demand(http, principal, Permissions.PurchasingRead);
            var authorizer = http.RequestServices.GetRequiredService<IForgeAuthorizer>();
            await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
            var views = new List<RfqView>();
            foreach (var rfq in await uow.Store<ISourcingLookup>().ListRfqsAsync(http.RequestAborted))
            {
                var need = await uow.Store<IRequisitionLookup>().FindRequisitionAsync(rfq.RequisitionId, http.RequestAborted);
                if (need is null || !authorizer.Authorize(principal, Permissions.PurchasingRead,
                    await SourcingService.ResourceForRfqAsync(uow, rfq, null, ct)).IsPermitted) continue;
                var visible = rfq;
                if (rfq.SelectedQuoteId is { } selected)
                {
                    var quote = (await uow.Store<ISourcingLookup>().ListQuotesAsync(rfq.RfqId, http.RequestAborted)).FirstOrDefault(q => q.QuoteId == selected);
                    var original = quote is null ? null : await uow.Store<IDocumentLookup>().FindDocumentAsync(quote.OriginalDocumentNumber, http.RequestAborted);
                    if (original is null || !DocumentAccess.CanSee(principal, original))
                        visible = visible with { SelectedQuoteId = null, SelectedBy = null, SelectedAt = null, SelectionReason = null };
                }
                if (rfq.Package is { } package)
                {
                    var documents = new List<PackageDocument>();
                    foreach (var sent in package.Documents)
                    {
                        var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(sent.DocumentNumber, http.RequestAborted);
                        documents.Add(document is not null && DocumentAccess.CanSee(principal, document) ? sent
                            : new PackageDocument("Restricted document", string.Empty, null, "Restricted", "Controlled package", null, null));
                    }

                    visible = visible with { Package = package with { Documents = documents } };
                }

                views.Add(new RfqView(visible, need));
            }

            return Results.Ok(views);
        });
        api.MapPost("/rfqs", (HttpContext http, RfqCreateRequest b, SourcingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateRfq(b.RequisitionId, b.Title, b.SupplierCodes), c, ct)));
        api.MapPost("/rfqs/{id:long}/release", (HttpContext http, long id, ReasonRequest b, SourcingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseRfq(id), c, ct)));
        api.MapPost("/rfqs/{id:long}/cancel", (HttpContext http, long id, ReasonRequest b, SourcingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelAsync(new CancelRfq(id), c, ct)));
        api.MapGet("/rfqs/{id:long}/quotes", async (HttpContext http, long id, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            BusinessSourceReadAccess.Demand(http, principal, Permissions.PurchasingRead);
            var authorizer = http.RequestServices.GetRequiredService<IForgeAuthorizer>();
            await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
            var rfq = await uow.Store<ISourcingLookup>().FindRfqAsync(id, http.RequestAborted);
            var need = rfq is null ? null : await uow.Store<IRequisitionLookup>().FindRequisitionAsync(rfq.RequisitionId, http.RequestAborted);
            if (need is null || rfq is null || !authorizer.Authorize(principal, Permissions.PurchasingRead,
                await SourcingService.ResourceForRfqAsync(uow, rfq, null, ct)).IsPermitted)
                throw new Forge.Kernel.Commands.CommandRejectedException("rfq_not_found", "RFQ does not exist.");
            var quotes = new List<SupplierQuote>();
            foreach (var quote in await uow.Store<ISourcingLookup>().ListQuotesAsync(id, http.RequestAborted))
            {
                var original = await uow.Store<IDocumentLookup>().FindDocumentAsync(quote.OriginalDocumentNumber, http.RequestAborted);
                if (original is not null && DocumentAccess.CanSee(principal, original)
                    && authorizer.Authorize(principal, Permissions.PurchasingRead, await SourcingService.ResourceForRfqAsync(uow, rfq, quote, ct)).IsPermitted) quotes.Add(quote);
            }

            return Results.Ok(quotes);
        });
        api.MapPost("/rfqs/{id:long}/quotes", (HttpContext http, long id, SupplierQuoteRequest b, SourcingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordQuoteAsync(new RecordSupplierQuote(id, b.SupplierCode, b.Quantity, b.UnitPrice, b.Currency, b.PromisedDate, b.ValidThrough,
                b.ProposedAlternatePart, b.OriginalDocumentNumber, b.OriginalDocumentRevision, b.Note), c, ct)));
        api.MapPost("/rfqs/{id:long}/select", (HttpContext http, long id, SourceSelectionRequest b, SourcingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SelectAsync(new SelectSupplierQuote(id, b.QuoteId), c, ct)));
    }
}
