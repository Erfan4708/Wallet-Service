using System.Text;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ledger.Infrastructure.Messaging;

/// <summary>
/// Drains the outbox: claims pending messages, publishes them, and records the
/// outcome.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivery is at-least-once, and cannot be more than that.</b> The sequence
/// is: claim and commit, publish and wait for the broker's confirmation, then
/// mark published. A crash in the window between the broker confirming and the
/// mark committing leaves the row pending, and the message is published again
/// once its lease expires. Closing that window would require the broker and the
/// database to commit together, which they cannot.
/// </para>
/// <para>
/// So consumers must be idempotent. Every message carries a stable
/// <c>MessageId</c> derived from the ledger transaction, which is what makes
/// recognising a repeat possible. Nothing here claims exactly-once delivery, and
/// no consumer should be written as though it had it.
/// </para>
/// <para>
/// A broker outage is not a financial failure. Transactions continue to commit
/// with their outbox rows; the backlog grows and drains when the broker returns.
/// </para>
/// </remarks>
internal sealed class OutboxPublisher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagePublisher _publisher;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        IServiceScopeFactory scopeFactory,
        IMessagePublisher publisher,
        IOptions<OutboxOptions> options,
        ILogger<OutboxPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromSeconds(_options.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            int published;

            try
            {
                published = await DrainOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Most often the database is unreachable. The loop must survive
                // it: the messages are durable and there is nothing to do but
                // wait and try again.
                _logger.LogError(exception, "The outbox publisher could not drain the outbox.");
                published = 0;
            }

            // Keep going while there is a full batch to move; a backlog should
            // drain as fast as the broker allows rather than one batch per poll.
            if (published < _options.BatchSize)
            {
                await Task.Delay(idleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Claims one batch and publishes it. Returns how many were confirmed.
    /// </summary>
    internal async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var store = new OutboxStore(context);

        var claimed = await store.ClaimAsync(
            _options.BatchSize,
            TimeSpan.FromSeconds(_options.ClaimLeaseSeconds),
            cancellationToken);

        var published = 0;

        foreach (var message in claimed)
        {
            try
            {
                await _publisher.PublishAsync(
                    message.MessageType,
                    message.MessageId,
                    Encoding.UTF8.GetBytes(message.Payload),
                    cancellationToken);

                // Only now, and only because the broker confirmed it.
                await store.MarkPublishedAsync(message.Id, cancellationToken);
                published++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down. The claim lease expires and another worker, or
                // this one after a restart, picks the message up.
                throw;
            }
            catch (Exception exception)
            {
                await store.RecordFailureAsync(
                    message.Id,
                    message.Attempts,
                    exception.Message,
                    TimeSpan.FromSeconds(_options.MaximumBackoffSeconds),
                    CancellationToken.None);

                _logger.LogWarning(
                    "Publishing outbox message {MessageId} failed on attempt {Attempts}; it remains pending.",
                    message.MessageId,
                    message.Attempts);

                // Stop the batch. If the broker is down the rest will fail too,
                // and burning through their attempt counts and backoff for
                // nothing only makes the recovery slower.
                break;
            }
        }

        return published;
    }
}
