using Forge.Kernel.Identity;

namespace Forge.Kernel.Security;

/// <summary>
/// What is being acted on, described by the attributes authorization needs. A resource with no
/// program is not program-segregated (for example a person record or a shared machine).
/// </summary>
public sealed record ResourceRef(
    string Type,
    string? Id = null,
    ProgramId? Program = null,
    DataMarking Markings = DataMarking.None)
{
    public static ResourceRef Of(string type, object? id = null) => new(type, id?.ToString());
}
