using System.Text.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Audit;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Planning;
using Forge.Quality.Qms;
using Forge.Host.WorkInbox;

namespace Forge.Host.History;

/// <summary>Historical audit bytes stay immutable; visibility follows their recorded and current source context.</summary>
public static class AuditAccess
{
    public static async Task<IReadOnlyList<ChainedAuditEntry>> FilterAsync(IKernelUnitOfWork uow,
        IReadOnlyList<ChainedAuditEntry> entries, ForgePrincipal principal, IWorkSourceResolver sources,
        IForgeAuthorizer authorizer, QmsService qms, CancellationToken ct)
    {
        var visible = new List<ChainedAuditEntry>();
        var planningSources = await MaterialSourceVisibility.LoadAsync(uow, principal, ct, authorizer: authorizer);
        foreach (var row in entries)
        {
            var entry = row.Entry;
            // A recorded planning explanation can name a supplying order or PO whose access
            // was later revoked. Suppress the record; retain its canonical bytes and hash.
            if (planningSources.HasRestrictedReference(entry.OldValue) || planningSources.HasRestrictedReference(entry.NewValue)) continue;
            // Person snapshots describe assigned clearance/program membership, rather than
            // classification/ownership of the administrative person record itself.
            var sourceSnapshot = entry.EntityType != "Person";
            var qmsSource = QmsSourceAccess.IsQms(entry.EntityType);
            var ownedSource = OwnedSourceFamilies.Contains(entry.EntityType);
            var source = await sources.ResolveAsync(uow, entry.EntityType, entry.EntityId, ct);
            // QMS facility ownership is immutable. Resolve that dimension before evaluating
            // a facility-scoped audit grant; historical program/marking bytes still apply.
            var captured = entry.OwningResource;
            var fallback = source is { Resources.Count: > 0 } ? source.Resources[0] : new ResourceRef(entry.EntityType, entry.EntityId);
            var recorded = (captured ?? fallback) with
            {
                Type = entry.EntityType, Id = entry.EntityId,
                Program = entry.ProgramId ?? (captured is null ? fallback.Program : captured.Program),
                Markings = entry.Markings | (captured?.Markings ?? DataMarking.None)
                    | (sourceSnapshot ? SnapshotMarkings(entry.OldValue) | SnapshotMarkings(entry.NewValue) : DataMarking.None),
            };
            if (!authorizer.Authorize(principal, Permissions.AuditRead, recorded).IsPermitted) continue;
            if (sourceSnapshot && SnapshotPrograms(entry.OldValue).Concat(SnapshotPrograms(entry.NewValue))
                .Any(program => !authorizer.Authorize(principal, Permissions.AuditRead, recorded with { Program = program }).IsPermitted)) continue;
            if (sourceSnapshot && SnapshotResources(entry.OldValue).Concat(SnapshotResources(entry.NewValue))
                .Any(resource => !authorizer.Authorize(principal, Permissions.AuditRead, resource).IsPermitted)) continue;
            if (ownedSource && !await OwnedSourceFamilies.CanReadAsync(uow, sources, principal, entry.EntityType, entry.EntityId, ct)) continue;
            if (qmsSource
                && !await QmsSourceAccess.CanSeeAsync(uow, qms, principal, entry.EntityType, entry.EntityId, ct)) continue;
            if (source is null && RequiresSource(entry.EntityType)) continue;
            if (source is not null && source.Resources.Any(resource => !authorizer.Authorize(principal, Permissions.AuditRead, resource).IsPermitted)) continue;
            visible.Add(row);
        }
        return visible;
    }

    private static bool RequiresSource(string type) => OwnedSourceFamilies.Contains(type) || type is "Document" or "DocumentRevision" or "DocumentLink"
        or "Ncr" or "NCR" or "Capa" or "CAPA" or "Hold" or "Order" or "ProductionOrder" or "OrderOperation"
        or "QmsRecord" or "QmsRevision";

    private static readonly JsonSerializerOptions ResourceJson = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    // Typed captured refs preserve facility/WC/contract/customer/shift alongside program/marks.
    // These are read checks only; neither audit snapshots nor chained entry bytes are rewritten.
    private static IReadOnlyList<ResourceRef> SnapshotResources(string? json)
    {
        if (json is null) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var result = new HashSet<ResourceRef>();
            Resources(document.RootElement, result);
            return result.ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static void Resources(JsonElement value, HashSet<ResourceRef> result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && value.TryGetProperty("id", out _)
                && (value.TryGetProperty("program", out _) || value.TryGetProperty("markings", out _)
                    || value.TryGetProperty("facilityId", out _) || value.TryGetProperty("workCenterId", out _)
                    || value.TryGetProperty("contractId", out _) || value.TryGetProperty("customerId", out _)
                    || value.TryGetProperty("shiftId", out _)))
            {
                var resource = JsonSerializer.Deserialize<ResourceRef>(value.GetRawText(), ResourceJson);
                if (resource is not null) result.Add(resource);
            }
            foreach (var property in value.EnumerateObject()) Resources(property.Value, result);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Resources(item, result);
    }

    // Early entries did not populate the existing markings column. Their canonical snapshots
    // retain explicit classification; use it for visibility without modifying the hash chain.
    private static DataMarking SnapshotMarkings(string? json)
    {
        if (json is null) return DataMarking.None;
        try
        {
            using var document = JsonDocument.Parse(json);
            return Markings(document.RootElement);
        }
        catch (JsonException) { return DataMarking.None; }
    }

    private static DataMarking Markings(JsonElement value)
    {
        var markings = DataMarking.None;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Equals("markings", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("targetMarkings", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number)) markings |= (DataMarking)number;
                    else if (property.Value.ValueKind == JsonValueKind.String && Enum.TryParse<DataMarking>(property.Value.GetString(), true, out var parsed)) markings |= parsed;
                }
                markings |= Markings(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) markings |= Markings(item);
        return markings;
    }

    private static IReadOnlyList<ProgramId> SnapshotPrograms(string? json)
    {
        if (json is null) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var programs = new HashSet<ProgramId>();
            Programs(document.RootElement, programs);
            return programs.ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static void Programs(JsonElement value, HashSet<ProgramId> programs)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Equals("program", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("programId", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("targetProgramId", StringComparison.OrdinalIgnoreCase))
                {
                    var reference = property.Value;
                    if (reference.ValueKind == JsonValueKind.Object && reference.TryGetProperty("value", out var wrapped)) reference = wrapped;
                    if (reference.ValueKind == JsonValueKind.String && reference.TryGetGuid(out var id)) programs.Add(new ProgramId(id));
                }
                Programs(property.Value, programs);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Programs(item, programs);
    }
}
