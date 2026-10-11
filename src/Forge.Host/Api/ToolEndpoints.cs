using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Resources;
using Forge.Resources.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ToolTypeRequest(string Code, string Name, ToolKind Kind, int? CalibrationDays, int? UsageLimit, int? ServiceUses, string? Reason);

public sealed record ToolRequest(string Code, string TypeCode, string Description, string? Serial, string? Location, string[]? EligibleMachines, string[]? EligibleActivities,
    DateOnly? CalibratedOn, DateOnly? CalibrationDue, string? Certificate, string? Reason);

public sealed record ToolMoveRequest(string? Location, ToolCustody Custody, string? Custodian, string? Reason);

public sealed record ToolCalibrationRequest(DateOnly CalibratedOn, bool Passed, DateOnly? DueOn, string? Certificate, string? Reason);

public sealed record ToolServiceRequest(string Vendor, string? Note, string? Reason);

public sealed record ToolReturnRequest(ToolCondition Condition, string? Location, DateOnly? CalibratedOn, DateOnly? DueOn, string? Certificate, string? Reason);

public sealed record ToolConditionRequest(ToolCondition Condition, string? Reason);

public sealed record ToolUseRequest(int Uses, string? MachineCode, string? ActivityCode, string? Reference, string? Reason);

public sealed record ToolRequirementRequest(string ActivityCode, string? MachineCode, string ToolTypeCode, int? Quantity, string? Reason);

/// <summary>A tool now: what it is, where, and whether it may be used.</summary>
public sealed record ToolView(ToolInfo Tool, ToolTypeInfo Type, ToolValidity Validity);

/// <summary>V3.8: tools as resources — types, tools, moves, calibration, service cycles, uses and validity at a moment.</summary>
internal static class ToolEndpoints
{
    public static void MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/resources").RequireAuthorization();
        api.MapGet("/tool-types", (HttpContext http, CancellationToken _) =>
            ReadStore<IToolLookup>(http, Permissions.ResourcesRead, async (l, ct) => await l.ListToolTypesAsync(ct)));
        api.MapGet("/tools", (HttpContext http, DateTimeOffset? at, string? machine, string? activity, CancellationToken _) =>
            ReadStore<IToolLookup>(http, Permissions.ResourcesRead, async (l, ct) =>
            {
                var when = at ?? http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                var types = (await l.ListToolTypesAsync(ct)).ToDictionary(t => t.Code, StringComparer.Ordinal);
                var events = (await l.ListToolEventsAsync(null, ct)).ToLookup(e => e.ToolCode);
                return (await l.ListToolsAsync(ct)).Select(t => new ToolView(t, types[t.TypeCode], ToolValidity.At(t, types[t.TypeCode], events[t.Code], when, Code(machine), Code(activity)))).ToList();
            }));
        api.MapGet("/tools/{code}/history", (HttpContext http, string code, CancellationToken _) =>
            ReadStore<IToolLookup>(http, Permissions.ResourcesRead, async (l, ct) => await l.ListToolEventsAsync(code.Trim().ToUpperInvariant(), ct)));

        api.MapGet("/tool-requirements", (HttpContext http, CancellationToken _) =>
            ReadStore<IToolLookup>(http, Permissions.ResourcesRead, async (l, ct) => await l.ListToolRequirementsAsync(ct)));
        api.MapPost("/tool-requirements", (HttpContext http, ToolRequirementRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetRequirementAsync(new SetToolRequirement(b.ActivityCode, b.MachineCode, b.ToolTypeCode, b.Quantity ?? 1), c, ct)));
        api.MapPost("/tool-requirements/{id:long}/remove", (HttpContext http, long id, ReasonRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveRequirementAsync(new RemoveToolRequirement(id), c, ct)));

        api.MapPost("/tool-types", (HttpContext http, ToolTypeRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineTypeAsync(new DefineToolType(b.Code, b.Name, b.Kind, b.CalibrationDays, b.UsageLimit, b.ServiceUses), c, ct)));
        api.MapPost("/tools", (HttpContext http, ToolRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RegisterAsync(new RegisterTool(b.Code, b.TypeCode, b.Description, b.Serial, b.Location, b.EligibleMachines, b.EligibleActivities, b.CalibratedOn, b.CalibrationDue, b.Certificate), c, ct)));
        api.MapPost("/tools/{code}/move", (HttpContext http, string code, ToolMoveRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.MoveAsync(new MoveTool(code, b.Location, b.Custody, b.Custodian), c, ct)));
        api.MapPost("/tools/{code}/calibrations", (HttpContext http, string code, ToolCalibrationRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CalibrateAsync(new CalibrateTool(code, b.CalibratedOn, b.Passed, b.DueOn, b.Certificate), c, ct)));
        api.MapPost("/tools/{code}/service", (HttpContext http, string code, ToolServiceRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SendForServiceAsync(new SendToolForService(code, b.Vendor, b.Note), c, ct)));
        api.MapPost("/tools/{code}/return", (HttpContext http, string code, ToolReturnRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReturnFromServiceAsync(new ReturnToolFromService(code, b.Condition, b.Location, b.CalibratedOn, b.DueOn, b.Certificate), c, ct)));
        api.MapPost("/tools/{code}/condition", (HttpContext http, string code, ToolConditionRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeConditionAsync(new ChangeToolCondition(code, b.Condition), c, ct)));
        api.MapPost("/tools/{code}/uses", (HttpContext http, string code, ToolUseRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UseAsync(new UseTool(code, b.Uses, b.MachineCode, b.ActivityCode, b.Reference), c, ct)));
        api.MapPost("/tools/{code}/retire", (HttpContext http, string code, ReasonRequest b, ToolService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RetireAsync(new RetireTool(code), c, ct)));
    }

    private static string? Code(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToUpperInvariant();
}
