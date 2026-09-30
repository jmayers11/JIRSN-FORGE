using Forge.Kernel.Events;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SubscriptionTests(PostgresFixture postgres)
{
    private sealed class RecordingSubscriber(int failAtCall = -1) : IEventSubscriber
    {
        private int calls;

        public List<(long Sequence, string Type)> Seen { get; } = [];

        public string Name => "test-recorder";

        public bool Handles(string eventType) => true;

        public Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
        {
            if (calls++ == failAtCall)
            {
                throw new InvalidOperationException("simulated failure");
            }

            Seen.Add((sequence, envelope.EventType));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Subscribers_receive_events_in_order_and_resume_after_failure()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using (var app = new ForgeApp(cs))
        {
            var admin = app.As(ForgeApp.AdminSubject);
            await admin.CommandAsync("/api/kernel/people", new { idpSubject = "sarah", displayName = "Sarah", reason = "r" }, "a");
            var relay = app.Services.GetRequiredService<OutboxRelay>();
            while (await relay.RelayBatchAsync(10, CancellationToken.None) > 0)
            {
            }
        }

        await using var dataSource = NpgsqlDataSource.Create(cs);

        // Fails on the second event: the first is delivered and checkpointed, the second is retried.
        var failing = new RecordingSubscriber(failAtCall: 1);
        await new SubscriptionPump(dataSource, [failing], NullLogger<SubscriptionPump>.Instance).PumpOnceAsync(100, CancellationToken.None);
        Assert.Single(failing.Seen);

        var recovered = new RecordingSubscriber();
        await new SubscriptionPump(dataSource, [recovered], NullLogger<SubscriptionPump>.Instance).PumpOnceAsync(100, CancellationToken.None);

        var all = failing.Seen.Concat(recovered.Seen).ToList();
        Assert.Equal(3, all.Count); // bootstrap person, bootstrap role, sarah registered
        Assert.Equal(all.OrderBy(e => e.Sequence).ToList(), all);
        Assert.Equal(["PersonRegistered", "RoleAssigned", "PersonRegistered"], all.Select(e => e.Type).ToArray());
    }
}
