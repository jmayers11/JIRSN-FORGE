using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Host.History;

/// <summary>Shared presentation access delegates linked-source decisions to the owning QMS service.</summary>
internal static class QmsSourceAccess
{
    public static bool IsQms(string type) => type is "QmsRecord" or "QmsRevision";

    public static (string Code, int? Revision)? Subject(string type, string id)
    {
        if (type == "QmsRecord") return string.IsNullOrWhiteSpace(id) ? null : (id, null);
        if (type != "QmsRevision") return null;
        var separator = id.LastIndexOf('/');
        return separator > 0 && int.TryParse(id.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision > 0
            ? (id[..separator], revision) : null;
    }

    public static async Task<bool> CanSeeAsync(IKernelUnitOfWork uow, QmsService qms, ForgePrincipal principal,
        string type, string id, CancellationToken ct)
    {
        var subject = Subject(type, id);
        if (subject is null) return false;
        var record = await uow.Store<IQmsLookup>().FindAsync(subject.Value.Code, ct);
        return record is not null && await qms.CanReadAsync(uow, principal, record, ct, subject.Value.Revision);
    }
}
