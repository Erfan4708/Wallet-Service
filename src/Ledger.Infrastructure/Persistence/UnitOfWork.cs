using Ledger.Application.Abstractions;
using Ledger.Application.Exceptions;
using Ledger.Domain.Entities;
using System.Text.Json;
using Ledger.Application.Messaging;
using Ledger.Domain.Enums;
using Ledger.Domain.Events;
using Ledger.Infrastructure.Persistence.Outbox;
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
    private readonly TimeProvider _timeProvider;

    public UnitOfWork(LedgerDbContext context, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _context = context;
        _timeProvider = timeProvider;
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
        // Written *before* SaveChanges, so the outbox rows are part of the same
        // INSERT batch and the same transaction as the ledger entries and the
        // balances they belong to. This is the whole point of the pattern: there
        // is no window in which the money has moved and the message has not been
        // recorded, or the reverse.
        DrainDomainEventsIntoOutbox();

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (Translate(exception) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Turns the events raised during this unit of work into outbox rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alternative -- commit the ledger, then publish to the broker -- is a
    /// dual write, and it has no correct failure mode. A crash after the commit
    /// loses the message and nothing downstream ever learns the money moved; a
    /// publish before the commit announces a transaction that may then roll back.
    /// Recording the intent to publish in the same transaction as the money
    /// removes the window entirely: either both are committed or neither is.
    /// </para>
    /// <para>
    /// What is left is the far easier problem of getting a durably recorded
    /// message to a broker eventually, which a background publisher can retry for
    /// as long as it takes.
    /// </para>
    /// </remarks>
    private void DrainDomainEventsIntoOutbox()
    {
        var aggregates = _context.ChangeTracker
            .Entries<LedgerTransaction>()
            .Select(entry => entry.Entity)
            .Where(transaction => transaction.DomainEvents.Count > 0)
            .ToList();

        var now = _timeProvider.GetUtcNow();

        foreach (var aggregate in aggregates)
        {
            foreach (var raised in aggregate.DomainEvents.ToList())
            {
                if (ToOutboxMessage(raised, now) is { } message)
                {
                    _context.OutboxMessages.Add(message);
                }
            }

            aggregate.ClearDomainEvents();
        }
    }

    /// <remarks>
    /// Mapping the domain event onto a published contract happens here, in
    /// infrastructure, because that is where serialisation belongs and because it
    /// lets the two shapes change independently. An event with no published
    /// contract simply produces no row.
    /// </remarks>
    private static OutboxMessage? ToOutboxMessage(IDomainEvent raised, DateTimeOffset now)
    {
        if (raised is not LedgerTransactionRecorded recorded)
        {
            return null;
        }

        // Derived from the transaction identifier rather than random, so that a
        // retried request producing the same transaction cannot produce a second
        // message identifier -- and so a consumer's de-duplication holds even
        // across a republication.
        var messageId = recorded.TransactionId;

        var payload = JsonSerializer.Serialize(new LedgerTransactionRecordedMessage(
            messageId,
            recorded.TransactionId,
            recorded.Kind.ToString(),
            CurrencyName(recorded.Currency),
            recorded.OccurredAt));

        return OutboxMessage.Create(
            messageId,
            LedgerTransactionRecordedMessage.MessageType,
            recorded.TransactionId,
            payload,
            recorded.OccurredAt,
            now);
    }

    private static string CurrencyName(Currency currency) =>
        Enum.IsDefined(currency) ? currency.ToString() : "unknown";

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
