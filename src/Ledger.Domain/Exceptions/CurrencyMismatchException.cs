using Ledger.Domain.Enums;

namespace Ledger.Domain.Exceptions;

/// <summary>
/// Thrown when an operation would combine amounts in different currencies.
/// </summary>
/// <remarks>
/// Money in different currencies is not comparable or addable without an
/// exchange rate, and an exchange rate is a business decision with a source, a
/// timestamp and a spread. The domain therefore refuses the operation outright
/// rather than guessing.
/// </remarks>
public sealed class CurrencyMismatchException : DomainException
{
    public CurrencyMismatchException(Currency left, Currency right)
        : base($"Cannot combine amounts in different currencies: {left} and {right}.")
    {
        Left = left;
        Right = right;
    }

    public Currency Left { get; }

    public Currency Right { get; }
}
