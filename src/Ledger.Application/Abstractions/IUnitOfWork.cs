namespace Ledger.Application.Abstractions;

/// <summary>
/// The transaction boundary for a single use case.
/// </summary>
/// <remarks>
/// <para>
/// This is separate from <see cref="IAccountRepository"/> on purpose. A
/// repository is concerned with one kind of aggregate; the unit of work is
/// concerned with <em>atomicity across all of them</em>. Once a transfer has to
/// debit one account, credit another and append ledger entries, those changes
/// must commit together or not at all — and that decision belongs to one object
/// that spans them, not to any individual repository.
/// </para>
/// <para>
/// Keeping the concept explicit now means the transfer use case will not have to
/// invent a transaction boundary later, and it keeps the commit point visible in
/// the use case rather than hidden inside a repository call.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Commits every change made during the current unit of work.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
