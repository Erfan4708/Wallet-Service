using Ledger.Domain.Entities;

namespace Ledger.Application.Abstractions;

/// <summary>
/// Storage for the ledger itself.
/// </summary>
public interface ILedgerTransactionRepository
{
    /// <summary>
    /// Registers a transaction and its entries to be persisted when the unit of
    /// work is saved.
    /// </summary>
    Task AddAsync(LedgerTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a transaction with its entries loaded, or <see langword="null"/>.
    /// </summary>
    Task<LedgerTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the transaction previously recorded under this idempotency key, if
    /// any.
    /// </summary>
    /// <remarks>
    /// Called <em>after</em> the accounts have been locked. Two requests carrying
    /// the same key necessarily touch the same accounts, so they serialise on
    /// those locks, and the second one's read then sees the first one's committed
    /// transaction. Checking before locking would be a race.
    /// </remarks>
    Task<LedgerTransaction?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Returns whether the given transaction has already been reversed.</summary>
    Task<bool> HasReversalAsync(Guid transactionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an account's entries, most recent first.
    /// </summary>
    Task<IReadOnlyList<LedgerEntry>> GetEntriesForAccountAsync(
        Guid accountId,
        int limit,
        CancellationToken cancellationToken = default);
}
