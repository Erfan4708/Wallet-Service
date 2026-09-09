using Npgsql;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// The assertions that decide whether the ledger is correct.
/// </summary>
/// <remarks>
/// Everything else in this suite tests a mechanism. These two test the outcome,
/// and they are the ones worth running after any change: if money can neither be
/// created nor destroyed, and every stored balance is exactly what its entries
/// say it should be, then the mechanisms did their job whatever they look like.
/// </remarks>
internal static class LedgerInvariants
{
    /// <summary>
    /// Money was neither created nor destroyed: every entry in the database, of
    /// every currency, sums to zero.
    /// </summary>
    /// <remarks>
    /// This holds only because a deposit debits a settlement account rather than
    /// crediting a wallet out of nowhere. It is the single strongest statement
    /// this system can make about itself.
    /// </remarks>
    internal static async Task AssertNoMoneyWasCreatedAsync(string connectionString)
    {
        var totals = await QueryAsync(
            connectionString,
            """
            SELECT currency, COALESCE(SUM(amount), 0)::text
            FROM ledger_entries
            GROUP BY currency
            ORDER BY currency
            """);

        Assert.All(totals, row =>
            Assert.True(
                decimal.Parse(row.Second, System.Globalization.CultureInfo.InvariantCulture) == 0m,
                $"Entries in {row.First} sum to {row.Second}, not zero."));
    }

    /// <summary>
    /// Every stored balance equals the sum of that account's entries.
    /// </summary>
    /// <remarks>
    /// The stored balance is a projection of the entries, written in the same
    /// transaction. This is the reconciliation that proves the projection has not
    /// drifted — the obligation ADR-005 takes on in exchange for O(1) reads.
    /// </remarks>
    internal static async Task AssertBalancesReconcileAsync(string connectionString)
    {
        var mismatches = await QueryAsync(
            connectionString,
            """
            SELECT a.id::text, (a.balance_amount - COALESCE(e.total, 0))::text
            FROM accounts a
            LEFT JOIN (
                SELECT account_id, SUM(amount) AS total
                FROM ledger_entries
                GROUP BY account_id
            ) e ON e.account_id = a.id
            WHERE a.balance_amount <> COALESCE(e.total, 0)
            """);

        Assert.True(
            mismatches.Count == 0,
            "Stored balances disagree with the ledger: " +
            string.Join(", ", mismatches.Select(row => $"{row.First} off by {row.Second}")));
    }

    /// <summary>Both invariants, which is what should be asserted after any workload.</summary>
    internal static async Task AssertAllAsync(string connectionString)
    {
        await AssertNoMoneyWasCreatedAsync(connectionString);
        await AssertBalancesReconcileAsync(connectionString);
    }

    internal static async Task<decimal> BalanceOfAsync(string connectionString, Guid accountId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT balance_amount FROM accounts WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", accountId);

        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task<int> CountAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {table}", connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<(string First, string Second)>> QueryAsync(
        string connectionString,
        string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<(string, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }
}
