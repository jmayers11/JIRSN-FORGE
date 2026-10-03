using Forge.Kernel.Security;

namespace Forge.Kernel.Commands;

/// <summary>Where a command came from, for audit.</summary>
public enum CommandSource
{
    Api,
    Terminal,
    Integration,
    System,
}

/// <summary>
/// Everything a command handler needs to know about the request besides its data: who, why, how it
/// is correlated, and the idempotency key that makes retries safe.
/// </summary>
public sealed record CommandContext(
    ForgePrincipal Principal,
    Guid CorrelationId,
    string IdempotencyKey,
    CommandSource Source,
    string? Reason,
    string? SystemActor = null)
{
    /// <summary>Who the audit names: the person, or a named system actor (for example system:propagation).</summary>
    public string Actor => SystemActor ?? "person:" + Principal.PersonId;
}

/// <summary>Outcome of a command, stable enough to be replayed for a repeated idempotency key.</summary>
public sealed record CommandResult(bool Succeeded, string Code, string Message, string? EntityId = null)
{
    public static CommandResult Ok(string entityId, string message) => new(true, "ok", message, entityId);

    public static CommandResult NoChange(string entityId, string message) => new(true, "no_change", message, entityId);
}

/// <summary>The command violated a business rule. Nothing was written.</summary>
public sealed class CommandRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The idempotency key was reused for a different request.</summary>
public sealed class IdempotencyConflictException(string key)
    : Exception($"Idempotency key '{key}' was already used for a different request.")
{
    public string Key { get; } = key;
}
