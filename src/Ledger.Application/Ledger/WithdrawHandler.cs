using Ledger.Application.Abstractions;
using Ledger.Application.Observability;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;

namespace Ledger.Application.Ledger;

/// <summary>A request to take money out of the system.</summary>
public sealed record WithdrawCommand(
    Guid TransactionId,
    Guid AccountId,
    decimal Amount,
    Currency Currency,
    string? IdempotencyKey = null,
    string? ExternalReference = null,
    DateTimeOffset? OccurredAt = null);

/// <summary>
/// Debits a wallet, crediting the settlement account by the same amount.
/// </summary>
/// <remarks>
/// The mirror of a deposit, with one added precondition: the wallet must hold
/// the funds. That check is only sound because the account row was locked before
/// its balance was read — see <see cref="IAccountRepository.GetForUpdateAsync"/>.
/// </remarks>
public sealed class WithdrawHandler
{
    private readonly IAccountRepository _accounts;
    private readonly ILedgerTransactionRepository _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly LedgerTelemetry _telemetry;

    public WithdrawHandler(
        IAccountRepository accounts,
        ILedgerTransactionRepository transactions,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        LedgerTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(telemetry);

        _accounts = accounts;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _telemetry = telemetry;
    }

    public async Task<LedgerTransactionResult> HandleAsync(
        WithdrawCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // One span and one measurement per operation, wrapped around the whole
        // use case so that validation failures are counted too -- a request the
        // system refused is still a request it handled.
        return await _telemetry.TrackTransactionAsync(
            LedgerTransactionKind.Withdrawal,
            command.Currency,
            activity =>
            {
                activity?.SetTag("ledger.account_id", command.AccountId);
                activity?.SetTag("ledger.transaction_id", command.TransactionId);

                return ExecuteAsync(command, cancellationToken);
            });
    }

    private async Task<LedgerTransactionResult> ExecuteAsync(
        WithdrawCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        LedgerCommandValidation.RequireIdentifier(command.TransactionId, nameof(command.TransactionId), errors);
        LedgerCommandValidation.RequireIdentifier(command.AccountId, nameof(command.AccountId), errors);
        var amount = LedgerCommandValidation.BuildAmount(command.Amount, command.Currency, errors);
        LedgerCommandValidation.ThrowIfInvalid(errors);

        var occurredAt = command.OccurredAt ?? _timeProvider.GetUtcNow();

        return await _unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var settlement = await LedgerAccounts.RequireSettlementAsync(_accounts, command.Currency, token);

            var locked = await _accounts.GetForUpdateAsync([command.AccountId, settlement.Id], token);
            var wallet = LedgerAccounts.Require(locked, command.AccountId);
            var settlementLocked = LedgerAccounts.Require(locked, settlement.Id);

            var replayed = await IdempotentReplay.FindAsync(
                _transactions, command.IdempotencyKey, LedgerTransactionKind.Withdrawal, amount, token);
            if (replayed is not null)
            {
                return replayed;
            }

            var transaction = LedgerTransaction.Withdraw(
                command.TransactionId, wallet, settlementLocked, amount,
                occurredAt, command.IdempotencyKey, command.ExternalReference);

            await _transactions.AddAsync(transaction, token);
            await _unitOfWork.SaveChangesAsync(token);

            return LedgerTransactionResult.From(transaction, wasReplayed: false);
        }, cancellationToken);
    }
}
