using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Ledger.Infrastructure.Persistence.Outbox;

/// <summary>One message a worker has taken responsibility for publishing.</summary>
internal sealed record ClaimedMessage(
    long Id,
    Guid MessageId,
    string MessageType,
    Guid AggregateId,
    string Payload,
    int Attempts);

/// <summary>
/// Claims pending messages and records what happened to them.
/// </summary>
/// <remarks>
/// <para>
/// The claim is a single statement that commits immediately. That is deliberate:
/// the alternative — holding <c>SELECT … FOR UPDATE</c> open while publishing —
/// would keep a database transaction alive for the duration of a network round
/// trip to the broker, and a broker that stops responding would then hold
/// database locks until it timed out. A short claim followed by an unlocked
/// publish keeps the two failure domains apart.
/// </para>
/// <para>
/// <c>SKIP LOCKED</c> inside the claim lets several workers drain the same table
/// without contending, and pushing <c>next_attempt_at</c> into the future acts as
/// a lease: no other worker takes the message while this one is publishing, and
/// if this one dies the lease simply expires and the message becomes claimable
/// again.
/// </para>
/// </remarks>
internal sealed class OutboxStore
{
    private readonly LedgerDbContext _context;

    internal OutboxStore(LedgerDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <summary>
    /// Takes up to <paramref name="batchSize"/> pending messages, leasing each for
    /// <paramref name="lease"/>.
    /// </summary>
    internal async Task<IReadOnlyList<ClaimedMessage>> ClaimAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE outbox_messages
               SET attempts = attempts + 1,
                   next_attempt_at = now() + @lease
             WHERE id IN (
                   SELECT id
                     FROM outbox_messages
                    WHERE published_at IS NULL
                      AND next_attempt_at <= now()
                    ORDER BY id
                    LIMIT @batch
                      FOR UPDATE SKIP LOCKED
             )
            RETURNING id, message_id, message_type, aggregate_id, payload::text, attempts;
            """;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;

        if (opened)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.Interval) { Value = lease });
            command.Parameters.AddWithValue("batch", batchSize);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var claimed = new List<ClaimedMessage>(batchSize);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new ClaimedMessage(
                    reader.GetInt64(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    reader.GetInt32(5)));
            }

            return claimed;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Records that the broker confirmed the message.
    /// </summary>
    /// <remarks>
    /// Happens strictly after confirmation. A crash between the broker accepting
    /// the message and this statement committing leaves the row pending, and the
    /// message is published a second time once the lease expires — which is
    /// precisely why delivery is at-least-once and consumers must tolerate
    /// duplicates.
    /// </remarks>
    internal async Task MarkPublishedAsync(long id, CancellationToken cancellationToken) =>
        await _context.Database.ExecuteSqlAsync(
            $"UPDATE outbox_messages SET published_at = now(), last_error = NULL WHERE id = {id}",
            cancellationToken);

    /// <summary>
    /// Records a failed attempt and schedules the next one.
    /// </summary>
    /// <remarks>
    /// Exponential backoff, capped. Without a cap a message that fails often
    /// enough is effectively abandoned; without backoff a broker outage becomes a
    /// tight retry loop against a system that is already unwell.
    /// </remarks>
    internal async Task RecordFailureAsync(
        long id,
        int attempts,
        string error,
        TimeSpan maximumBackoff,
        CancellationToken cancellationToken)
    {
        var seconds = Math.Min(Math.Pow(2, Math.Min(attempts, 10)), maximumBackoff.TotalSeconds);
        var truncated = error.Length > 1000 ? error[..1000] : error;

        await _context.Database.ExecuteSqlAsync(
            $"""
             UPDATE outbox_messages
                SET next_attempt_at = now() + make_interval(secs => {seconds}),
                    last_error = {truncated}
              WHERE id = {id}
             """,
            cancellationToken);
    }

    /// <summary>How many messages are waiting. Exposed for metrics and tests.</summary>
    internal async Task<int> CountPendingAsync(CancellationToken cancellationToken) =>
        await _context.OutboxMessages.CountAsync(
            message => message.PublishedAt == null, cancellationToken);
}
