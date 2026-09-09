using Ledger.Application.Abstractions;
using Ledger.Application.Exceptions;
using Ledger.Domain.Entities;
using Ledger.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>.
/// </summary>
/// <remarks>
/// <para>
/// The repositories and this class are handed the same scoped
/// <see cref="LedgerDbContext"/>, so everything a use case changes accumulates in
/// one change tracker and commits together.
/// </para>
/// <para>
/// The ledger needs an <em>explicit</em> transaction, which Phase 3 did not. Row
/// locks live only as long as the transaction that took them, and the ledger
/// takes its locks before it reads a balance. Relying on the implicit transaction
/// that <c>SaveChanges</c> opens would acquire the locks in one transaction and
/// write in another, by which time the lock had been released and the balance it
/// protected could have changed.
/// </para>
/// </remarks>
internal sealed class UnitOfWork : IUnitOfWork
{
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";
    private const string RaisedException = "P0001";

    private readonly LedgerDbContext _context;

    public UnitOfWork(LedgerDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Re-entrant: a use case composed of others must not start a second
        // transaction, which would defeat the atomicity the outer one provides.
        if (_context.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var result = await operation(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    /// <exception cref="ConflictException">A database constraint rejected the write.</exception>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (Translate(exception) is { } translated)
        {
            throw translated;
        }

        DrainDomainEvents();
    }

    /// <summary>
    /// Collects the events raised by everything this unit of work saved.
    /// </summary>
    /// <remarks>
    /// This is the seam the outbox will occupy. When it arrives, the drained
    /// events are serialised into an <c>outbox_messages</c> insert issued
    /// <em>inside this same transaction</em>, which is what makes "the money
    /// moved" and "the world was told" atomic — no dual write, no lost event, no
    /// event for a transaction that rolled back. Until then they are cleared, so
    /// nothing accumulates on a long-lived aggregate.
    /// </remarks>
    private void DrainDomainEvents()
    {
        var aggregates = _context.ChangeTracker
            .Entries<LedgerTransaction>()
            .Select(entry => entry.Entity)
            .Where(transaction => transaction.DomainEvents.Count > 0)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            _ = aggregate.DomainEvents.ToList<IDomainEvent>();
            aggregate.ClearDomainEvents();
        }
    }

    /// <remarks>
    /// Translated here so a database refusal crosses the boundary as something
    /// the application layer already understands, and reaches the client as a 409
    /// or 422 rather than a 500. This is translation, not retry or locking —
    /// those belong elsewhere.
    /// </remarks>
    private static Exception? Translate(DbUpdateException exception) =>
        exception.InnerException switch
        {
            PostgresException { SqlState: UniqueViolation } inner =>
                new ConflictException(DescribeUniqueViolation(inner), exception),

            // A check constraint or a trigger fired. These are the ledger's last
            // line of defence — an unbalanced transaction, a negative wallet
            // balance, an attempt to modify an entry — and reaching one means the
            // domain's own guard was bypassed.
            PostgresException { SqlState: CheckViolation or RaisedException } inner =>
                new ConflictException($"The database rejected the change: {inner.MessageText}", exception),

            _ => null,
        };

    private static string DescribeUniqueViolation(PostgresException exception) =>
        exception.ConstraintName switch
        {
            "ux_ledger_transactions_idempotency_key" =>
                "A transaction with this idempotency key already exists.",
            "ux_ledger_transactions_reverses" =>
                "That transaction has already been reversed.",
            _ => "The change conflicts with a record that already exists.",
        };
}
