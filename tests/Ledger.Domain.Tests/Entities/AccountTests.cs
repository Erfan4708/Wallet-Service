using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;
using Ledger.Domain.ValueObjects;

namespace Ledger.Domain.Tests.Entities;

public class AccountTests
{
    private static readonly Guid AccountId = new("11111111-1111-1111-1111-111111111111");

    private static Account OpenUsdAccount() => Account.Open(AccountId, Currency.USD);

    private static Money Usd(decimal amount) => new(amount, Currency.USD);

    [Fact]
    public void A_new_account_starts_empty_in_its_own_currency()
    {
        var account = OpenUsdAccount();

        Assert.Equal(AccountId, account.Id);
        Assert.Equal(Currency.USD, account.Currency);
        Assert.Equal(Money.Zero(Currency.USD), account.Balance);
    }

    [Fact]
    public void An_account_cannot_be_opened_without_an_identifier()
    {
        Assert.Throws<ArgumentException>(() => Account.Open(Guid.Empty, Currency.USD));
    }

    [Fact]
    public void Crediting_increases_the_balance()
    {
        var account = OpenUsdAccount();

        account.Credit(Usd(100m));
        account.Credit(Usd(0.50m));

        Assert.Equal(Usd(100.50m), account.Balance);
    }

    [Fact]
    public void Debiting_decreases_the_balance()
    {
        var account = OpenUsdAccount();
        account.Credit(Usd(100m));

        account.Debit(Usd(30m));

        Assert.Equal(Usd(70m), account.Balance);
    }

    [Fact]
    public void An_account_can_be_emptied_exactly()
    {
        var account = OpenUsdAccount();
        account.Credit(Usd(100m));

        account.Debit(Usd(100m));

        Assert.True(account.Balance.IsZero);
    }

    [Fact]
    public void Debiting_more_than_the_balance_is_refused()
    {
        var account = OpenUsdAccount();
        account.Credit(Usd(100m));

        var exception = Assert.Throws<InsufficientFundsException>(() => account.Debit(Usd(100.01m)));

        Assert.Equal(AccountId, exception.AccountId);
        Assert.Equal(Usd(100m), exception.Balance);
        Assert.Equal(Usd(100.01m), exception.Requested);
    }

    // A refused operation must not apply partially: the balance has to be
    // exactly what it was before the attempt.
    [Fact]
    public void A_refused_debit_leaves_the_balance_untouched()
    {
        var account = OpenUsdAccount();
        account.Credit(Usd(100m));

        Assert.Throws<InsufficientFundsException>(() => account.Debit(Usd(500m)));

        Assert.Equal(Usd(100m), account.Balance);
    }

    [Fact]
    public void An_empty_account_cannot_be_debited()
    {
        var account = OpenUsdAccount();

        Assert.Throws<InsufficientFundsException>(() => account.Debit(Usd(0.01m)));
    }

    [Fact]
    public void Money_in_another_currency_cannot_be_credited()
    {
        var account = OpenUsdAccount();

        Assert.Throws<CurrencyMismatchException>(() => account.Credit(new Money(100m, Currency.EUR)));
    }

    [Fact]
    public void Money_in_another_currency_cannot_be_debited()
    {
        var account = OpenUsdAccount();
        account.Credit(Usd(100m));

        Assert.Throws<CurrencyMismatchException>(() => account.Debit(new Money(1m, Currency.EUR)));
    }

    // A negative credit would be a debit in disguise and would never be checked
    // against the balance, which is how an account ends up overdrawn through the
    // deposit path.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Credits_must_be_positive(int amount)
    {
        var account = OpenUsdAccount();

        Assert.Throws<ArgumentOutOfRangeException>(() => account.Credit(Usd(amount)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Debits_must_be_positive(int amount)
    {
        var account = OpenUsdAccount();
        account.Credit(Usd(100m));

        Assert.Throws<ArgumentOutOfRangeException>(() => account.Debit(Usd(amount)));
    }

    [Fact]
    public void A_sequence_of_movements_leaves_the_expected_balance()
    {
        var account = OpenUsdAccount();

        account.Credit(Usd(0.10m));
        account.Credit(Usd(0.20m));
        account.Debit(Usd(0.30m));

        Assert.True(account.Balance.IsZero);
    }
}
