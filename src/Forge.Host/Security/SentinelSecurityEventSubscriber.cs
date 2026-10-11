using System.Net.Http.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Events;
using Forge.Kernel.Postgres;
using Forge.Workforce;

namespace Forge.Host.Security;

public sealed record SentinelSecurityNotification(
    long Sequence,
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    string SourceContext,
    string EntityType,
    string EntityId,
    string Actor,
    Guid CorrelationId,
    string Payload);

/// <summary>Durable outbox subscriber for the configured Sentinel security-event intake endpoint.</summary>
public sealed class SentinelSecurityEventSubscriber(HttpClient client, Uri endpoint) : IEventSubscriber
{
    public const string SubscriberName = "sentinel-security-events";

    public string Name => SubscriberName;

    public bool Handles(string eventType) => eventType is nameof(AuthorizationDenied)
        or nameof(BadgePinVerificationFailed)
        or nameof(IdentityDeactivated);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        var notification = new SentinelSecurityNotification(
            sequence,
            envelope.EventId,
            envelope.EventType,
            envelope.OccurredAt,
            envelope.SourceContext,
            envelope.EntityType,
            envelope.EntityId,
            envelope.Actor,
            envelope.CorrelationId,
            envelope.Payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(notification),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", envelope.EventId.ToString("D"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }
}
