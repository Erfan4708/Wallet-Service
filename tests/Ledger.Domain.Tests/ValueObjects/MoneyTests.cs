using System.Globalization;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;
using Ledger.Domain.ValueObjects;

namespace Ledger.Domain.Tests.ValueObjects;

public class MoneyTests
{
    [Fact]
    public void Constructor_keeps_the_amount_and_currency_it_was_given()
    {
        var money = new Money(100.25m, Currency.USD);

        Assert.Equal(100.25m, money.Amount);
        Assert.Equal(Currency.USD, money.Currency);
    }

    [Fact]
    public void Zero_is_zero_in_the_requested_currency()
    {
        var zero = Money.Zero(Currency.EUR);

        Assert.True(zero.IsZero);
        Assert.Equal(Currency.EUR, zero.Currency);
    }

    [Fact]
    public void Negative_amounts_are_allowed()
    {
        var money = new Money(-10m, Currency.USD);

        Assert.True(money.IsNegative);
        Assert.Equal(-10m, money.Amount);
    }

    [Fact]
    public void Undefined_currency_values_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Money(1m, (Currency)9999));
    }

    // The bug this guards against: decimal happily stores 0.001 USD, which is
    // not an amount anyone can pay. Letting it in means a fraction of a cent
    // enters the books and has to be silently lost or invented later.
    [Fact]
    public void Amounts_more_precise_than_the_currency_allows_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Money(0.001m, Currency.USD));
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("1.10")]
    [InlineData("1.100")]
    public void Trailing_zeros_do_not_count_as_extra_precision(string amount)
    {
        var money = new Money(decimal.Parse(amount, CultureInfo.InvariantCulture), Currency.USD);

        Assert.Equal(1.1m, money.Amount);
    }

    [Fact]
    public void Amounts_with_the_same_value_and_currency_are_equal()
    {
        var left = new Money(100m, Currency.USD);
        var right = new Money(100m, Currency.USD);

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    // 1.10m and 1.1m are equal but are stored with different scales. If their
    // hash codes disagreed, equal amounts would land in different hash buckets.
    [Fact]
    public void Equal_amounts_written_with_different_scales_hash_identically()
    {
        var left = new Money(1.1m, Currency.USD);
        var right = new Money(1.10m, Currency.USD);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Amounts_in_different_currencies_are_never_equal()
    {
        var dollars = new Money(100m, Currency.USD);
        var euros = new Money(100m, Currency.EUR);

        Assert.NotEqual(dollars, euros);
        Assert.True(dollars != euros);
    }

    [Fact]
    public void Different_amounts_in_the_same_currency_are_not_equal()
    {
        Assert.NotEqual(new Money(100m, Currency.USD), new Money(100.01m, Currency.USD));
    }

    [Fact]
    public void Money_is_never_equal_to_null()
    {
        var money = new Money(1m, Currency.USD);

        Assert.False(money.Equals(null));
        Assert.False(money == null);
        Assert.True(money != null);
    }

    [Fact]
    public void Two_nulls_compare_equal()
    {
        Money? left = null;
        Money? right = null;

        Assert.True(left == right);
    }

    [Fact]
    public void Adding_two_amounts_of_the_same_currency_sums_them()
    {
        var sum = new Money(100.10m, Currency.USD) + new Money(0.20m, Currency.USD);

        Assert.Equal(new Money(100.30m, Currency.USD), sum);
    }

    // The classic floating-point failure: with double, 0.1 + 0.2 != 0.3.
    [Fact]
    public void Addition_of_fractional_amounts_is_exact()
    {
        var sum = new Money(0.10m, Currency.USD) + new Money(0.20m, Currency.USD);

        Assert.Equal(new Money(0.30m, Currency.USD), sum);
    }

    [Fact]
    public void Repeated_addition_does_not_drift()
    {
        var total = Money.Zero(Currency.USD);
        var cent = new Money(0.01m, Currency.USD);

        for (var i = 0; i < 1_000; i++)
        {
            total += cent;
        }

        Assert.Equal(new Money(10.00m, Currency.USD), total);
    }

    [Fact]
    public void Subtraction_can_produce_a_negative_amount()
    {
        var result = new Money(1m, Currency.USD) - new Money(3m, Currency.USD);

        Assert.Equal(new Money(-2m, Currency.USD), result);
    }

    [Fact]
    public void Arithmetic_leaves_its_operands_unchanged()
    {
        var left = new Money(100m, Currency.USD);
        var right = new Money(40m, Currency.USD);

        _ = left + right;
        _ = left - right;
        _ = -left;

        Assert.Equal(100m, left.Amount);
        Assert.Equal(40m, right.Amount);
    }

    [Fact]
    public void Negate_flips_the_sign_and_keeps_the_currency()
    {
        var negated = -new Money(25m, Currency.EUR);

        Assert.Equal(new Money(-25m, Currency.EUR), negated);
    }

    [Fact]
    public void Adding_different_currencies_is_refused()
    {
        var dollars = new Money(100m, Currency.USD);
        var euros = new Money(100m, Currency.EUR);

        var exception = Assert.Throws<CurrencyMismatchException>(() => { _ = dollars + euros; });

        Assert.Equal(Currency.USD, exception.Left);
        Assert.Equal(Currency.EUR, exception.Right);
    }

    [Fact]
    public void Subtracting_different_currencies_is_refused()
    {
        var dollars = new Money(100m, Currency.USD);
        var euros = new Money(100m, Currency.EUR);

        Assert.Throws<CurrencyMismatchException>(() => { _ = dollars - euros; });
    }

    [Fact]
    public void Comparing_different_currencies_is_refused()
    {
        var dollars = new Money(100m, Currency.USD);
        var euros = new Money(1m, Currency.EUR);

        Assert.Throws<CurrencyMismatchException>(() => { _ = dollars > euros; });
    }

    [Fact]
    public void Amounts_of_the_same_currency_order_by_value()
    {
        var small = new Money(1m, Currency.USD);
        var large = new Money(2m, Currency.USD);
        var alsoSmall = new Money(1m, Currency.USD);

        Assert.True(small < large);
        Assert.True(large > small);
        Assert.True(small <= alsoSmall);
        Assert.True(small >= alsoSmall);
    }

    // decimal overflows by throwing rather than wrapping, so a balance can never
    // silently roll over from a huge positive number to a negative one.
    [Fact]
    public void Overflow_throws_rather_than_wrapping_around()
    {
        var max = new Money(decimal.MaxValue, Currency.USD);

        Assert.Throws<OverflowException>(() => { _ = max + new Money(1m, Currency.USD); });
    }

    [Fact]
    public void ToString_is_culture_independent_and_shows_the_currency_scale()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");

        try
        {
            Assert.Equal("1234.50 USD", new Money(1234.5m, Currency.USD).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
