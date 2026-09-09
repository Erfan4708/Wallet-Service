using Ledger.Domain.Enums;
using Npgsql;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// The database's own guarantees, tested by going around the domain.
/// </summary>
/// <remarks>
/// Every insert here is raw SQL. That is the point: the domain already refuses
/// all of this, and these tests prove the refusal survives even when the domain
/// is not involved — a bad migration, a second service, an admin at a psql
/// prompt, or a future refactor that forgets the rule. For the invariants a
/// ledger exists to guarantee, one layer of defence is not enough.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LedgerConstraintTests : IAsyncLifetime
{
    private static readonly Guid WalletId = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid EuroWalletId = new("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid SettlementUsd = new("00000000-0000-0000-0000-000000000840");

    private const string CheckViolation = "23514";
    private const string ForeignKeyViolation = "23503";
    private const string RaisedException = "P0001";

    private readonly PostgresFixture _postgres;

    public LedgerConstraintTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.ResetAsync();

        await using var host = new LedgerHost(_postgres.CreateContext());
        await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
        await host.OpenWalletAsync(EuroWalletId, Currency.EUR);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // The invariant the whole system exists to guarantee, enforced at COMMIT by a
    // deferred constraint trigger. It cannot be a CHECK, because a CHECK cannot
    // see other rows, and it cannot fire immediately, because entries arrive one
    // at a time and the sum is legitimately non-zero in between.
    [RequiresDockerFact]
    public async Task An_unbalanced_transaction_is_refused_at_commit()
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            WriteRawTransactionAsync(
                (WalletId, 100m),
                (SettlementUsd, -60m)));

        Assert.Equal(RaisedException, failure.SqlState);
        Assert.Contains("does not balance", failure.MessageText, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task A_single_sided_transaction_is_refused()
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            WriteRawTransactionAsync((WalletId, 100m)));

        Assert.Equal(RaisedException, failure.SqlState);
        Assert.Contains("at least two", failure.MessageText, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task A_balanced_transaction_written_directly_is_accepted()
    {
        // The control for the two tests above: the trigger rejects what is wrong
        // rather than everything.
        await WriteRawTransactionAsync((WalletId, 10m), (SettlementUsd, -10m));

        Assert.Equal(1, await CountEntriesForAsync(WalletId) - 1);
    }

    // --------------------------------------------------------- append-only

    [RequiresDockerFact]
    public async Task An_entry_cannot_be_updated()
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync("UPDATE ledger_entries SET amount = amount + 1 WHERE account_id = @id",
                ("id", WalletId)));

        Assert.Equal(RaisedException, failure.SqlState);
        Assert.Contains("append-only", failure.MessageText, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task An_entry_cannot_be_deleted()
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync("DELETE FROM ledger_entries WHERE account_id = @id", ("id", WalletId)));

        Assert.Equal(RaisedException, failure.SqlState);
        Assert.Contains("append-only", failure.MessageText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- currency

    // A composite foreign key from (account_id, currency) to the account's own
    // (id, currency) makes this structurally impossible rather than merely
    // checked. There is no code path, however careless, that can post a dollar
    // entry to a euro account.
    [RequiresDockerFact]
    public async Task An_entry_cannot_be_posted_to_an_account_of_another_currency()
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            WriteRawTransactionAsync(
                currency: "USD",
                legs: [(EuroWalletId, 10m), (SettlementUsd, -10m)]));

        Assert.Equal(ForeignKeyViolation, failure.SqlState);
        Assert.Equal("fk_ledger_entries_account_currency", failure.ConstraintName);
    }

    // The mirror constraint: every entry must share its transaction's currency, so
    // a mixed-currency transaction cannot be stored at all.
    [RequiresDockerFact]
    public async Task A_transaction_cannot_mix_currencies()
    {
        var transactionId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await InsertTransactionAsync(connection, transaction, transactionId, "USD");
        await InsertEntryAsync(connection, transaction, transactionId, WalletId, 10m, "USD", 0);

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertEntryAsync(connection, transaction, transactionId, EuroWalletId, -10m, "EUR", 1));

        Assert.Equal(ForeignKeyViolation, failure.SqlState);
        Assert.Equal("fk_ledger_entries_transaction_currency", failure.ConstraintName);
    }

    // --------------------------------------------------------------- amounts

    [RequiresDockerFact]
    public async Task An_entry_of_zero_is_refused()
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            WriteRawTransactionAsync((WalletId, 0m), (SettlementUsd, 0m)));

        Assert.Equal(CheckViolation, failure.SqlState);
        Assert.Equal("ck_ledger_entries_amount_not_zero", failure.ConstraintName);
    }

    // --------------------------------------------------------------- balances

    [RequiresDockerFact]
    public async Task A_wallet_cannot_be_driven_negative_behind_the_domains_back()
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync("UPDATE accounts SET balance_amount = -1 WHERE id = @id", ("id", WalletId)));

        Assert.Equal(CheckViolation, failure.SqlState);
        Assert.Equal("ck_accounts_balance_not_negative", failure.ConstraintName);
    }

    [RequiresDockerFact]
    public async Task A_system_account_may_hold_a_negative_balance()
    {
        await ExecuteAsync(
            "UPDATE accounts SET balance_amount = -1000 WHERE id = @id", ("id", SettlementUsd));

        Assert.Equal(
            -1000m, await LedgerInvariants.BalanceOfAsync(_postgres.ConnectionString, SettlementUsd));
    }

    [RequiresDockerFact]
    public async Task A_wallet_cannot_be_given_a_system_key()
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync("UPDATE accounts SET system_key = 'SETTLEMENT:FAKE' WHERE id = @id",
                ("id", WalletId)));

        Assert.Equal(CheckViolation, failure.SqlState);
        Assert.Equal("ck_accounts_system_key_matches_type", failure.ConstraintName);
    }

    // ------------------------------------------------------------- plumbing

    private Task WriteRawTransactionAsync(params (Guid AccountId, decimal Amount)[] legs) =>
        WriteRawTransactionAsync("USD", legs);

    private async Task WriteRawTransactionAsync(
        string currency,
        (Guid AccountId, decimal Amount)[] legs)
    {
        var transactionId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await InsertTransactionAsync(connection, transaction, transactionId, currency);

        for (short index = 0; index < legs.Length; index++)
        {
            var leg = legs[index];
            await InsertEntryAsync(connection, transaction, transactionId, leg.AccountId, leg.Amount, currency, index);
        }

        // The deferred trigger fires here, not on the inserts above.
        await transaction.CommitAsync();
    }

    private static async Task InsertTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid transactionId,
        string currency)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger_transactions (id, kind, currency, occurred_at)
            VALUES (@id, 'Deposit', @currency, now())
            """, connection, transaction);

        command.Parameters.AddWithValue("id", transactionId);
        command.Parameters.AddWithValue("currency", currency);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid transactionId,
        Guid accountId,
        decimal amount,
        string currency,
        short index)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger_entries (transaction_id, account_id, amount, currency, entry_index)
            VALUES (@transaction, @account, @amount, @currency, @index)
            """, connection, transaction);

        command.Parameters.AddWithValue("transaction", transactionId);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("currency", currency);
        command.Parameters.AddWithValue("index", index);

        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountEntriesForAsync(Guid accountId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM ledger_entries WHERE account_id = @id", connection);
        command.Parameters.AddWithValue("id", accountId);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
