using System.Text;
using System.Text.Json;
using Ledger.Application.Ledger;
using Ledger.Application.Messaging;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;
using Ledger.Infrastructure.Messaging;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// The transactional outbox, against a real PostgreSQL database.
/// </summary>
/// <remarks>
/// The broker is faked here on purpose. What needs proving is the behaviour a
/// real broker makes hardest to arrange: that a message survives a publish
/// failure, that attempts are counted and backed off, that a claim stops a second
/// worker taking the same row, and that a crash after publishing results in a
/// duplicate rather than a loss. A separate test publishes through real RabbitMQ.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class OutboxTests : IAsyncLifetime
{
    private static readonly Guid WalletId = new("aabbccdd-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;

    public OutboxTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private LedgerHost NewHost() => new(_postgres.CreateContext());

    // ------------------------------------------------------------- atomicity

    // The reason the pattern exists. If the message were published after the
    // commit, a crash in between would lose it; if it were published before, a
    // rollback would announce a transaction that never happened.
    [RequiresDockerFact]
    public async Task A_committed_transaction_leaves_exactly_one_outbox_message()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
        }

        await using var context = _postgres.CreateContext();
        var message = await context.OutboxMessages.SingleAsync();

        Assert.Equal(LedgerTransactionRecordedMessage.MessageType, message.MessageType);
        Assert.Null(message.PublishedAt);
        Assert.Equal(0, message.Attempts);
    }

    [RequiresDockerFact]
    public async Task A_refused_transaction_leaves_no_outbox_message()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 10m);
        }

        await ClearOutboxAsync();

        await using (var host = NewHost())
        {
            await Assert.ThrowsAsync<InsufficientFundsException>(() =>
                host.Withdraw.HandleAsync(new WithdrawCommand(Guid.NewGuid(), WalletId, 500m, Currency.USD)));
        }

        await using var context = _postgres.CreateContext();

        Assert.Empty(await context.OutboxMessages.ToListAsync());
    }

    // The message carries identifiers, not money. A broker fans messages out to
    // every service with a binding, and they sit in backlogs and dead-letter
    // queues indefinitely.
    [RequiresDockerFact]
    public async Task An_outbox_payload_carries_no_amount_or_balance()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 123.45m);
        }

        await using var context = _postgres.CreateContext();
        var message = await context.OutboxMessages.SingleAsync();

        Assert.DoesNotContain("123.45", message.Payload, StringComparison.Ordinal);

        var payload = JsonSerializer.Deserialize<LedgerTransactionRecordedMessage>(message.Payload)!;
        Assert.Equal(message.AggregateId, payload.TransactionId);
        Assert.Equal("Deposit", payload.Kind);
        Assert.Equal("USD", payload.Currency);
    }

    // --------------------------------------------------------------- claiming

    [RequiresDockerFact]
    public async Task Claiming_leases_a_message_so_a_second_worker_skips_it()
    {
        await SeedPendingAsync();

        await using var first = _postgres.CreateContext();
        await using var second = _postgres.CreateContext();

        var claimedByFirst = await new OutboxStore(first)
            .ClaimAsync(10, TimeSpan.FromMinutes(5), CancellationToken.None);
        var claimedBySecond = await new OutboxStore(second)
            .ClaimAsync(10, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Single(claimedByFirst);

        // Not because it is locked -- the first claim committed -- but because
        // the lease pushed next_attempt_at into the future.
        Assert.Empty(claimedBySecond);
    }

    [RequiresDockerFact]
    public async Task Claiming_counts_the_attempt()
    {
        await SeedPendingAsync();

        await using var context = _postgres.CreateContext();
        var claimed = Assert.Single(
            await new OutboxStore(context).ClaimAsync(10, TimeSpan.FromMinutes(5), CancellationToken.None));

        Assert.Equal(1, claimed.Attempts);
    }

    // A worker that dies mid-publish must not strand the message forever.
    [RequiresDockerFact]
    public async Task An_expired_lease_makes_a_message_claimable_again()
    {
        await SeedPendingAsync();

        await using var context = _postgres.CreateContext();
        var store = new OutboxStore(context);

        await store.ClaimAsync(10, TimeSpan.FromSeconds(-1), CancellationToken.None);
        var reclaimed = await store.ClaimAsync(10, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Single(reclaimed);
    }

    // -------------------------------------------------------------- draining

    [RequiresDockerFact]
    public async Task A_confirmed_message_is_marked_published()
    {
        await SeedPendingAsync();
        var broker = new RecordingPublisher();

        var moved = await DrainAsync(broker);

        Assert.Equal(1, moved);
        Assert.Single(broker.Published);
        Assert.Equal(0, await PendingCountAsync());
    }

    [RequiresDockerFact]
    public async Task A_broker_failure_leaves_the_message_pending_with_its_error_recorded()
    {
        await SeedPendingAsync();
        var broker = new FailingPublisher("the broker is unreachable");

        var moved = await DrainAsync(broker);

        Assert.Equal(0, moved);
        Assert.Equal(1, await PendingCountAsync());

        await using var context = _postgres.CreateContext();
        var message = await context.OutboxMessages.SingleAsync();

        Assert.Null(message.PublishedAt);
        Assert.Equal(1, message.Attempts);
        Assert.Contains("unreachable", message.LastError!, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task A_message_that_failed_is_retried_once_its_backoff_expires()
    {
        await SeedPendingAsync();

        Assert.Equal(0, await DrainAsync(new FailingPublisher("down")));

        // Backoff moved it into the future, so an immediate retry finds nothing.
        Assert.Equal(0, await DrainAsync(new RecordingPublisher()));

        await MakeClaimableAsync();

        var broker = new RecordingPublisher();
        Assert.Equal(1, await DrainAsync(broker));
        Assert.Single(broker.Published);
    }

    // The restart case: a process that died with pending messages loses nothing,
    // because the messages were never in the process to begin with.
    [RequiresDockerFact]
    public async Task Messages_pending_from_a_previous_process_are_published_after_a_restart()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 50m);
        }

        Assert.Equal(1, await PendingCountAsync());

        // A brand new publisher, as a restarted process would have.
        var broker = new RecordingPublisher();
        Assert.Equal(1, await DrainAsync(broker));

        Assert.Single(broker.Published);
        Assert.Equal(0, await PendingCountAsync());
    }

    // ------------------------------------------------------- delivery semantics

    // Delivery is at-least-once and cannot be more. A crash between the broker
    // confirming and the row being marked leaves the message pending, and it is
    // published again. This test reproduces exactly that window, because it is a
    // property consumers must be written against rather than a bug to be fixed.
    [RequiresDockerFact]
    public async Task A_crash_after_publishing_but_before_marking_causes_a_duplicate()
    {
        await SeedPendingAsync();
        var broker = new RecordingPublisher();

        await using (var context = _postgres.CreateContext())
        {
            var store = new OutboxStore(context);
            var claimed = Assert.Single(
                await store.ClaimAsync(10, TimeSpan.FromSeconds(-1), CancellationToken.None));

            await broker.PublishAsync(
                claimed.MessageType, claimed.MessageId,
                Encoding.UTF8.GetBytes(claimed.Payload), CancellationToken.None);

            // The process dies here: the broker has the message, the database
            // does not know it.
        }

        Assert.Equal(1, await DrainAsync(broker));

        Assert.Equal(2, broker.Published.Count);

        // Which is why every message carries a stable identifier: a consumer can
        // recognise the second copy as one it has already handled.
        Assert.Single(broker.Published.Distinct());
    }

    // ------------------------------------------------------------------ helpers

    private async Task<int> DrainAsync(IMessagePublisher broker)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LedgerDbContext>(options => options.UseNpgsql(_postgres.ConnectionString));

        await using var provider = services.BuildServiceProvider();

        using var telemetry = new OutboxTelemetry($"ledger.outbox.test.{Guid.NewGuid():N}");

        var publisher = new OutboxPublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            broker,
            Options.Create(new OutboxOptions { BatchSize = 10, ClaimLeaseSeconds = 60 }),
            telemetry,
            NullLogger<OutboxPublisher>.Instance);

        return await publisher.DrainOnceAsync(CancellationToken.None);
    }

    private async Task SeedPendingAsync()
    {
        await using var host = NewHost();
        await host.OpenWalletAsync(WalletId, Currency.USD, funding: 25m);
    }

    private async Task<int> PendingCountAsync()
    {
        await using var context = _postgres.CreateContext();

        return await new OutboxStore(context).CountPendingAsync(CancellationToken.None);
    }

    private async Task ClearOutboxAsync() => await ExecuteAsync("TRUNCATE TABLE outbox_messages");

    private async Task MakeClaimableAsync() =>
        await ExecuteAsync("UPDATE outbox_messages SET next_attempt_at = now() - interval '1 hour'");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RecordingPublisher : IMessagePublisher
    {
        internal List<Guid> Published { get; } = [];

        public Task PublishAsync(
            string messageType, Guid messageId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            Published.Add(messageId);

            return Task.CompletedTask;
        }
    }

    private sealed class FailingPublisher : IMessagePublisher
    {
        private readonly string _reason;

        internal FailingPublisher(string reason) => _reason = reason;

        public Task PublishAsync(
            string messageType, Guid messageId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(_reason));
    }
}
