using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Ledger.Domain.Events;
using Ledger.Domain.Exceptions;
using Ledger.Domain.ValueObjects;

namespace Ledger.Domain.Tests.Entities;

public class LedgerTransactionTests
{
    private static readonly Guid TransactionId = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid WalletId = new("22222222-0000-0000-0000-000000000001");
    private static readonly Guid OtherWalletId = new("22222222-0000-0000-0000-000000000002");
    private static readonly Guid SettlementId = new("33333333-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset OccurredAt = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static Money Usd(decimal amount) => new(amount, Currency.USD);

    private static Account Wallet(Guid id, Currency currency = Currency.USD) => Account.Open(id, currency);

    private static Account Settlement(Currency currency = Currency.USD) =>
        Account.OpenSystem(SettlementId, currency, SystemAccountKeys.Settlement(currency));

    private static Account FundedWallet(decimal amount, Guid? id = null)
    {
        var wallet = Wallet(id ?? WalletId);
        LedgerTransaction.Deposit(Guid.NewGuid(), wallet, Settlement(), Usd(amount), OccurredAt);

        return wallet;
    }

    // ---------------------------------------------------------------- deposit

    [Fact]
    public void A_deposit_credits_the_wallet_and_debits_settlement()
    {
        var wallet = Wallet(WalletId);
        var settlement = Settlement();

        var transaction = LedgerTransaction.Deposit(
            TransactionId, wallet, settlement, Usd(100m), OccurredAt);

        Assert.Equal(Usd(100m), wallet.Balance);
        Assert.Equal(Usd(-100m), settlement.Balance);
        Assert.Equal(LedgerTransactionKind.Deposit, transaction.Kind);
    }

    // The invariant the whole system exists to guarantee. If a deposit only
    // credited the wallet, money would appear from nowhere and this could never
    // be asserted.
    [Fact]
    public void A_deposit_balances_to_zero()
    {
        var transaction = LedgerTransaction.Deposit(
            TransactionId, Wallet(WalletId), Settlement(), Usd(100m), OccurredAt);

        Assert.True(transaction.Total.IsZero);
        Assert.Equal(2, transaction.Entries.Count);
    }

    [Fact]
    public void A_deposit_records_one_entry_per_account_with_opposite_signs()
    {
        var transaction = LedgerTransaction.Deposit(
            TransactionId, Wallet(WalletId), Settlement(), Usd(100m), OccurredAt);

        var wallet = transaction.Entries.Single(entry => entry.AccountId == WalletId);
        var settlement = transaction.Entries.Single(entry => entry.AccountId == SettlementId);

        Assert.Equal(Usd(100m), wallet.Amount);
        Assert.Equal(Usd(-100m), settlement.Amount);
        Assert.Equal([0, 1], transaction.Entries.Select(entry => entry.EntryIndex));
    }

    [Fact]
    public void A_deposit_must_settle_against_a_system_account()
    {
        var exception = Assert.Throws<AccountTypeMismatchException>(() =>
            LedgerTransaction.Deposit(
                TransactionId, Wallet(WalletId), Wallet(OtherWalletId), Usd(100m), OccurredAt));

        Assert.Equal(AccountType.System, exception.Expected);
    }

    [Fact]
    public void A_deposit_into_a_system_account_is_refused()
    {
        Assert.Throws<AccountTypeMismatchException>(() =>
            LedgerTransaction.Deposit(
                TransactionId, Settlement(), Settlement(), Usd(100m), OccurredAt));
    }

    // ------------------------------------------------------------- withdrawal

    [Fact]
    public void A_withdrawal_debits_the_wallet_and_credits_settlement()
    {
        var wallet = FundedWallet(100m);
        var settlement = Settlement();

        LedgerTransaction.Withdraw(TransactionId, wallet, settlement, Usd(40m), OccurredAt);

        Assert.Equal(Usd(60m), wallet.Balance);
        Assert.Equal(Usd(40m), settlement.Balance);
    }

    [Fact]
    public void A_withdrawal_beyond_the_balance_is_refused()
    {
        var wallet = FundedWallet(100m);

        Assert.Throws<InsufficientFundsException>(() =>
            LedgerTransaction.Withdraw(TransactionId, wallet, Settlement(), Usd(100.01m), OccurredAt));
    }

    // A rejected operation must not apply partially. The wallet is debited first,
    // so a failure here is exactly where a half-applied transaction would show up.
    [Fact]
    public void A_refused_withdrawal_leaves_both_balances_untouched()
    {
        var wallet = FundedWallet(100m);
        var settlement = Settlement();

        Assert.Throws<InsufficientFundsException>(() =>
            LedgerTransaction.Withdraw(TransactionId, wallet, settlement, Usd(500m), OccurredAt));

        Assert.Equal(Usd(100m), wallet.Balance);
        Assert.True(settlement.Balance.IsZero);
    }

    [Fact]
    public void A_wallet_can_be_emptied_exactly()
    {
        var wallet = FundedWallet(100m);

        LedgerTransaction.Withdraw(TransactionId, wallet, Settlement(), Usd(100m), OccurredAt);

        Assert.True(wallet.Balance.IsZero);
    }

    // --------------------------------------------------------------- transfer

    [Fact]
    public void A_transfer_moves_money_between_wallets_and_balances()
    {
        var source = FundedWallet(100m);
        var destination = Wallet(OtherWalletId);

        var transaction = LedgerTransaction.Transfer(
            TransactionId, source, destination, Usd(30m), OccurredAt);

        Assert.Equal(Usd(70m), source.Balance);
        Assert.Equal(Usd(30m), destination.Balance);
        Assert.True(transaction.Total.IsZero);
    }

    [Fact]
    public void A_transfer_to_the_same_account_is_refused()
    {
        var wallet = FundedWallet(100m);

        Assert.Throws<SameAccountTransferException>(() =>
            LedgerTransaction.Transfer(TransactionId, wallet, wallet, Usd(10m), OccurredAt));
    }

    [Fact]
    public void A_transfer_beyond_the_source_balance_is_refused()
    {
        var source = FundedWallet(10m);

        Assert.Throws<InsufficientFundsException>(() =>
            LedgerTransaction.Transfer(
                TransactionId, source, Wallet(OtherWalletId), Usd(10.01m), OccurredAt));
    }

    [Fact]
    public void A_transfer_involving_a_system_account_is_refused()
    {
        var source = FundedWallet(100m);

        Assert.Throws<AccountTypeMismatchException>(() =>
            LedgerTransaction.Transfer(TransactionId, source, Settlement(), Usd(10m), OccurredAt));
    }

    // -------------------------------------------------------------- currency

    [Fact]
    public void A_transfer_between_different_currencies_is_refused()
    {
        var source = FundedWallet(100m);
        var destination = Wallet(OtherWalletId, Currency.EUR);

        Assert.Throws<CurrencyMismatchException>(() =>
            LedgerTransaction.Transfer(TransactionId, source, destination, Usd(10m), OccurredAt));
    }

    [Fact]
    public void A_deposit_in_the_wrong_currency_is_refused()
    {
        Assert.Throws<CurrencyMismatchException>(() =>
            LedgerTransaction.Deposit(
                TransactionId, Wallet(WalletId), Settlement(), new Money(10m, Currency.EUR), OccurredAt));
    }

    // The currency is checked before anything is posted, so a mismatch cannot
    // leave one leg applied and the other not.
    [Fact]
    public void A_currency_mismatch_leaves_no_balance_changed()
    {
        var source = FundedWallet(100m);
        var destination = Wallet(OtherWalletId, Currency.EUR);

        Assert.Throws<CurrencyMismatchException>(() =>
            LedgerTransaction.Transfer(TransactionId, source, destination, Usd(10m), OccurredAt));

        Assert.Equal(Usd(100m), source.Balance);
        Assert.True(destination.Balance.IsZero);
    }

    // ---------------------------------------------------------------- amounts

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_amount_is_refused(int amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LedgerTransaction.Deposit(
                TransactionId, Wallet(WalletId), Settlement(), Usd(amount), OccurredAt));
    }

    [Fact]
    public void A_transaction_requires_an_identifier()
    {
        Assert.Throws<ArgumentException>(() =>
            LedgerTransaction.Deposit(
                Guid.Empty, Wallet(WalletId), Settlement(), Usd(10m), OccurredAt));
    }

    // --------------------------------------------------------------- reversal

    [Fact]
    public void A_reversal_negates_every_entry_of_the_original()
    {
        var wallet = Wallet(WalletId);
        var settlement = Settlement();
        var original = LedgerTransaction.Deposit(
            TransactionId, wallet, settlement, Usd(100m), OccurredAt);

        var accounts = new Dictionary<Guid, Account> { [WalletId] = wallet, [SettlementId] = settlement };
        var reversal = LedgerTransaction.Reverse(
            Guid.NewGuid(), original, accounts, OccurredAt.AddMinutes(5));

        Assert.Equal(LedgerTransactionKind.Reversal, reversal.Kind);
        Assert.Equal(original.Id, reversal.ReversesTransactionId);
        Assert.True(reversal.Total.IsZero);
    }

    [Fact]
    public void A_reversal_returns_the_accounts_to_where_they_started()
    {
        var wallet = Wallet(WalletId);
        var settlement = Settlement();
        var original = LedgerTransaction.Deposit(
            TransactionId, wallet, settlement, Usd(100m), OccurredAt);

        var accounts = new Dictionary<Guid, Account> { [WalletId] = wallet, [SettlementId] = settlement };
        LedgerTransaction.Reverse(Guid.NewGuid(), original, accounts, OccurredAt);

        Assert.True(wallet.Balance.IsZero);
        Assert.True(settlement.Balance.IsZero);
    }

    // Reversing a reversal would restore a position already judged wrong. The
    // remedy for a bad correction is another transaction, not an undo of an undo.
    [Fact]
    public void A_reversal_cannot_itself_be_reversed()
    {
        var wallet = Wallet(WalletId);
        var settlement = Settlement();
        var original = LedgerTransaction.Deposit(TransactionId, wallet, settlement, Usd(100m), OccurredAt);

        var accounts = new Dictionary<Guid, Account> { [WalletId] = wallet, [SettlementId] = settlement };
        var reversal = LedgerTransaction.Reverse(Guid.NewGuid(), original, accounts, OccurredAt);

        var refused = Assert.Throws<TransactionAlreadyReversedException>(() =>
            LedgerTransaction.Reverse(Guid.NewGuid(), reversal, accounts, OccurredAt));

        // Nothing reversed the reversal, so the refusal must not say something did.
        Assert.Equal(reversal.Id, refused.TransactionId);
        Assert.Equal($"Transaction {reversal.Id} is itself a reversal and cannot be reversed.", refused.Message);
    }

    [Fact]
    public void A_reversal_needs_every_account_the_original_touched()
    {
        var wallet = Wallet(WalletId);
        var settlement = Settlement();
        var original = LedgerTransaction.Deposit(TransactionId, wallet, settlement, Usd(100m), OccurredAt);

        var incomplete = new Dictionary<Guid, Account> { [WalletId] = wallet };

        Assert.Throws<ArgumentException>(() =>
            LedgerTransaction.Reverse(Guid.NewGuid(), original, incomplete, OccurredAt));
    }

    [Fact]
    public void Reversing_a_withdrawal_restores_the_funds()
    {
        var wallet = FundedWallet(100m);
        var settlement = Settlement();
        var withdrawal = LedgerTransaction.Withdraw(
            TransactionId, wallet, settlement, Usd(40m), OccurredAt);

        var accounts = new Dictionary<Guid, Account> { [WalletId] = wallet, [SettlementId] = settlement };
        LedgerTransaction.Reverse(Guid.NewGuid(), withdrawal, accounts, OccurredAt);

        Assert.Equal(Usd(100m), wallet.Balance);
    }

    // ----------------------------------------------------------------- events

    [Fact]
    public void Recording_a_transaction_raises_a_domain_event()
    {
        var transaction = LedgerTransaction.Deposit(
            TransactionId, Wallet(WalletId), Settlement(), Usd(100m), OccurredAt);

        var raised = Assert.Single(transaction.DomainEvents);
        var recorded = Assert.IsType<LedgerTransactionRecorded>(raised);

        Assert.Equal(TransactionId, recorded.TransactionId);
        Assert.Equal(LedgerTransactionKind.Deposit, recorded.Kind);
        Assert.Equal(OccurredAt, recorded.OccurredAt);
    }

    [Fact]
    public void Events_can_be_drained_once_they_have_been_handed_on()
    {
        var transaction = LedgerTransaction.Deposit(
            TransactionId, Wallet(WalletId), Settlement(), Usd(100m), OccurredAt);

        transaction.ClearDomainEvents();

        Assert.Empty(transaction.DomainEvents);
    }

    // ------------------------------------------------------------ idempotency

    [Fact]
    public void A_transaction_recognises_a_replay_of_the_same_movement()
    {
        var transaction = LedgerTransaction.Deposit(
            TransactionId, Wallet(WalletId), Settlement(), Usd(100m), OccurredAt, "key-1");

        Assert.True(transaction.Matches(LedgerTransactionKind.Deposit, [(WalletId, Usd(100m))]));
    }

    [Theory]
    [InlineData(LedgerTransactionKind.Withdrawal, 100)]
    [InlineData(LedgerTransactionKind.Deposit, 99)]
    public void A_transaction_rejects_a_replay_describing_something_else(
        LedgerTransactionKind kind, int amount)
    {
        var transaction = LedgerTransaction.Deposit(
            TransactionId, Wallet(WalletId), Settlement(), Usd(100m), OccurredAt, "key-1");

        Assert.False(transaction.Matches(kind, [(WalletId, Usd(amount))]));
    }

    // The same amount into a different wallet is a different operation. Treating
    // it as a replay would tell the second caller its deposit succeeded when no
    // money moved into its wallet at all.
    [Fact]
    public void A_transaction_rejects_a_replay_on_a_different_account()
    {
        var transaction = LedgerTransaction.Deposit(
            TransactionId, Wallet(WalletId), Settlement(), Usd(100m), OccurredAt, "key-1");

        Assert.False(transaction.Matches(LedgerTransactionKind.Deposit, [(OtherWalletId, Usd(100m))]));
    }

    [Fact]
    public void A_transfer_replay_must_name_both_wallets_in_the_same_direction()
    {
        var source = FundedWallet(100m);
        var destination = Wallet(OtherWalletId);

        var transaction = LedgerTransaction.Transfer(
            TransactionId, source, destination, Usd(30m), OccurredAt, "key-1");

        Assert.True(transaction.Matches(
            LedgerTransactionKind.Transfer, [(WalletId, Usd(-30m)), (OtherWalletId, Usd(30m))]));

        Assert.False(transaction.Matches(
            LedgerTransactionKind.Transfer, [(OtherWalletId, Usd(-30m)), (WalletId, Usd(30m))]));
    }

    [Fact]
    public void A_blank_idempotency_key_is_stored_as_absent()
    {
        var transaction = LedgerTransaction.Deposit(
            TransactionId, Wallet(WalletId), Settlement(), Usd(100m), OccurredAt, "   ");

        Assert.Null(transaction.IdempotencyKey);
    }
}
