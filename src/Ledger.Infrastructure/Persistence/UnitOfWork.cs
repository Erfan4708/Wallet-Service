using Ledger.Application.Abstractions;
using Ledger.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>.
/// </summary>
/// <remarks>
/// <para>
/// The repository and this class are handed the same scoped
/// <see cref="LedgerDbContext"/>, so everything a use case changes accumulates
/// in one change tracker and commits together. EF Core wraps a single
/// <c>SaveChanges</c> in a database transaction of its own, which is why no
/// explicit <c>BeginTransaction</c> appears here yet: a transfer that writes two
/// accounts and its ledger entries in one save is already atomic. An explicit
/// transaction becomes necessary only when one use case needs several saves, or
/// mixes EF Core with raw SQL.
/// </para>
/// </remarks>
internal sealed class UnitOfWork : IUnitOfWork
{
    private const string UniqueViolation = "23505";

    private readonly LedgerDbContext _context;

    public UnitOfWork(LedgerDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <exception cref="ConflictException">
    /// A uniqueness constraint rejected the write.
    /// </exception>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Translated here so the failure crosses the boundary as something
            // the application layer already understands, and reaches the client
            // as 409 rather than 500. A use case that checks for an existing row
            // before inserting still races with a concurrent request; the unique
            // index is what actually decides, and this turns its verdict into
            // the same answer the check would have given.
            //
            // Note that this is translation, not retry or locking — those belong
            // to the concurrency phase.
            throw new ConflictException(
                "The change conflicts with a record that already exists.", exception);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation };
}
