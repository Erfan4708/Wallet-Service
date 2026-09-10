namespace Ledger.Application.Messaging;

/// <summary>
/// What other systems are told when the ledger records a transaction.
/// </summary>
/// <remarks>
/// <para>
/// A separate type from the domain event on purpose. The domain event changes
/// when the domain changes; this is a published contract that other services
/// have already deployed against, and the two must be free to move
/// independently. Mapping between them is a few lines in the infrastructure
/// layer and is the cheapest place this decoupling could possibly be bought.
/// </para>
/// <para>
/// <b>It carries no money.</b> Identifiers, a kind and a currency are enough for
/// a consumer to decide whether it cares and then ask the ledger for the detail
/// over an authenticated channel. Amounts and balances on a broker would be
/// readable by every service with a queue binding, and would sit in message
/// backlogs and dead-letter queues indefinitely.
/// </para>
/// <para>
/// The type name is versioned. A consumer matches on it, so changing the shape
/// means publishing <c>...v2</c> alongside rather than redefining what v1 means.
/// </para>
/// </remarks>
public sealed record LedgerTransactionRecordedMessage(
    Guid MessageId,
    Guid TransactionId,
    string Kind,
    string Currency,
    DateTimeOffset OccurredAt)
{
    /// <summary>The routing key and stored message type.</summary>
    public const string MessageType = "ledger.transaction.recorded.v1";
}
