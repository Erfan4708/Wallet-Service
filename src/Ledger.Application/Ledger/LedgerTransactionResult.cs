using Ledger.Domain.Entities;
using Ledger.Domain.Enums;

namespace Ledger.Application.Ledger;

/// <summary>What a caller is told about a recorded ledger transaction.</summary>
public sealed record LedgerTransactionResult(
    Guid TransactionId,
    LedgerTransactionKind Kind,
    Currency Currency,
    DateTimeOffset OccurredAt,
    IReadOnlyList<LedgerEntryResult> Entries,
    bool WasReplayed)
{
    internal static LedgerTransactionResult From(LedgerTransaction transaction, bool wasReplayed) =>
        new(transaction.Id,
            transaction.Kind,
            transaction.Currency,
            transaction.OccurredAt,
            transaction.Entries
                .OrderBy(entry => entry.EntryIndex)
                .Select(entry => new LedgerEntryResult(entry.AccountId, entry.Amount.Amount, entry.Amount.Currency))
                .ToList(),
            wasReplayed);
}

/// <summary>One signed movement, as reported to a caller.</summary>
public sealed record LedgerEntryResult(Guid AccountId, decimal Amount, Currency Currency);
