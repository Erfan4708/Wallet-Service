using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// Verifies what the migrations actually build in PostgreSQL.
/// </summary>
/// <remarks>
/// Asserting against <c>information_schema</c> rather than against the EF Core
/// model checks the thing that matters. The model is what EF Core intends; the
/// catalogue is what the database really has, and a mapping mistake shows up as
/// a wrong column type there long before it shows up as a wrong number.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SchemaTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;

    public SchemaTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [RequiresDockerFact]
    public async Task Migrations_create_the_accounts_table()
    {
        var tables = await QueryStringsAsync(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'");

        Assert.Contains("accounts", tables);
    }

    [RequiresDockerFact]
    public async Task No_migration_is_left_pending()
    {
        await using var context = _postgres.CreateContext();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());
    }

    // The money column must be an exact base-10 type. If this ever reads
    // "double precision", every balance in the system is approximate.
    [RequiresDockerTheory]
    [InlineData("id", "uuid")]
    [InlineData("balance_amount", "numeric")]
    [InlineData("balance_currency", "character varying")]
    public async Task Columns_have_the_intended_types(string column, string expectedType)
    {
        var actual = await QueryStringsAsync(
            $"SELECT data_type FROM information_schema.columns " +
            $"WHERE table_name = 'accounts' AND column_name = '{column}'");

        Assert.Equal([expectedType], actual);
    }

    [RequiresDockerFact]
    public async Task The_money_column_has_the_intended_precision_and_scale()
    {
        var details = await QueryStringsAsync(
            "SELECT numeric_precision || ',' || numeric_scale FROM information_schema.columns " +
            "WHERE table_name = 'accounts' AND column_name = 'balance_amount'");

        Assert.Equal(["19,4"], details);
    }

    // Only the system key may be absent, and only because a wallet has none —
    // a check constraint ties its presence to the account type, so "nullable"
    // here does not mean "optional".
    [RequiresDockerFact]
    public async Task Only_the_system_key_is_nullable()
    {
        var nullable = await QueryStringsAsync(
            "SELECT column_name FROM information_schema.columns " +
            "WHERE table_name = 'accounts' AND is_nullable = 'YES'");

        Assert.Equal(["system_key"], nullable);
    }

    [RequiresDockerFact]
    public async Task No_ledger_column_is_nullable_except_the_optional_references()
    {
        var nullable = await QueryStringsAsync(
            "SELECT table_name || '.' || column_name FROM information_schema.columns " +
            "WHERE table_name IN ('ledger_entries', 'ledger_transactions') " +
            "AND is_nullable = 'YES' ORDER BY 1");

        Assert.Equal(
            [
                "ledger_transactions.external_reference",
                "ledger_transactions.idempotency_key",
                "ledger_transactions.reverses_transaction_id",
            ],
            nullable);
    }

    [RequiresDockerFact]
    public async Task The_balance_and_currency_check_constraints_exist()
    {
        var constraints = await QueryStringsAsync(
            "SELECT conname FROM pg_constraint WHERE conrelid = 'accounts'::regclass AND contype = 'c'");

        Assert.Contains("ck_accounts_balance_not_negative", constraints);
        Assert.Contains("ck_accounts_currency_is_known", constraints);
    }

    // Defence in depth: the domain refuses to overdraw an account, and so does
    // the database. This bypasses the domain entirely to prove the second
    // guarantee is real and not merely configured.
    [RequiresDockerFact]
    public async Task The_database_refuses_a_negative_balance_written_behind_the_domains_back()
    {
        await using var context = _postgres.CreateContext();

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "INSERT INTO accounts (id, account_type, balance_amount, balance_currency) " +
                "VALUES (gen_random_uuid(), 'Wallet', -1, 'USD')"));

        Assert.Equal("23514", failure.SqlState); // check_violation
    }

    // The mirror of the rule above, and the reason the check is conditional
    // rather than blanket: a system account's negative balance is how much value
    // the platform has issued into wallets.
    [RequiresDockerFact]
    public async Task The_database_permits_a_system_account_to_go_negative()
    {
        await using var context = _postgres.CreateContext();

        var affected = await context.Database.ExecuteSqlRawAsync(
            "UPDATE accounts SET balance_amount = -500 WHERE system_key = 'SETTLEMENT:USD'");

        Assert.Equal(1, affected);
    }

    [RequiresDockerFact]
    public async Task The_database_refuses_an_unknown_currency()
    {
        await using var context = _postgres.CreateContext();

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "INSERT INTO accounts (id, account_type, balance_amount, balance_currency) " +
                "VALUES (gen_random_uuid(), 'Wallet', 0, 'XXX')"));

        Assert.Equal("23514", failure.SqlState);
    }

    private async Task<List<string>> QueryStringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var results = new List<string>();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
