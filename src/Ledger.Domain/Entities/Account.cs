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
    private Account(Guid id, Money balance)
    {
        Id = id;
        Balance = balance;
    }

    public Guid Id { get; }

    /// <summary>
    /// The current balance.
    /// </summary>
    /// <remarks>
    /// Readable but not assignable from outside. Every change has to go through
    /// <see cref="Credit"/> or <see cref="Debit"/>, which is what makes the
    /// "a balance may not go negative" rule enforceable rather than merely
    /// documented.
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
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An account requires an identifier.", nameof(id));
        }

        return new Account(id, Money.Zero(currency));
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
    public void Credit(Money amount)
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
    public void Debit(Money amount)
    {
        EnsurePositiveAmountInAccountCurrency(amount);

        if (Balance < amount)
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
