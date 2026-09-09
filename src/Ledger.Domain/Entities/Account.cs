using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;
using Ledger.Domain.ValueObjects;

namespace Ledger.Domain.Entities;

/// <summary>
/// An account holding a balance in a single currency.
/// </summary>
/// <remarks>
/// <para>
/// This is an <em>entity</em>, not a value object: it has an identity that
/// outlives any particular balance. Two accounts with the same balance are not
/// the same account.
/// </para>
/// <para>
/// <b>Why the identifier is a parameter.</b> The domain does not call
/// <c>Guid.NewGuid()</c> (nor the clock, nor anything else ambient) so that its
/// behaviour is a pure function of its inputs and every test is deterministic.
/// Choosing an identifier is the caller's job.
/// </para>
/// <para>
/// <b>Why the balance is stored rather than derived.</b> Summing the ledger on
/// every read means loading an account's whole history to answer one question,
/// and leaves nothing to lock when concurrency is addressed. The stored balance
/// is a projection; once ledger entries exist, they are the audit truth and the
/// two must be provably in agreement. This is an open decision worth recording
/// in an ADR.
/// </para>
/// </remarks>
public sealed class Account
{
    private Account(Guid id, AccountType type, Money balance, string? systemKey)
    {
        Id = id;
        Type = type;
        Balance = balance;
        SystemKey = systemKey;
    }

    /// <summary>
    /// Only for the persistence layer's materializer.
    /// </summary>
    /// <remarks>
    /// An object-relational mapper rebuilds an object from a row without going
    /// through the domain's factory, and it cannot pass <see cref="Balance"/> to
    /// a constructor because that value is reconstructed from its own columns.
    /// It therefore needs a constructor it can call with nothing and populate
    /// afterwards.
    /// <para>
    /// This concedes nothing to the domain's invariants. The constructor is
    /// private, so no caller outside this class can reach it; the only route to
    /// a new account remains <see cref="Open"/>, and the only route to a changed
    /// balance remains <see cref="Credit"/> and <see cref="Debit"/>. It also
    /// introduces no dependency: there is no attribute, no base class and no
    /// package reference here, so the domain still compiles with no knowledge
    /// that a database exists.
    /// </para>
    /// </remarks>
    private Account() => Balance = null!;

    public Guid Id { get; }

    /// <summary>
    /// What the account is for, which decides whether it may go negative.
    /// </summary>
    public AccountType Type { get; }

    /// <summary>
    /// The stable name of a system account, or <see langword="null"/> for a wallet.
    /// </summary>
    public string? SystemKey { get; }

    /// <summary>
    /// The current balance.
    /// </summary>
    /// <remarks>
    /// Readable but not assignable from outside, and the two methods that can
    /// change it are <c>internal</c> — reachable only from
    /// <see cref="LedgerTransaction"/>. A balance therefore cannot move without
    /// ledger entries recording why, which is what turns "the entries are the
    /// source of truth" from a claim into something the compiler enforces.
    /// <para>
    /// This value is a materialised projection of the account's entries, written
    /// in the same database transaction as they are. It is never stale, and if it
    /// ever disagrees with <c>SUM(entries)</c> the entries win.
    /// </para>
    /// </remarks>
    public Money Balance { get; private set; }

    public Currency Currency => Balance.Currency;

    /// <summary>
    /// Opens an account with a zero balance.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="currency"/> is not a defined currency.</exception>
    public static Account Open(Guid id, Currency currency)
    {
        EnsureIdentifier(id);

        return new Account(id, AccountType.Wallet, Money.Zero(currency), systemKey: null);
    }

    /// <summary>
    /// Opens a system account: the ledger's counterparty for money entering or
    /// leaving the platform.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Open"/> because the two are not interchangeable.
    /// A system account is permitted to go negative, so creating one has to be a
    /// deliberate act rather than a flag someone can pass to the ordinary
    /// factory by accident.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is empty, or <paramref name="systemKey"/> is blank.
    /// </exception>
    public static Account OpenSystem(Guid id, Currency currency, string systemKey)
    {
        EnsureIdentifier(id);

        if (string.IsNullOrWhiteSpace(systemKey))
        {
            throw new ArgumentException("A system account requires a key.", nameof(systemKey));
        }

        return new Account(id, AccountType.System, Money.Zero(currency), systemKey);
    }

    private static void EnsureIdentifier(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An account requires an identifier.", nameof(id));
        }
    }

    /// <summary>
    /// Increases the balance.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="amount"/> is not positive. A non-positive credit would be
    /// a disguised debit and would slip past the overdraft rule, so it is
    /// rejected rather than reinterpreted.
    /// </exception>
    /// <exception cref="CurrencyMismatchException">
    /// <paramref name="amount"/> is in a different currency to the account.
    /// </exception>
    internal void Credit(Money amount)
    {
        EnsurePositiveAmountInAccountCurrency(amount);

        Balance = Balance.Add(amount);
    }

    /// <summary>
    /// Decreases the balance.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is not positive.</exception>
    /// <exception cref="CurrencyMismatchException">
    /// <paramref name="amount"/> is in a different currency to the account.
    /// </exception>
    /// <exception cref="InsufficientFundsException">
    /// The balance is smaller than <paramref name="amount"/>. The balance is left
    /// untouched: a rejected operation must not apply partially.
    /// </exception>
    internal void Debit(Money amount)
    {
        EnsurePositiveAmountInAccountCurrency(amount);

        // A wallet holds a customer's money and may never go negative. A system
        // account is the platform's own position against the outside world, and a
        // negative balance there is the normal, meaningful state: it is how much
        // value has been issued into wallets.
        if (Type == AccountType.Wallet && Balance < amount)
        {
            throw new InsufficientFundsException(Id, Balance, amount);
        }

        Balance = Balance.Subtract(amount);
    }

    private void EnsurePositiveAmountInAccountCurrency(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Currency != Currency)
        {
            throw new CurrencyMismatchException(Currency, amount.Currency);
        }

        if (!amount.IsPositive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "The amount must be positive.");
        }
    }
}
