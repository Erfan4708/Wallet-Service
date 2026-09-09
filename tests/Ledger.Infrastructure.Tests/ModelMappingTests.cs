using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Ledger.Domain.ValueObjects;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// Verifies the EF Core model without a database.
/// </summary>
/// <remarks>
/// EF Core builds its model from the configuration alone and opens no connection
/// until a query runs, so these assertions cost milliseconds and need no
/// container. They are a complement to the PostgreSQL tests, not a substitute:
/// this file proves the mapping <em>says</em> the right thing, while the
/// container tests prove PostgreSQL then <em>does</em> the right thing. Both
/// matter, and only the second would catch a provider that quietly ignored a
/// setting.
/// </remarks>
public class ModelMappingTests
{
    /// <remarks>
    /// The design-time model rather than the runtime one. The runtime model is
    /// trimmed to what querying needs, so schema-only configuration such as
    /// check constraints is deliberately absent from it.
    /// </remarks>
    private static IModel Model()
    {
        using var context = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>()
                .UseNpgsql("Host=offline;Database=ledger")
                .Options);

        return context.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType AccountType() => Model().FindEntityType(typeof(Account))!;

    private static IEntityType BalanceType() =>
        AccountType().FindNavigation(nameof(Account.Balance))!.TargetEntityType;

    private static IProperty BalanceProperty(string name) => BalanceType().FindProperty(name)!;

    [Fact]
    public void The_account_maps_to_the_accounts_table()
    {
        Assert.Equal("accounts", AccountType().GetTableName());
    }

    [Fact]
    public void The_identifier_is_the_primary_key_and_is_never_generated()
    {
        var id = AccountType().FindProperty(nameof(Account.Id))!;

        Assert.Equal("id", id.GetColumnName());
        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);
        Assert.Equal([id], AccountType().FindPrimaryKey()!.Properties);
    }

    // The single most important assertion in this file. If the money column is
    // ever mapped to a binary floating point type, every balance in the system
    // becomes approximate and the ledger stops balancing.
    [Fact]
    public void The_balance_amount_is_an_exact_numeric_column()
    {
        var amount = BalanceProperty(nameof(Money.Amount));

        Assert.Equal("balance_amount", amount.GetColumnName());
        Assert.Equal("numeric(19,4)", amount.GetColumnType());
        Assert.False(amount.IsNullable);
    }

    [Fact]
    public void The_balance_amount_keeps_the_intended_precision_and_scale()
    {
        var amount = BalanceProperty(nameof(Money.Amount));

        Assert.Equal(19, amount.GetPrecision());
        Assert.Equal(4, amount.GetScale());
    }

    [Fact]
    public void The_currency_is_a_three_character_text_column()
    {
        var currency = BalanceProperty(nameof(Money.Currency));

        Assert.Equal("balance_currency", currency.GetColumnName());
        Assert.Equal("character varying(3)", currency.GetColumnType());
        Assert.False(currency.IsNullable);
    }

    // Stored as the ISO 4217 alpha code, never the enum's ordinal position.
    // Ordinals are positional, so inserting or reordering an enum member would
    // silently change what every already-stored row means.
    [Theory]
    [InlineData(Currency.USD, "USD")]
    [InlineData(Currency.EUR, "EUR")]
    [InlineData(Currency.IRR, "IRR")]
    public void The_currency_is_stored_as_its_alpha_code(Currency currency, string expected)
    {
        var converter = BalanceProperty(nameof(Money.Currency)).GetValueConverter()!;

        Assert.Equal(expected, converter.ConvertToProvider(currency));
        Assert.Equal(currency, converter.ConvertFromProvider(expected));
    }

    [Fact]
    public void The_balance_and_currency_constraints_are_part_of_the_model()
    {
        var names = AccountType().GetCheckConstraints().Select(constraint => constraint.Name).ToList();

        Assert.Contains("ck_accounts_balance_not_negative", names);
        Assert.Contains("ck_accounts_currency_is_known", names);
    }

    // Indexes are not free: each one is written on every insert and update. The
    // two current access patterns both find an account by its primary key, so
    // any additional index would cost writes to serve a query nobody makes.
    [Fact]
    public void No_index_exists_beyond_the_primary_key()
    {
        Assert.Empty(AccountType().GetIndexes());
    }

    [Fact]
    public void The_account_table_has_exactly_the_columns_the_domain_needs()
    {
        // Read from the relational model, which is the table as it will actually
        // be created. Asking a property for its column name in isolation gives
        // the owned type's default rather than the column it shares with its
        // owner, which is how the balance's key ends up looking like a fourth
        // column that the migration never creates.
        var table = Model().GetRelationalModel().Tables.Single(table => table.Name == "accounts");

        var columns = table.Columns
            .Select(column => column.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["balance_amount", "balance_currency", "id"], columns);
    }
}
