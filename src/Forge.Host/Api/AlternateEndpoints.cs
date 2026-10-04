using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record AlternateRequest(
    string PrimaryPart,
    string AlternatePart,
    AlternateKind Kind,
    decimal? Factor,
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
    string? ChangeNumber,
    string? Reason);

public sealed record LinePolicyRequest(SubstitutionPolicy Policy, string? Reason);

/// <summary>A BOM that uses a part: the parent revision, the line, and whether that revision is in force.</summary>
public sealed record WhereUsedLine(string ParentPart, string Revision, int LineNumber, decimal QuantityPer, SubstitutionPolicy Policy, bool InForce);

/// <summary>Where-used in both directions (V2.6): the BOMs a part is on, its alternates, and what it is an alternate for.</summary>
public sealed record WhereUsedView(string PartNumber, IReadOnlyList<WhereUsedLine> UsedIn, IReadOnlyList<AlternateInfo> Alternates, IReadOnlyList<AlternateInfo> AlternateFor);

/// <summary>V2.6 endpoints: alternates, where-used, candidates, BOM line substitution policy.</summary>
public static class AlternateEndpoints
{
    public static void MapAlternateEndpoints(this IEndpointRouteBuilder app)
    {
        var eng = app.MapGroup("/api/engineering").RequireAuthorization();

        eng.MapGet("/alternates", (HttpContext http, string? part, AlternateStatus? status, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var p = part?.Trim().ToUpperInvariant();
                return (await uow.Store<IAlternateLookup>().ListAlternatesAsync(http.RequestAborted))
                    .Where(a => Visible(principal, a) && (p is null || a.PrimaryPart == p || a.AlternatePart == p) && (status is null || a.Status == status))
                    .OrderByDescending(a => a.AlternateId)
                    .ToList();
            }));
        eng.MapGet("/alternates/{id:long}", (HttpContext http, long id, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var a = await uow.Store<IAlternateLookup>().FindAlternateAsync(id, http.RequestAborted);
                return a is not null && Visible(principal, a) ? a : throw new CommandRejectedException("alternate_not_found", $"Alternate {id} does not exist.");
            }));
        eng.MapGet("/alternates/where-used/{part}", (HttpContext http, string part, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var p = part.Trim().ToUpperInvariant();
                var engineering = uow.Store<IEngineeringLookup>();
                if (await engineering.FindItemAsync(p, ct) is null)
                {
                    throw new CommandRejectedException("part_not_found", $"Part {p} does not exist.");
                }

                var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                var usedIn = (await engineering.ListReleasedStructuresAsync(ct))
                    .SelectMany(s => s.Bom.Where(l => l.ComponentPartNumber == p).Select(l => new WhereUsedLine(s.Revision.PartNumber, s.Revision.Revision, l.LineNumber, l.QuantityPer, l.Policy, s.Revision.IsEffectiveAt(now))))
                    .OrderBy(w => w.ParentPart, StringComparer.Ordinal).ThenBy(w => w.Revision, StringComparer.Ordinal).ToList();
                var all = (await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct)).Where(a => Visible(principal, a)).ToList();
                return new WhereUsedView(p, usedIn,
                    all.Where(a => a.PrimaryPart == p || (a.Kind == AlternateKind.Interchangeable && a.AlternatePart == p)).ToList(),
                    all.Where(a => a.AlternatePart == p || (a.Kind == AlternateKind.Interchangeable && a.PrimaryPart == p)).ToList());
            }));
        eng.MapGet("/alternates/candidates/{part}", (HttpContext http, string part, Guid? program, string? customer, string? parent, string? asset, string? machine, int? operation, string? supplier, DateOnly? date, bool? maintenance, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                if (program is { } pid && !principal.Programs.Contains(new ProgramId(pid)))
                {
                    throw new CommandRejectedException("program_not_found", "Choose a program you have access to.");
                }

                await using var uow = await Snapshot(http);
                var today = date ?? DateOnly.FromDateTime(http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
                var context = new AlternateContext(today, Upper(parent), program, string.IsNullOrWhiteSpace(customer) ? null : customer.Trim(), Upper(asset), Upper(machine), operation, Upper(supplier), maintenance == true);
                var all = (await uow.Store<IAlternateLookup>().ListAlternatesAsync(http.RequestAborted)).Where(a => Visible(principal, a));
                return Alternates.Candidates(all, part.Trim().ToUpperInvariant(), context);
            }));

        eng.MapPost("/alternates", (HttpContext http, AlternateRequest b, AlternateService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeAsync(new ProposeAlternate(b.PrimaryPart, b.AlternatePart, b.Kind, b.Factor ?? 1m, b.ProgramId, b.Customer, b.ParentPart, b.AssetCode, b.MachineCode,
                b.OperationSequence, b.SupplierCode, b.ValidFrom, b.ValidTo, b.QualityNotes, b.Evidence, b.ChangeNumber), c, ct)));
        eng.MapPost("/alternates/{id:long}/approve", async (HttpContext http, long id, ReasonRequest b, AlternateService s, CancellationToken ct) =>
        {
            var kind = await KindAsync(http, id, ct);
            return await Command(http, b.Reason, c => s.ApproveAsync(new ApproveAlternate(id), kind, c, ct));
        });
        eng.MapPost("/alternates/{id:long}/reject", async (HttpContext http, long id, ReasonRequest b, AlternateService s, CancellationToken ct) =>
        {
            var kind = await KindAsync(http, id, ct);
            return await Command(http, b.Reason, c => s.RejectAsync(new RejectAlternate(id), kind, c, ct));
        });
        eng.MapPost("/alternates/{id:long}/expire", async (HttpContext http, long id, ReasonRequest b, AlternateService s, CancellationToken ct) =>
        {
            var kind = await KindAsync(http, id, ct);
            return await Command(http, b.Reason, c => s.ExpireAsync(new ExpireAlternate(id), kind, c, ct));
        });
        eng.MapPost("/items/{part}/revisions/{rev}/bom/{line:int}/policy", (HttpContext http, string part, string rev, int line, LinePolicyRequest b, AlternateService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetPolicyAsync(new SetSubstitutionPolicy(part, rev, line, b.Policy), c, ct)));
    }

    private static async Task<AlternateKind> KindAsync(HttpContext http, long id, CancellationToken ct)
    {
        await using var uow = await Snapshot(http);
        return (await uow.Store<IAlternateLookup>().FindAlternateAsync(id, ct))?.Kind
            ?? throw new CommandRejectedException("alternate_not_found", $"Alternate {id} does not exist.");
    }

    private static bool Visible(ForgePrincipal principal, AlternateInfo a) => a.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p));

    private static string? Upper(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static Task<IKernelUnitOfWork> Snapshot(HttpContext http) =>
        http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
}
