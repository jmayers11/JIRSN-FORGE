namespace Forge.Quality.Qms;

/// <summary>Explicit deployment identity; this provides no tenancy or inferred organization policy.</summary>
public sealed record QmsOptions(string? OrganizationCode)
{
    public bool Configured => OrganizationCode is not null;
}
