namespace Ledger.Domain.Events;

/// <summary>
/// Something the domain decided, recorded as a fact for other parts of the
/// system to react to.
/// </summary>
/// <remarks>
/// Deliberately an empty marker on a plain record. The domain describes
/// <em>what happened</em>; it holds no opinion about who is told or how. When
/// the outbox arrives, infrastructure will drain these at save time and write
/// them as messages in the same database transaction as the ledger entries —
/// which is what makes "the money moved" and "the world was told" atomic. None
/// of that requires a single line of this project's domain to change.
/// </remarks>
public interface IDomainEvent
{
    /// <summary>When the event happened, in business time.</summary>
    DateTimeOffset OccurredAt { get; }
}
