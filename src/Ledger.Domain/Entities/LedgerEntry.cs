using Ledger.Domain.ValueObjects;

namespace Ledger.Domain.Entities;

/// <summary>
/// One signed movement of value, on exactly one account, belonging to exactly
/// one transaction.
/// </summary>
/// <remarks>
/// <para>
/// The sign convention is that <b>positive increases the account's balance and
/// negative decreases it</b>. A single signed column was chosen over the classic
/// pair of debit and credit columns because it makes the invariant that matters
/// — the entries of a transaction sum to zero — one trivially checkable
/// aggregate, because arithmetic composes (a reversal is literally a negation),
/// and because it removes the perennial bug of a row with both columns populated
/// or neither.
/// </para>
/// <para>
/// <b>There is no public constructor.</b> The only way to obtain an entry is
/// through a factory on <see cref="LedgerTransaction"/>, and no factory can
/// return an unbalanced set. An unbalanced transaction is therefore not
/// "rejected" — it is unrepresentable.
/// </para>
/// <para>
/// An entry is immutable once created, and the database refuses updates and
/// deletes outright. A mistake is corrected by recording a reversal, never by
/// rewriting history.
/// </para>
/// </remarks>
public sealed class LedgerEntry
{
    internal LedgerEntry(Guid transactionId, Guid accountId, Money amount, short entryIndex)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.IsZero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A ledger entry must move a non-zero amount.");
        }

        TransactionId = transactionId;
        AccountId = accountId;
        Amount = amount;
        EntryIndex = entryIndex;
    }

    /// <summary>Only for the persistence layer's materializer. See <see cref="Account"/>.</summary>
    private LedgerEntry() => Amount = null!;

    /// <summary>
    /// A physical identifier, assigned by the database.
    /// </summary>
    /// <remarks>
    /// The one place in this project where an identifier is not supplied by the
    /// caller, and deliberately so. An entry is never referenced by an external
    /// system — its business identity is the transaction it belongs to, which
    /// <em>is</em> caller-supplied. A monotonic integer buys compact indexes,
    /// insertion-order locality at volume, and a cheap total ordering that the
    /// future outbox can use as a streaming cursor.
    /// </remarks>
    public long Id { get; }

    public Guid TransactionId { get; }

    public Guid AccountId { get; }

    /// <summary>Signed: positive increases the account's balance, negative decreases it.</summary>
    public Money Amount { get; }

    /// <summary>The entry's position within its transaction, starting at zero.</summary>
    public short EntryIndex { get; }
}
