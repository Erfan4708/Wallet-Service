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
    /// Returns the system account with the given key, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// System accounts are addressed by meaning rather than by a hard-coded
    /// identifier, so no magic GUID appears in application code.
    /// </remarks>
    Task<Account?> FindBySystemKeyAsync(string systemKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the given accounts and takes an exclusive lock on each, blocking any
    /// other transaction that wants to change them until this one ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the concurrency control for the whole ledger. A balance decision —
    /// "are there sufficient funds?" — is only sound if nothing can change the
    /// balance between reading it and writing the result. Locking first and
    /// reading afterwards is what closes that window; the reverse order does not.
    /// </para>
    /// <para>
    /// Implementations <b>must</b> acquire the locks in a deterministic order,
    /// sorted by identifier. Two concurrent transfers, one A to B and one B to A,
    /// that lock in argument order will deadlock; sorting makes that impossible
    /// rather than unlikely.
    /// </para>
    /// <para>
    /// Accounts that do not exist are simply absent from the result, so callers
    /// must check what came back rather than assume.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Account>> GetForUpdateAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new account to be persisted when the unit of work is saved.
    /// </summary>
    Task AddAsync(Account account, CancellationToken cancellationToken = default);
}
