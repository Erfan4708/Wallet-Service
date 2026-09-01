using System.Globalization;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;

namespace Ledger.Domain.ValueObjects;

/// <summary>
/// An immutable amount of a single currency.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="decimal"/> and not <see cref="double"/>.</b> <c>double</c>
/// is binary floating point and cannot represent most decimal fractions
/// exactly, so errors accumulate across a sequence of operations and equality
/// comparisons become unreliable. <c>decimal</c> is base-10 with 28-29
/// significant digits, which makes the fractions money is actually written in
/// exact. It also throws on overflow rather than silently wrapping.
/// </para>
/// <para>
/// <b>Why negative amounts are allowed.</b> A negative amount is meaningful:
/// the debit side of a ledger entry, a reversal, an overdrawn balance. The rule
/// that a <em>wallet balance</em> may not go negative is a rule about accounts,
/// not about money, and it lives on <c>Account</c>. Keeping it out of here
/// avoids forcing every caller to carry a separate sign or direction flag.
/// </para>
/// <para>
/// <b>Why this is a class and not a positional <c>record</c>.</b> A record
/// would give equality and immutability for free, but its compiler-generated
/// <c>with</c> expression copies the object and assigns the properties directly,
/// bypassing the validation in the constructor. That is exactly the invariant
/// hole this type exists to close.
/// </para>
/// </remarks>
public sealed class Money : IEquatable<Money>
{
    /// <summary>
    /// Creates an amount of <paramref name="currency"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="currency"/> is not a defined currency, or
    /// <paramref name="amount"/> is more precise than the currency can express.
    /// </exception>
    public Money(decimal amount, Currency currency)
    {
        if (!Enum.IsDefined(currency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(currency), currency, "Unknown currency.");
        }

        // An amount that cannot be paid must not be representable. Rounding here
        // instead would hide the defect and quietly lose or invent fractions of
        // a cent; rounding is a business decision that belongs where the
        // fraction is actually created (interest, fees, splitting a bill), not
        // silently inside a constructor.
        var places = currency.DecimalPlaces();
        if (decimal.Round(amount, places, MidpointRounding.ToEven) != amount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                $"{currency} is expressed in {places} decimal places.");
        }

        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    /// <summary>Zero in the given currency.</summary>
    public static Money Zero(Currency currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    /// <exception cref="CurrencyMismatchException">The currencies differ.</exception>
    /// <exception cref="OverflowException">The result is outside the range of <see cref="decimal"/>.</exception>
    public Money Add(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameCurrency(other);

        return new Money(Amount + other.Amount, Currency);
    }

    /// <exception cref="CurrencyMismatchException">The currencies differ.</exception>
    /// <exception cref="OverflowException">The result is outside the range of <see cref="decimal"/>.</exception>
    public Money Subtract(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameCurrency(other);

        return new Money(Amount - other.Amount, Currency);
    }

    /// <summary>The same amount with the opposite sign.</summary>
    public Money Negate() => new(-Amount, Currency);

    /// <summary>
    /// Orders this amount against <paramref name="other"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="IComparable{T}"/>: comparison is only
    /// meaningful within a single currency, and an implementation that throws
    /// would break the contract that sorting APIs rely on.
    /// </remarks>
    /// <exception cref="CurrencyMismatchException">The currencies differ.</exception>
    public int CompareTo(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameCurrency(other);

        return Amount.CompareTo(other.Amount);
    }

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator -(Money value) => value.Negate();

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public bool Equals(Money? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Currency == other.Currency && Amount == other.Amount;
    }

    public override bool Equals(object? obj) => Equals(obj as Money);

    public override int GetHashCode() => HashCode.Combine(Amount, Currency);

    public static bool operator ==(Money? left, Money? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Money? left, Money? right) => !(left == right);

    /// <summary>
    /// Renders the amount in a fixed, culture-independent form, e.g. "100.00 USD".
    /// </summary>
    /// <remarks>
    /// <see cref="CultureInfo.InvariantCulture"/> is used on purpose: a value
    /// that appears in logs, audit records and exception messages must not
    /// change shape with the machine's locale.
    /// </remarks>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Amount.ToString("F" + Currency.DecimalPlaces().ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)} {Currency}");

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }
}
