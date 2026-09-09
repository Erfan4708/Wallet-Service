using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Ledger.Domain.ValueObjects;

namespace Ledger.Application.Tests.Fakes;

/// <summary>Builds accounts the only way the domain now allows: through the ledger.</summary>
internal static class LedgerScenario
{
    /// <summary>
    /// The settlement account for a currency, with the identifier the migration
    /// seeds so that tests and the database agree.
    /// </summary>
    internal static Account Settlement(Currency currency) =>
        Account.OpenSystem(SettlementId(currency), currency, SystemAccountKeys.Settlement(currency));

    internal static Guid SettlementId(Currency currency) => currency switch
    {
        Currency.USD => new Guid("00000000-0000-0000-0000-000000000840"),
        Currency.EUR => new Guid("00000000-0000-0000-0000-000000000978"),
        Currency.IRR => new Guid("00000000-0000-0000-0000-000000000364"),
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, "Unknown currency."),
    };

    /// <summary>A wallet holding <paramref name="amount"/>, funded by a real deposit.</summary>
    internal static Account FundedWallet(Guid id, Currency currency, decimal amount) =>
        FundedWallet(id, currency, amount, Settlement(currency));

    internal static Account FundedWallet(Guid id, Currency currency, decimal amount, Account settlement)
    {
        var wallet = Account.Open(id, currency);

        if (amount > 0m)
        {
            LedgerTransaction.Deposit(
                Guid.NewGuid(), wallet, settlement, new Money(amount, currency), DateTimeOffset.UnixEpoch);
        }

        return wallet;
    }
}
