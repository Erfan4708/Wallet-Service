using Ledger.Domain.Enums;

namespace Ledger.Domain.Events;

/// <summary>
/// A balanced set of ledger entries was committed to the ledger.
/// </summary>
public sealed record LedgerTransactionRecorded(
    Guid TransactionId,
    LedgerTransactionKind Kind,
    Currency Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;
