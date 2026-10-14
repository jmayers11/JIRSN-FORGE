using Forge.Kernel.Security;

namespace Forge.Kernel.Commands;

public enum CommandSource { Api, Terminal, Integration, System }

public sealed record CommandContext(ForgePrincipal Principal, Guid CorrelationId, string IdempotencyKey,
    CommandSource Source, string? Reason, string? SystemActor = null, Guid? CausationId = null)
{
    public string Actor => SystemActor ?? "person:" + Principal.PersonId;
    public CommandSessionReference? ScanSession { get; init; }
    public CommandAuthenticationEvidence? Authentication { get; init; }
}

public sealed record CommandResult(bool Succeeded, string Code, string Message, string? EntityId = null)
{
    public static CommandResult Ok(string entityId, string message) => new(true, "ok", message, entityId);
    public static CommandResult NoChange(string entityId, string message) => new(true, "no_change", message, entityId);
}

public sealed class CommandRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class IdempotencyConflictException(string key) : Exception($"Idempotency key '{key}' was already used for a different request.")
{
    public string Key { get; } = key;
}
