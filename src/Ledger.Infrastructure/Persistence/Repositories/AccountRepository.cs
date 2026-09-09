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
}
