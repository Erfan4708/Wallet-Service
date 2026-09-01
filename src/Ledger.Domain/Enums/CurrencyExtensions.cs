namespace Ledger.Domain.Enums;

/// <summary>
/// Currency metadata the domain needs in order to reason about precision.
/// </summary>
public static class CurrencyExtensions
{
    /// <summary>
    /// The number of decimal places in which an amount of <paramref name="currency"/>
    /// can actually be paid — the ISO 4217 minor unit.
    /// </summary>
    /// <remarks>
    /// This lookup exists because currencies genuinely differ: most use two
    /// decimal places, the Japanese yen uses none, and the Kuwaiti dinar uses
    /// three. Hard-coding "2" anywhere in the domain would be a bug waiting for
    /// the first zero-decimal currency to be added.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not a defined <see cref="Currency"/>.
    /// </exception>
    public static int DecimalPlaces(this Currency currency) => currency switch
    {
        Currency.USD => 2,
        Currency.EUR => 2,
        Currency.IRR => 2,
        _ => throw new ArgumentOutOfRangeException(
            nameof(currency), currency, "Unknown currency."),
    };
}
