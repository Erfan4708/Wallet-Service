using Ledger.Application.Exceptions;
using Ledger.Application.Ledger;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// The ledger use cases against a real PostgreSQL database.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LedgerPersistenceTests : IAsyncLifetime
{
    private static readonly Guid WalletId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid OtherWalletId = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid SettlementUsd = new("00000000-0000-0000-0000-000000000840");

    private readonly PostgresFixture _postgres;

    public LedgerPersistenceTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private LedgerHost NewHost() => new(_postgres.CreateContext());

    // ---------------------------------------------------------------- deposit

    [RequiresDockerFact]
    public async Task A_deposit_is_persisted_as_two_balanced_entries()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
        }

        Assert.Equal(1, await LedgerInvariants.CountAsync(_postgres.ConnectionString, "ledger_transactions"));
        Assert.Equal(2, await LedgerInvariants.CountAsync(_postgres.ConnectionString, "ledger_entries"));
        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    // A deposit takes money from the settlement account rather than inventing it.
    // The negative settlement balance is the platform's issued position, and it is
    // what reconciles against the bank rail.
    [RequiresDockerFact]
    public async Task A_deposit_moves_the_settlement_account_negative()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
        }

        Assert.Equal(-100m, await LedgerInvariants.BalanceOfAsync(_postgres.ConnectionString, SettlementUsd));
    }

    [RequiresDockerFact]
    public async Task A_deposit_survives_being_read_through_a_different_context()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 250.75m);
        }

        await using var reader = NewHost();

        Assert.Equal(250.75m, await reader.BalanceOfAsync(WalletId));
    }

    // ------------------------------------------------------------- withdrawal

    [RequiresDockerFact]
    public async Task A_withdrawal_reduces_the_balance_and_reconciles()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
            await host.Withdraw.HandleAsync(new WithdrawCommand(Guid.NewGuid(), WalletId, 40m, Currency.USD));
        }

        await using var reader = NewHost();

        Assert.Equal(60m, await reader.BalanceOfAsync(WalletId));
        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    [RequiresDockerFact]
    public async Task A_withdrawal_beyond_the_balance_is_refused_and_persists_nothing()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 50m);
        }

        await using (var host = NewHost())
        {
            await Assert.ThrowsAsync<InsufficientFundsException>(() =>
                host.Withdraw.HandleAsync(new WithdrawCommand(Guid.NewGuid(), WalletId, 50.01m, Currency.USD)));
        }

        await using var reader = NewHost();

        Assert.Equal(50m, await reader.BalanceOfAsync(WalletId));

        // The failed attempt left no transaction behind: one deposit, two entries.
        Assert.Equal(1, await LedgerInvariants.CountAsync(_postgres.ConnectionString, "ledger_transactions"));
        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    // --------------------------------------------------------------- transfer

    [RequiresDockerFact]
    public async Task A_transfer_moves_money_and_leaves_the_ledger_balanced()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
            await host.OpenWalletAsync(OtherWalletId, Currency.USD);

            await host.Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), WalletId, OtherWalletId, 30m, Currency.USD));
        }

        await using var reader = NewHost();

        Assert.Equal(70m, await reader.BalanceOfAsync(WalletId));
        Assert.Equal(30m, await reader.BalanceOfAsync(OtherWalletId));
        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    // A transfer moves money between wallets without touching the outside world,
    // so the platform's issued position must not change.
    [RequiresDockerFact]
    public async Task A_transfer_does_not_change_the_settlement_position()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
            await host.OpenWalletAsync(OtherWalletId, Currency.USD);

            await host.Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), WalletId, OtherWalletId, 30m, Currency.USD));
        }

        Assert.Equal(-100m, await LedgerInvariants.BalanceOfAsync(_postgres.ConnectionString, SettlementUsd));
    }

    [RequiresDockerFact]
    public async Task A_transfer_between_currencies_is_refused()
    {
        await using var host = NewHost();

        await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
        await host.OpenWalletAsync(OtherWalletId, Currency.EUR);

        await Assert.ThrowsAsync<CurrencyMismatchException>(() =>
            host.Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), WalletId, OtherWalletId, 30m, Currency.USD)));
    }

    // --------------------------------------------------------------- reversal

    [RequiresDockerFact]
    public async Task A_reversal_restores_the_balances_and_keeps_the_original_entries()
    {
        var transferId = Guid.NewGuid();

        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
            await host.OpenWalletAsync(OtherWalletId, Currency.USD);

            await host.Transfer.HandleAsync(
                new TransferCommand(transferId, WalletId, OtherWalletId, 30m, Currency.USD));
        }

        await using (var host = NewHost())
        {
            await host.Reverse.HandleAsync(new ReverseTransactionCommand(Guid.NewGuid(), transferId));
        }

        await using var reader = NewHost();

        Assert.Equal(100m, await reader.BalanceOfAsync(WalletId));
        Assert.Equal(0m, await reader.BalanceOfAsync(OtherWalletId));

        // History is appended to, never rewritten: the deposit, the transfer and
        // the reversal are all still there.
        Assert.Equal(3, await LedgerInvariants.CountAsync(_postgres.ConnectionString, "ledger_transactions"));
        Assert.Equal(6, await LedgerInvariants.CountAsync(_postgres.ConnectionString, "ledger_entries"));
        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    // The unique index on the reversed-transaction column, not just the in-memory
    // check, is what makes this impossible.
    [RequiresDockerFact]
    public async Task A_transaction_cannot_be_reversed_twice()
    {
        var depositId = Guid.NewGuid();

        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD);
            await host.Deposit.HandleAsync(new DepositCommand(depositId, WalletId, 100m, Currency.USD));
            await host.Reverse.HandleAsync(new ReverseTransactionCommand(Guid.NewGuid(), depositId));
        }

        await using var second = NewHost();

        await Assert.ThrowsAsync<TransactionAlreadyReversedException>(() =>
            second.Reverse.HandleAsync(new ReverseTransactionCommand(Guid.NewGuid(), depositId)));
    }

    // ------------------------------------------------------------ idempotency

    [RequiresDockerFact]
    public async Task A_replayed_deposit_moves_the_money_once()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD);
        }

        LedgerTransactionResult first;
        await using (var host = NewHost())
        {
            first = await host.Deposit.HandleAsync(
                new DepositCommand(Guid.NewGuid(), WalletId, 100m, Currency.USD, "retry-me"));
        }

        LedgerTransactionResult second;
        await using (var host = NewHost())
        {
            second = await host.Deposit.HandleAsync(
                new DepositCommand(Guid.NewGuid(), WalletId, 100m, Currency.USD, "retry-me"));
        }

        Assert.False(first.WasReplayed);
        Assert.True(second.WasReplayed);
        Assert.Equal(first.TransactionId, second.TransactionId);

        await using var reader = NewHost();
        Assert.Equal(100m, await reader.BalanceOfAsync(WalletId));
        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    [RequiresDockerFact]
    public async Task Reusing_an_idempotency_key_for_a_different_amount_is_a_conflict()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletId, Currency.USD);
            await host.Deposit.HandleAsync(
                new DepositCommand(Guid.NewGuid(), WalletId, 100m, Currency.USD, "key-1"));
        }

        await using var second = NewHost();

        await Assert.ThrowsAsync<ConflictException>(() =>
            second.Deposit.HandleAsync(
                new DepositCommand(Guid.NewGuid(), WalletId, 250m, Currency.USD, "key-1")));
    }

    // ------------------------------------------------------------- statement

    [RequiresDockerFact]
    public async Task A_statement_explains_the_balance_it_reports()
    {
        await using var host = NewHost();

        await host.OpenWalletAsync(WalletId, Currency.USD, funding: 100m);
        await host.Withdraw.HandleAsync(new WithdrawCommand(Guid.NewGuid(), WalletId, 25m, Currency.USD));

        var statement = await host.Statement.HandleAsync(new GetAccountStatementQuery(WalletId));

        Assert.Equal(75m, statement.Balance);
        Assert.Equal(2, statement.Entries.Count);
        Assert.Equal(statement.Balance, statement.Entries.Sum(entry => entry.Amount));
    }

    // --------------------------------------------------------- mixed workload

    // The flagship assertion. After a varied sequence of operations the two
    // invariants must still hold: money was neither created nor destroyed, and
    // every stored balance is exactly what its entries say.
    [RequiresDockerFact]
    public async Task The_invariants_hold_after_a_mixed_workload()
    {
        var wallets = Enumerable.Range(1, 5)
            .Select(index => new Guid($"cccccccc-0000-0000-0000-00000000000{index}"))
            .ToList();

        await using (var host = NewHost())
        {
            foreach (var wallet in wallets)
            {
                await host.OpenWalletAsync(wallet, Currency.USD, funding: 100m);
            }
        }

        var reversible = Guid.NewGuid();

        await using (var host = NewHost())
        {
            await host.Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), wallets[0], wallets[1], 25.50m, Currency.USD));
            await host.Transfer.HandleAsync(
                new TransferCommand(reversible, wallets[1], wallets[2], 10.25m, Currency.USD));
            await host.Withdraw.HandleAsync(
                new WithdrawCommand(Guid.NewGuid(), wallets[3], 99.99m, Currency.USD));
            await host.Deposit.HandleAsync(
                new DepositCommand(Guid.NewGuid(), wallets[4], 0.01m, Currency.USD));
        }

        await using (var host = NewHost())
        {
            await host.Reverse.HandleAsync(new ReverseTransactionCommand(Guid.NewGuid(), reversible));
        }

        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }
}
