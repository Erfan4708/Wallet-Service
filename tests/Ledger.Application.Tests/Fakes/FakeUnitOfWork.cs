using Ledger.Application.Abstractions;

namespace Ledger.Application.Tests.Fakes;

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public FakeUnitOfWork(List<string>? callLog = null) => CallLog = callLog ?? [];

    public List<string> CallLog { get; }

    public int SaveCount { get; private set; }

    public int TransactionCount { get; private set; }

    /// <summary>Set to make the commit fail, to test that nothing is left applied.</summary>
    public Exception? FailOnSave { get; set; }

    /// <remarks>
    /// There is no real transaction to open here, so this just runs the
    /// operation. What it does record is that the use case asked for one — an
    /// operation that writes without asking is a bug this fake can catch.
    /// </remarks>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        CallLog.Add(nameof(ExecuteInTransactionAsync));
        TransactionCount++;

        return await operation(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        CallLog.Add(nameof(SaveChangesAsync));

        if (FailOnSave is not null)
        {
            return Task.FromException(FailOnSave);
        }

        SaveCount++;

        return Task.CompletedTask;
    }
}

/// <summary>In-memory stand-in for <see cref="ILedgerTransactionRepository"/>.</summary>
internal sealed class FakeLedgerTransactionRepository : ILedgerTransactionRepository
{
    private readonly List<Domain.Entities.LedgerTransaction> _transactions = [];

    public IReadOnlyList<Domain.Entities.LedgerTransaction> Transactions => _transactions;

    public Task AddAsync(Domain.Entities.LedgerTransaction transaction, CancellationToken cancellationToken = default)
    {
        _transactions.Add(transaction);

        return Task.CompletedTask;
    }

    public Task<Domain.Entities.LedgerTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_transactions.FirstOrDefault(transaction => transaction.Id == id));

    public Task<Domain.Entities.LedgerTransaction?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_transactions.FirstOrDefault(transaction => transaction.IdempotencyKey == idempotencyKey));

    public Task<bool> HasReversalAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_transactions.Any(transaction => transaction.ReversesTransactionId == transactionId));

    public Task<IReadOnlyList<Domain.Entities.LedgerEntry>> GetEntriesForAccountAsync(
        Guid accountId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Domain.Entities.LedgerEntry> entries = _transactions
            .SelectMany(transaction => transaction.Entries)
            .Where(entry => entry.AccountId == accountId)
            .Take(limit)
            .ToList();

        return Task.FromResult(entries);
    }
}
