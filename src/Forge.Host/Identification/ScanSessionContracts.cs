using Forge.Kernel.Access;
using Forge.Kernel.Events;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Host.Identification;

public enum ScanInputMode { Manual, KeyboardWedge, Camera, ConfiguredAdapter }
public enum ScanSessionState { Active, Invalidated, Superseded, Expired }
public sealed record ScanReadSource(string Permission, ResourceRef Resource);
public sealed record ScanSelection(string Kind, string Code, string SubjectCode, ScanInputMode InputMode,
    string? AdapterId, string? Symbology, DateTimeOffset RecordedAt, string SourceFingerprint,
    IReadOnlyList<ScanReadSource> Sources);
public sealed record ScanAccepted(Guid AcceptanceId, string CommandType, string? EntityType, string? EntityId,
    Guid CorrelationId, Guid? AuditId, IReadOnlyList<Guid> EventIds, Guid OperatorPersonId,
    string Actor, DateTimeOffset RecordedAt, int SessionVersion, string PolicyFingerprint,
    IReadOnlyList<ScanSelection> Selections, string EvidenceFingerprint);
public sealed record ScanAcceptedRecord(Guid SessionId, ScanAccepted Entry);
/// <summary>Minimized rejected-attempt telemetry; scan payloads and authentication secrets are never retained.</summary>
public sealed record ScanAttempt(Guid AttemptId, Guid SessionId, int SessionVersion, string CommandType,
    string Outcome, string ValidationCode, DateTimeOffset RecordedAt, Guid CorrelationId, Guid AuditId,
    Guid OperatorPersonId, string AuthenticatedActor, string? AssuranceSummary, string? WorkstationBindingHash,
    string? EntityType, string? EntityId);
public sealed record ScanSessionView(Guid Id, int Version, ScanSessionState State, Guid PrincipalPersonId,
    Guid OperatorPersonId, string ChannelHash, string AuthenticationBindingHash, string OperationCode, long OrderNumber, int Sequence,
    string FacilityCode, string WorkCenterCode, Guid? ProgramId, DataMarking Markings,
    string PartNumber, string PartRevision, string OperationFingerprint, string? SelectedMachineCode,
    IReadOnlyList<ScanSelection> Selections, IReadOnlyList<QmsRequiredScanKind> RequiredKinds,
    bool ManualAllowed, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, QmsAdoptedPolicy Policy,
    IReadOnlyList<ScanReadSource> Sources, string? InvalidatedBy = null, string? InvalidationReason = null,
    DateTimeOffset? InvalidatedAt = null, IReadOnlyList<ScanAccepted>? Accepted = null, string? CreationRequestFingerprint = null,
    IReadOnlyList<ScanAttempt>? Attempts = null);
public sealed record ScanRequirements(bool Adopted, bool Required, bool ManualAllowed,
    IReadOnlyList<QmsRequiredScanKind> RequiredKinds, QmsAdoptedPolicy? Policy, int? ExpiresInSeconds);
public sealed record CreateScanSession(string? OperationCode, long? OrderNumber, int? Sequence, Secret? Badge,
    string? MachineCode = null, Guid? PreviousSessionId = null);
public sealed record StageScan(int ExpectedVersion, string Code, ScanInputMode InputMode,
    string? AdapterId = null, string? Symbology = null);
public sealed record InvalidateScanSession(int ExpectedVersion);
public sealed record ScanSessionChanged(Guid SessionId, int Version, string State, Guid OperatorPersonId) : IDomainEvent
{
    public static string EventType => nameof(ScanSessionChanged);
    public static int SchemaVersion => 1;
}
public sealed record ScannerAdapter(string Id, string DisplayName, ScanInputMode InputMode,
    IReadOnlyList<string> Symbologies, bool Enabled, string Availability);
public sealed record ScannerConfiguration(bool Configured, IReadOnlyList<ScannerAdapter> Adapters,
    IReadOnlyList<ScanInputMode> SupportedInputModes);
public sealed record ScanTransactionBinding(ScanSessionView Session, QmsAdoptedPolicy Policy,
    Guid OperatorPersonId, string OperationFingerprint);

public interface IScanSessionStore
{
    Task LockChannelAsync(string channel, CancellationToken ct);
    Task<Guid?> CurrentAsync(string channel, CancellationToken ct);
    Task SetCurrentAsync(string channel, Guid session, CancellationToken ct);
    Task<ScanSessionView?> FindAsync(Guid id, CancellationToken ct);
    Task SaveAsync(ScanSessionView session, int? expectedVersion, CancellationToken ct);
    Task AppendAcceptedAsync(Guid session, ScanAccepted accepted, CancellationToken ct);
    Task<IReadOnlyList<ScanAccepted>> AcceptedAsync(Guid session, CancellationToken ct);
    Task<ScanAcceptedRecord?> FindAcceptedAsync(Guid acceptanceId, CancellationToken ct);
    Task AppendAttemptAsync(ScanAttempt attempt, ScanAttemptRetentionPolicy retention, CancellationToken ct);
    Task<IReadOnlyList<ScanAttempt>> AttemptsAsync(Guid session, CancellationToken ct);
}
