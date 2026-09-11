using Ledger.Application.Exceptions;
using Ledger.Application.Ledger;
using Ledger.Application.Tests.Fakes;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;

namespace Ledger.Application.Tests.Ledger;

/// <summary>
/// The ledger use cases against hand-written doubles.
/// </summary>
/// <remarks>
/// These check the parts a database cannot: that a use case asks for a
/// transaction before it writes, that it locks before it reads, that validation
/// names every bad field at once, and that a replayed request returns the
/// original result rather than repeating the work. Whether PostgreSQL then keeps
/// its side of the bargain is a question for the integration tests.
/// </remarks>
public class LedgerHandlerTests
{
    private static readonly Guid TransactionId = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid WalletId = new("22222222-0000-0000-0000-000000000001");
    private static readonly Guid OtherWalletId = new("22222222-0000-0000-0000-000000000002");

    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeLedgerTransactionRepository _transactions = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private DepositHandler Deposit => new(_accounts, _transactions, _unitOfWork, TimeProvider.System, TestTelemetry.Instance);

    private WithdrawHandler Withdraw => new(_accounts, _transactions, _unitOfWork, TimeProvider.System, TestTelemetry.Instance);

    private TransferHandler Transfer => new(_accounts, _transactions, _unitOfWork, TimeProvider.System, TestTelemetry.Instance);

    private ReverseTransactionHandler Reverse => new(_accounts, _transactions, _unitOfWork, TimeProvider.System, TestTelemetry.Instance);

    private GetLedgerTransactionHandler GetTransaction => new(_transactions, TestTelemetry.Instance);

    public LedgerHandlerTests() => _accounts.Seed(LedgerScenario.Settlement(Currency.USD));

    private Account SeedWallet(Guid id, decimal funding = 0m, Currency currency = Currency.USD)
    {
        var settlement = _accounts.Find(LedgerScenario.SettlementId(currency))
            ?? throw new InvalidOperationException("Seed the settlement account first.");

        var wallet = LedgerScenario.FundedWallet(id, currency, funding, settlement);
        _accounts.Seed(wallet);

        return wallet;
    }

    // ---------------------------------------------------------------- deposit

    [Fact]
    public async Task A_deposit_credits_the_wallet_and_records_a_balanced_transaction()
    {
        SeedWallet(WalletId);

        var result = await Deposit.HandleAsync(
            new DepositCommand(TransactionId, WalletId, 100m, Currency.USD));

        Assert.Equal(TransactionId, result.TransactionId);
        Assert.Equal(LedgerTransactionKind.Deposit, result.Kind);
        Assert.Equal(0m, result.Entries.Sum(entry => entry.Amount));
        Assert.Equal(100m, _accounts.Find(WalletId)!.Balance.Amount);
    }

    // Everything a ledger use case does must be inside one transaction: the locks
    // it takes live only as long as that transaction, and the balance it writes
    // must land with the entries that justify it.
    [Fact]
    public async Task A_deposit_runs_inside_a_transaction_and_commits_once()
    {
        SeedWallet(WalletId);

        await Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.USD));

        Assert.Equal(1, _unitOfWork.TransactionCount);
        Assert.Equal(1, _unitOfWork.SaveCount);
    }

    // Locking has to happen before the balance is read, or the decision is made
    // on a value another request is free to change.
    [Fact]
    public async Task A_deposit_locks_both_accounts_before_writing()
    {
        SeedWallet(WalletId);

        await Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.USD));

        var locked = Assert.Single(_accounts.LockRequests);
        Assert.Contains(WalletId, locked);
        Assert.Contains(LedgerScenario.SettlementId(Currency.USD), locked);
        Assert.Equal(locked.OrderBy(id => id), locked);
    }

    [Fact]
    public async Task A_deposit_to_an_unknown_account_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.USD)));
    }

    // Without a settlement account there is no counterparty, and a deposit would
    // have to create money from nothing. Failing loudly is the only honest option.
    [Fact]
    public async Task A_deposit_in_a_currency_with_no_settlement_account_is_not_found()
    {
        SeedWallet(WalletId, currency: Currency.USD);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.EUR)));
    }

    // ------------------------------------------------------------- withdrawal

    [Fact]
    public async Task A_withdrawal_debits_the_wallet()
    {
        SeedWallet(WalletId, funding: 100m);

        await Withdraw.HandleAsync(new WithdrawCommand(TransactionId, WalletId, 40m, Currency.USD));

        Assert.Equal(60m, _accounts.Find(WalletId)!.Balance.Amount);
    }

    [Fact]
    public async Task A_withdrawal_beyond_the_balance_is_refused_and_nothing_is_committed()
    {
        SeedWallet(WalletId, funding: 10m);

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            Withdraw.HandleAsync(new WithdrawCommand(TransactionId, WalletId, 50m, Currency.USD)));

        Assert.Equal(0, _unitOfWork.SaveCount);
        Assert.Equal(10m, _accounts.Find(WalletId)!.Balance.Amount);
        Assert.Empty(_transactions.Transactions);
    }

    // --------------------------------------------------------------- transfer

    [Fact]
    public async Task A_transfer_moves_money_between_wallets()
    {
        SeedWallet(WalletId, funding: 100m);
        SeedWallet(OtherWalletId);

        var result = await Transfer.HandleAsync(
            new TransferCommand(TransactionId, WalletId, OtherWalletId, 30m, Currency.USD));

        Assert.Equal(70m, _accounts.Find(WalletId)!.Balance.Amount);
        Assert.Equal(30m, _accounts.Find(OtherWalletId)!.Balance.Amount);
        Assert.Equal(0m, result.Entries.Sum(entry => entry.Amount));
    }

    [Fact]
    public async Task A_transfer_locks_both_wallets_in_identifier_order()
    {
        SeedWallet(WalletId, funding: 100m);
        SeedWallet(OtherWalletId);

        await Transfer.HandleAsync(
            new TransferCommand(TransactionId, WalletId, OtherWalletId, 30m, Currency.USD));

        var locked = Assert.Single(_accounts.LockRequests);
        Assert.Equal(2, locked.Count);
        Assert.Equal(locked.OrderBy(id => id), locked);
    }

    [Fact]
    public async Task A_transfer_to_the_same_account_is_rejected_as_invalid()
    {
        SeedWallet(WalletId, funding: 100m);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Transfer.HandleAsync(
                new TransferCommand(TransactionId, WalletId, WalletId, 10m, Currency.USD)));

        Assert.Contains("DestinationAccountId", exception.Errors.Keys);
    }

    [Fact]
    public async Task A_transfer_between_currencies_is_refused()
    {
        SeedWallet(WalletId, funding: 100m);
        _accounts.Seed(Account.Open(OtherWalletId, Currency.EUR));

        await Assert.ThrowsAsync<CurrencyMismatchException>(() =>
            Transfer.HandleAsync(
                new TransferCommand(TransactionId, WalletId, OtherWalletId, 10m, Currency.USD)));
    }

    // -------------------------------------------------------------- validation

    [Fact]
    public async Task Every_invalid_field_is_reported_at_once()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Deposit.HandleAsync(new DepositCommand(Guid.Empty, Guid.Empty, -5m, Currency.USD)));

        Assert.Equal(3, exception.Errors.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_amount_is_rejected(int amount)
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, amount, Currency.USD)));

        Assert.Contains("Amount", exception.Errors.Keys);
    }

    // Sub-cent amounts are refused rather than rounded: rounding would silently
    // lose or invent a fraction of a cent, and hide the calculation that produced
    // it.
    [Fact]
    public async Task An_amount_more_precise_than_the_currency_allows_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 0.001m, Currency.USD)));

        Assert.Contains("Amount", exception.Errors.Keys);
    }

    [Fact]
    public async Task An_unknown_currency_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 10m, (Currency)9999)));

        Assert.Contains("Currency", exception.Errors.Keys);
    }

    // Each of these reaches a database column limit. Refused here they are a 400;
    // let through, they fail inside PostgreSQL and become a 500.
    [Fact]
    public async Task An_amount_larger_than_the_ledger_can_store_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 1_000_000_000_000_000m, Currency.USD)));

        Assert.Contains("Amount", exception.Errors.Keys);
        Assert.Empty(_accounts.LockRequests);
    }

    [Fact]
    public async Task An_idempotency_key_longer_than_the_ledger_stores_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Deposit.HandleAsync(new DepositCommand(
                TransactionId, WalletId, 10m, Currency.USD, IdempotencyKey: new string('k', 201))));

        Assert.Contains("IdempotencyKey", exception.Errors.Keys);
        Assert.Empty(_accounts.LockRequests);
    }

    [Fact]
    public async Task An_external_reference_longer_than_the_ledger_stores_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Transfer.HandleAsync(new TransferCommand(
                TransactionId, WalletId, OtherWalletId, 10m, Currency.USD, ExternalReference: new string('r', 201))));

        Assert.Contains("ExternalReference", exception.Errors.Keys);
    }

    [Fact]
    public async Task A_reversal_key_longer_than_the_ledger_stores_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Reverse.HandleAsync(new ReverseTransactionCommand(
                Guid.NewGuid(), TransactionId, IdempotencyKey: new string('k', 201))));

        Assert.Contains("IdempotencyKey", exception.Errors.Keys);
    }

    [Fact]
    public async Task A_key_of_exactly_the_maximum_length_is_accepted()
    {
        SeedWallet(WalletId);

        var result = await Deposit.HandleAsync(new DepositCommand(
            TransactionId, WalletId, 10m, Currency.USD, IdempotencyKey: new string('k', 200)));

        Assert.False(result.WasReplayed);
    }

    [Fact]
    public async Task Nothing_is_locked_or_committed_when_validation_fails()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Deposit.HandleAsync(new DepositCommand(Guid.Empty, WalletId, 10m, Currency.USD)));

        Assert.Empty(_accounts.LockRequests);
        Assert.Equal(0, _unitOfWork.TransactionCount);
    }

    // ------------------------------------------------------------ idempotency

    [Fact]
    public async Task Replaying_a_request_returns_the_original_result_without_repeating_it()
    {
        SeedWallet(WalletId);

        var first = await Deposit.HandleAsync(
            new DepositCommand(TransactionId, WalletId, 100m, Currency.USD, "key-1"));

        var second = await Deposit.HandleAsync(
            new DepositCommand(Guid.NewGuid(), WalletId, 100m, Currency.USD, "key-1"));

        Assert.False(first.WasReplayed);
        Assert.True(second.WasReplayed);
        Assert.Equal(first.TransactionId, second.TransactionId);

        // The decisive assertion: the money moved once, not twice.
        Assert.Equal(100m, _accounts.Find(WalletId)!.Balance.Amount);
        Assert.Single(_transactions.Transactions);
    }

    // A key reused for a different movement must not receive the original's
    // result: that would report success for an operation that never happened.
    [Fact]
    public async Task Reusing_a_key_for_a_different_amount_is_a_conflict()
    {
        SeedWallet(WalletId);

        await Deposit.HandleAsync(
            new DepositCommand(TransactionId, WalletId, 100m, Currency.USD, "key-1"));

        await Assert.ThrowsAsync<ConflictException>(() =>
            Deposit.HandleAsync(
                new DepositCommand(Guid.NewGuid(), WalletId, 250m, Currency.USD, "key-1")));
    }

    // Same key, same amount, different wallet. Replaying the first deposit here
    // would tell the second caller its money arrived when it did not.
    [Fact]
    public async Task Reusing_a_key_for_a_deposit_to_another_wallet_is_a_conflict()
    {
        SeedWallet(WalletId);
        SeedWallet(OtherWalletId);

        await Deposit.HandleAsync(
            new DepositCommand(TransactionId, WalletId, 100m, Currency.USD, "key-1"));

        await Assert.ThrowsAsync<ConflictException>(() =>
            Deposit.HandleAsync(
                new DepositCommand(Guid.NewGuid(), OtherWalletId, 100m, Currency.USD, "key-1")));

        Assert.Equal(0m, _accounts.Find(OtherWalletId)!.Balance.Amount);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_transfer_in_the_opposite_direction_is_a_conflict()
    {
        SeedWallet(WalletId, funding: 100m);
        SeedWallet(OtherWalletId, funding: 100m);

        await Transfer.HandleAsync(
            new TransferCommand(TransactionId, WalletId, OtherWalletId, 30m, Currency.USD, "key-1"));

        await Assert.ThrowsAsync<ConflictException>(() =>
            Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), OtherWalletId, WalletId, 30m, Currency.USD, "key-1")));

        Assert.Equal(130m, _accounts.Find(OtherWalletId)!.Balance.Amount);
    }

    [Fact]
    public async Task Requests_without_a_key_are_never_treated_as_replays()
    {
        SeedWallet(WalletId);

        await Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.USD));
        await Deposit.HandleAsync(new DepositCommand(Guid.NewGuid(), WalletId, 100m, Currency.USD));

        Assert.Equal(200m, _accounts.Find(WalletId)!.Balance.Amount);
        Assert.Equal(2, _transactions.Transactions.Count);
    }

    // --------------------------------------------------------------- reversal

    [Fact]
    public async Task A_reversal_undoes_the_original_movement()
    {
        SeedWallet(WalletId);
        await Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.USD));

        var reversal = await Reverse.HandleAsync(
            new ReverseTransactionCommand(Guid.NewGuid(), TransactionId));

        Assert.Equal(LedgerTransactionKind.Reversal, reversal.Kind);
        Assert.Equal(2, reversal.Entries.Count);
        Assert.Equal(0m, reversal.Entries.Sum(entry => entry.Amount));
        Assert.Equal(0m, _accounts.Find(WalletId)!.Balance.Amount);
    }

    [Fact]
    public async Task Reversing_the_same_transaction_twice_is_refused()
    {
        SeedWallet(WalletId);
        await Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.USD));
        await Reverse.HandleAsync(new ReverseTransactionCommand(Guid.NewGuid(), TransactionId));

        await Assert.ThrowsAsync<TransactionAlreadyReversedException>(() =>
            Reverse.HandleAsync(new ReverseTransactionCommand(Guid.NewGuid(), TransactionId)));
    }

    [Fact]
    public async Task Reversing_an_unknown_transaction_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Reverse.HandleAsync(new ReverseTransactionCommand(Guid.NewGuid(), TransactionId)));
    }

    [Fact]
    public async Task Replaying_a_reversal_returns_the_original_reversal()
    {
        SeedWallet(WalletId);
        await Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.USD));

        var first = await Reverse.HandleAsync(
            new ReverseTransactionCommand(Guid.NewGuid(), TransactionId, "reverse-1"));
        var second = await Reverse.HandleAsync(
            new ReverseTransactionCommand(Guid.NewGuid(), TransactionId, "reverse-1"));

        Assert.True(second.WasReplayed);
        Assert.Equal(first.TransactionId, second.TransactionId);
        Assert.Equal(0m, _accounts.Find(WalletId)!.Balance.Amount);
    }

    [Fact]
    public async Task Reusing_a_reversal_key_for_another_transaction_is_a_conflict()
    {
        SeedWallet(WalletId);
        var otherDeposit = Guid.NewGuid();
        await Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.USD));
        await Deposit.HandleAsync(new DepositCommand(otherDeposit, WalletId, 40m, Currency.USD));

        await Reverse.HandleAsync(new ReverseTransactionCommand(Guid.NewGuid(), TransactionId, "reverse-1"));

        await Assert.ThrowsAsync<ConflictException>(() =>
            Reverse.HandleAsync(new ReverseTransactionCommand(Guid.NewGuid(), otherDeposit, "reverse-1")));

        Assert.Equal(40m, _accounts.Find(WalletId)!.Balance.Amount);
    }

    // ------------------------------------------------------ reading a transaction

    [Fact]
    public async Task A_recorded_transaction_can_be_read_back()
    {
        SeedWallet(WalletId);
        var recorded = await Deposit.HandleAsync(new DepositCommand(TransactionId, WalletId, 100m, Currency.USD));

        var read = await GetTransaction.HandleAsync(new GetLedgerTransactionQuery(TransactionId));

        Assert.Equal(recorded.TransactionId, read.TransactionId);
        Assert.Equal(recorded.Entries, read.Entries);
        Assert.False(read.WasReplayed);
    }

    [Fact]
    public async Task Reading_an_unknown_transaction_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            GetTransaction.HandleAsync(new GetLedgerTransactionQuery(TransactionId)));
    }

    [Fact]
    public async Task Reading_a_transaction_requires_an_identifier()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            GetTransaction.HandleAsync(new GetLedgerTransactionQuery(Guid.Empty)));
    }
}
