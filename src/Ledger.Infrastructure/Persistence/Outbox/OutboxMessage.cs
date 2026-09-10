namespace Ledger.Infrastructure.Persistence.Outbox;

/// <summary>
/// A message waiting to be published, written in the same transaction as the
/// financial change that produced it.
/// </summary>
/// <remarks>
/// <para>
/// Infrastructure, not domain. The domain raises an event because something
/// happened; that this fact has to survive a broker outage, be retried, and be
/// claimed by exactly one worker at a time is a delivery concern the domain has
/// no opinion about.
/// </para>
/// <para>
/// The row exists to close the dual-write window. Committing the ledger and then
/// publishing means a crash in between loses the message; publishing first means
/// a crash announces something that never happened. Writing the message into the
/// same transaction as the money makes both outcomes impossible.
/// </para>
/// </remarks>
internal sealed class OutboxMessage
{
    /// <summary>Insertion order, and the order messages are published in.</summary>
    public long Id { get; private set; }

    /// <summary>
    /// A stable identifier that survives republication.
    /// </summary>
    /// <remarks>
    /// Delivery is at-least-once, so a consumer will eventually see the same
    /// message twice. This is what lets it recognise the second copy.
    /// </remarks>
    public Guid MessageId { get; private set; }

    public string MessageType { get; private set; } = string.Empty;

    /// <summary>The ledger transaction this message is about.</summary>
    public Guid AggregateId { get; private set; }

    public string Payload { get; private set; } = string.Empty;

    /// <summary>Business time, copied from the event.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the broker confirmed it. Null while the message is pending.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>How many times publication has been attempted.</summary>
    public int Attempts { get; private set; }

    /// <summary>
    /// When this message may next be claimed.
    /// </summary>
    /// <remarks>
    /// Doubles as the claim lease and the retry backoff. A worker that claims a
    /// message pushes this into the future, so no other worker takes it while the
    /// first is publishing; if that worker dies, the lease simply expires and the
    /// message becomes claimable again.
    /// </remarks>
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>Why the last attempt failed, truncated. Operational aid only.</summary>
    public string? LastError { get; private set; }

    private OutboxMessage()
    {
    }

    internal static OutboxMessage Create(
        Guid messageId,
        string messageType,
        Guid aggregateId,
        string payload,
        DateTimeOffset occurredAt,
        DateTimeOffset now) =>
        new()
        {
            MessageId = messageId,
            MessageType = messageType,
            AggregateId = aggregateId,
            Payload = payload,
            OccurredAt = occurredAt,
            CreatedAt = now,
            NextAttemptAt = now,
        };
}
