using System.Globalization;
using Ledger.Application.Exceptions;
using Ledger.Domain.Enums;
using Ledger.Domain.ValueObjects;

namespace Ledger.Application.Ledger;

/// <summary>
/// Turns the raw values in a command into a validated <see cref="Money"/>.
/// </summary>
/// <remarks>
/// The domain rejects these values too, but with <see cref="ArgumentException"/>
/// and friends, which mean "the caller has a bug" and are not something to
/// translate into a helpful response. Checking here turns bad input into a clear
/// 400 that names every offending field at once, and leaves the domain's guards
/// in place as the last line of defence.
/// </remarks>
internal static class LedgerCommandValidation
{
    /// <summary>The largest amount a single movement may carry.</summary>
    /// <remarks>
    /// Amounts and balances are stored as <c>numeric(19,4)</c>, which holds at
    /// most fifteen integer digits. A larger amount passes every business rule and
    /// then fails inside the database, where it becomes a 500; refusing it here
    /// makes it the client error it is. A balance can still exceed the column if
    /// enough large movements accumulate on one account.
    /// </remarks>
    internal const decimal MaximumAmount = 999_999_999_999_999.99m;

    /// <summary>The longest idempotency key or external reference the ledger stores.</summary>
    /// <remarks>Matches the <c>character varying(200)</c> columns they are stored in.</remarks>
    internal const int MaximumReferenceLength = 200;

    internal static Money BuildAmount(
        decimal amount,
        Currency currency,
        Dictionary<string, string[]> errors,
        string amountField = "Amount",
        string currencyField = "Currency")
    {
        if (!Enum.IsDefined(currency))
        {
            errors[currencyField] = ["Unknown currency."];

            // Nothing further can be said about the amount without knowing how
            // precise the currency allows it to be.
            return Money.Zero(Currency.USD);
        }

        if (amount <= 0m)
        {
            errors[amountField] = ["The amount must be greater than zero."];

            return Money.Zero(currency);
        }

        if (amount > MaximumAmount)
        {
            errors[amountField] =
                [string.Create(CultureInfo.InvariantCulture, $"The amount must not exceed {MaximumAmount}.")];

            return Money.Zero(currency);
        }

        var places = currency.DecimalPlaces();
        if (decimal.Round(amount, places, MidpointRounding.ToEven) != amount)
        {
            errors[amountField] = [$"{currency} is expressed in {places} decimal places."];

            return Money.Zero(currency);
        }

        return new Money(amount, currency);
    }

    internal static void RequireIdentifier(Guid value, string field, Dictionary<string, string[]> errors)
    {
        if (value == Guid.Empty)
        {
            errors[field] = ["An identifier is required."];
        }
    }

    /// <remarks>
    /// Measured as the value will be stored: surrounding whitespace is trimmed
    /// before a key or reference is kept.
    /// </remarks>
    internal static void RequireMaximumLength(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is not null && value.Trim().Length > MaximumReferenceLength)
        {
            errors[field] = [string.Create(
                CultureInfo.InvariantCulture, $"Must be at most {MaximumReferenceLength} characters.")];
        }
    }

    internal static void ThrowIfInvalid(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
