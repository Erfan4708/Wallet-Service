using Ledger.Application.Abstractions;
using Ledger.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAccountRepository"/>.
/// </summary>
/// <remarks>
/// Internal on purpose: nothing outside this assembly can name the type, so the
/// only way to obtain one is through the interface the application layer owns.
/// Nothing here returns an <c>IQueryable</c> or a <c>DbContext</c>, so EF Core
/// cannot escape into the layers above.
/// </remarks>
internal sealed class AccountRepository : IAccountRepository
{
    private readonly LedgerDbContext _context;

    public AccountRepository(LedgerDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <remarks>
    /// Returns a <em>tracked</em> entity. That is deliberate: this repository
    /// serves write use cases, and a transfer will load an account, call
    /// <c>Debit</c> on it and expect the change to be saved. An
    /// <c>AsNoTracking</c> read would silently discard that mutation. When a
    /// read-only query path is worth optimising it should get its own method
    /// rather than weakening this one.
    /// </remarks>
    public async Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _context.Accounts.FirstOrDefaultAsync(account => account.Id == id, cancellationToken);

    /// <remarks>
    /// Registers the account with the change tracker; nothing reaches the
    /// database until the unit of work is saved. EF Core's own <c>AddAsync</c>
    /// exists only for value generators that need a database round trip, and
    /// identifiers here come from the caller, so the synchronous overload is
    /// both correct and cheaper.
    /// </remarks>
    public Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        _context.Accounts.Add(account);

        return Task.CompletedTask;
    }

    /// <remarks>
    /// Deliberately untracked. This is a lookup to discover which account to
    /// lock, and a tracked copy taken before the lock would be a copy of the
    /// balance as it was <em>before</em> anyone was excluded from changing it.
    /// The caller must use the instance returned by
    /// <see cref="GetForUpdateAsync"/>, not this one.
    /// </remarks>
    public async Task<Account?> FindBySystemKeyAsync(
        string systemKey,
        CancellationToken cancellationToken = default) =>
        await _context.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(account => account.SystemKey == systemKey, cancellationToken);

    /// <remarks>
    /// <para>
    /// Locks are taken one row at a time, in ascending identifier order. The
    /// ordering is the entire point: two concurrent transfers, one A to B and one
    /// B to A, that locked in argument order would each hold what the other
    /// wants, and PostgreSQL would resolve it by killing one with a deadlock
    /// error. Sorting makes that impossible rather than unlikely.
    /// </para>
    /// <para>
    /// One statement per row rather than a single <c>WHERE id = ANY(...) ORDER BY
    /// id FOR UPDATE</c>, because a plan is free to lock rows in the order it
    /// finds them rather than the order it returns them — the ordering guarantee
    /// applies to the result set, not to lock acquisition. Separate statements
    /// make the order a fact rather than an assumption, and for the two rows a
    /// transfer touches the extra round trip is not worth arguing about.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Account>> GetForUpdateAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // A row lock lives exactly as long as the transaction that took it. In
        // autocommit each statement is its own transaction, so the lock would be
        // released the moment the SELECT returned and would protect nothing --
        // the balance could change before the caller had finished deciding what
        // to do about it. Failing loudly is the only safe response, because the
        // alternative is a lost update that no test would notice.
        if (_context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Accounts can only be locked inside a transaction. Call this through " +
                "IUnitOfWork.ExecuteInTransactionAsync.");
        }

        var accounts = new List<Account>(ids.Count);

        foreach (var id in ids.Distinct().OrderBy(id => id))
        {
            // Take the lock as a statement in its own right, rather than as a
            // side effect of a projection. Composing FOR UPDATE into a query EF
            // then shapes is fragile: what the caller gets back is the shaped
            // result, and the locking is easy to lose without anything failing.
            // Locking and reading as two explicit steps makes the order --
            // exclude everyone else first, look second -- impossible to misread.
            await _context.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM accounts WHERE id = {id} FOR UPDATE", cancellationToken);

            // Identity resolution would hand back an instance this context loaded
            // before the lock existed, carrying the balance as it was then --
            // precisely the stale read the lock is meant to prevent. Only
            // untouched instances are detached, so nothing pending is discarded.
            foreach (var stale in _context.ChangeTracker.Entries<Account>()
                         .Where(entry => entry.Entity.Id == id && entry.State == EntityState.Unchanged)
                         .ToList())
            {
                stale.State = EntityState.Detached;
            }

            var account = await _context.Accounts
                .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

            if (account is not null)
            {
                accounts.Add(account);
            }
        }

        return accounts;
    }
}
