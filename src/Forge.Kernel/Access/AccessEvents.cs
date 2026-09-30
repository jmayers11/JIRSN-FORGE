using Forge.Kernel.Events;

namespace Forge.Kernel.Access;

public sealed record PersonRegistered(Guid PersonId, string IdpSubject, string DisplayName, string Markings) : IDomainEvent
{
    public static string EventType => nameof(PersonRegistered);

    public static int SchemaVersion => 1;
}

public sealed record RoleAssigned(Guid PersonId, string Role) : IDomainEvent
{
    public static string EventType => nameof(RoleAssigned);

    public static int SchemaVersion => 1;
}

public sealed record RoleRevoked(Guid PersonId, string Role) : IDomainEvent
{
    public static string EventType => nameof(RoleRevoked);

    public static int SchemaVersion => 1;
}

public sealed record ProgramAccessGranted(Guid PersonId, Guid ProgramId) : IDomainEvent
{
    public static string EventType => nameof(ProgramAccessGranted);

    public static int SchemaVersion => 1;
}

public sealed record ProgramAccessRevoked(Guid PersonId, Guid ProgramId) : IDomainEvent
{
    public static string EventType => nameof(ProgramAccessRevoked);

    public static int SchemaVersion => 1;
}

/// <summary>Security-relevant: forwarded to Sentinel once that integration exists.</summary>
public sealed record AuthorizationDenied(string Permission, string ResourceType, string? ResourceId, IReadOnlyList<string> Reasons) : IDomainEvent
{
    public static string EventType => nameof(AuthorizationDenied);

    public static int SchemaVersion => 1;
}
