using Ledger.Domain.Entities;

namespace Ledger.Application.Abstractions;

/// <summary>
/// Storage for <see cref="Account"/> aggregates.
/// </summary>
/// <remarks>
/// <para>
/// This interface lives in the application layer and is implemented by the
/// infrastructure layer. That is the dependency inversion: the inner layer
/// states what it needs, the outer layer supplies it, and the arrow between
/// them points inwards. Nothing here mentions EF Core, PostgreSQL, connection
/// strings or SQL, so the use cases can be written and tested before a database
/// exists and remain unchanged when one arrives.
/// </para>
/// <para>
/// It is deliberately specific rather than a generic <c>IRepository&lt;T&gt;</c>.
/// A generic repository can only offer operations that make sense for every
/// entity, which means it converges on exposing <c>IQueryable</c> and pushes
/// query construction back into the caller — reintroducing the persistence
/// coupling the abstraction was meant to remove.
/// </para>
/// </remarks>
public interface IAccountRepository
{
    /// <summary>
    /// Returns the account with the given identifier, or <see langword="null"/>
    /// if no such account exists.
    /// </summary>
    Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new account to be persisted when the unit of work is saved.
    /// </summary>
    /// <remarks>
    /// Adding does not write to the database. Committing is the unit of work's
    /// job, which is what allows several changes to share one transaction.
    /// </remarks>
    Task AddAsync(Account account, CancellationToken cancellationToken = default);
}
