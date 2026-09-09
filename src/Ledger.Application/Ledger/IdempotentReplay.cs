using Ledger.Application.Abstractions;
using Ledger.Application.Exceptions;
using Ledger.Domain.Enums;
using Ledger.Domain.ValueObjects;

namespace Ledger.Application.Ledger;

/// <summary>
/// Finds the transaction a retried request already produced.
/// </summary>
/// <remarks>
/// <para>
/// Must be called <b>after</b> the accounts have been locked. Two requests
/// carrying the same key necessarily touch the same accounts, so the locks
/// serialise them, and the second one's read then sees the first one's committed
/// transaction. Checking before locking would leave a window in which both
/// requests find nothing and both proceed.
/// </para>
/// <para>
/// The unique index on the key remains the ultimate authority: if this check is
/// ever bypassed, the insert fails rather than producing a duplicate.
/// </para>
/// </remarks>
internal static class IdempotentReplay
{
    internal static async Task<LedgerTransactionResult?> FindAsync(
        ILedgerTransactionRepository transactions,
        string? idempotencyKey,
        LedgerTransactionKind expectedKind,
        Money expectedAmount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        var existing = await transactions.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        // A key reused for a different movement must not quietly receive the
        // original's result: that would report success for an operation that
        // never happened.
        if (!existing.Matches(expectedKind, expectedAmount))
        {
            throw new ConflictException(
                $"Idempotency key '{idempotencyKey}' was already used for a different operation.");
        }

        return LedgerTransactionResult.From(existing, wasReplayed: true);
    }
}
