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
public class SchemaTests
{
    private readonly PostgresFixture _postgres;

    public SchemaTests(PostgresFixture postgres) => _postgres = postgres;

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

    [RequiresDockerFact]
    public async Task Every_column_is_required()
    {
        var nullable = await QueryStringsAsync(
            "SELECT column_name FROM information_schema.columns " +
            "WHERE table_name = 'accounts' AND is_nullable = 'YES'");

        Assert.Empty(nullable);
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
                "INSERT INTO accounts (id, balance_amount, balance_currency) " +
                "VALUES (gen_random_uuid(), -1, 'USD')"));

        Assert.Equal("23514", failure.SqlState); // check_violation
    }

    [RequiresDockerFact]
    public async Task The_database_refuses_an_unknown_currency()
    {
        await using var context = _postgres.CreateContext();

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "INSERT INTO accounts (id, balance_amount, balance_currency) " +
                "VALUES (gen_random_uuid(), 0, 'XXX')"));

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
