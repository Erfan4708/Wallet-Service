namespace Ledger.Application.Abstractions;

/// <summary>
/// The transaction boundary for a single use case.
/// </summary>
/// <remarks>
/// <para>
/// This is separate from the repositories on purpose. A repository is concerned
/// with one kind of aggregate; the unit of work is concerned with
/// <em>atomicity across all of them</em>. A transfer debits one account, credits
/// another and appends ledger entries, and those changes must commit together or
/// not at all — a decision that belongs to one object spanning them, not to any
/// individual repository.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="operation"/> inside one database transaction,
    /// committing if it returns and rolling back if it throws.
    /// </summary>
    /// <remarks>
    /// Needed because the ledger takes row locks <em>before</em> it reads, and a
    /// lock only lasts as long as the transaction holding it. Saving alone would
    /// open its own transaction too late, after the locks had already been
    /// released.
    /// </remarks>
    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>Commits every change made during the current unit of work.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
