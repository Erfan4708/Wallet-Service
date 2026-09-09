using Ledger.Application.Abstractions;
using Ledger.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="ILedgerTransactionRepository"/>.</summary>
internal sealed class LedgerTransactionRepository : ILedgerTransactionRepository
{
    private readonly LedgerDbContext _context;

    public LedgerTransactionRepository(LedgerDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    public Task AddAsync(LedgerTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // Adding the transaction cascades to its entries, because they are its
        // children in the model. Nothing reaches the database until the unit of
        // work is saved.
        _context.LedgerTransactions.Add(transaction);

        return Task.CompletedTask;
    }

    public async Task<LedgerTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _context.LedgerTransactions
            .Include(transaction => transaction.Entries)
            .FirstOrDefaultAsync(transaction => transaction.Id == id, cancellationToken);

    public async Task<LedgerTransaction?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await _context.LedgerTransactions
            .Include(transaction => transaction.Entries)
            .FirstOrDefaultAsync(transaction => transaction.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<bool> HasReversalAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        await _context.LedgerTransactions
            .AnyAsync(transaction => transaction.ReversesTransactionId == transactionId, cancellationToken);

    /// <remarks>
    /// Read-only, so tracking is switched off: these entries are being reported,
    /// not modified, and entries can never be modified in any case.
    /// </remarks>
    public async Task<IReadOnlyList<LedgerEntry>> GetEntriesForAccountAsync(
        Guid accountId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await _context.LedgerEntries
            .AsNoTracking()
            .Where(entry => entry.AccountId == accountId)
            .OrderByDescending(entry => entry.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
}
