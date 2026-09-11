using Ledger.Application.Abstractions;
using Ledger.Application.Observability;
using Ledger.Application.Exceptions;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;

namespace Ledger.Application.Ledger;

/// <summary>A request to bring money into the system.</summary>
/// <param name="OccurredAt">
/// When the movement happened in the business's world. Defaults to now. Not
/// exposed at the HTTP boundary: letting a client backdate an entry is a fraud
/// vector, so it stays an internal capability for corrections.
/// </param>
public sealed record DepositCommand(
    Guid TransactionId,
    Guid AccountId,
    decimal Amount,
    Currency Currency,
    string? IdempotencyKey = null,
    string? ExternalReference = null,
    DateTimeOffset? OccurredAt = null);

/// <summary>
/// Credits a wallet, debiting the settlement account by the same amount.
/// </summary>
public sealed class DepositHandler
{
    private readonly IAccountRepository _accounts;
    private readonly ILedgerTransactionRepository _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly LedgerTelemetry _telemetry;

    public DepositHandler(
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
        DepositCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // One span and one measurement per operation, wrapped around the whole
        // use case so that validation failures are counted too -- a request the
        // system refused is still a request it handled.
        return await _telemetry.TrackTransactionAsync(
            LedgerTransactionKind.Deposit,
            command.Currency,
            activity =>
            {
                activity?.SetTag("ledger.account_id", command.AccountId);
                activity?.SetTag("ledger.transaction_id", command.TransactionId);

                return ExecuteAsync(command, cancellationToken);
            });
    }

    private async Task<LedgerTransactionResult> ExecuteAsync(
        DepositCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        LedgerCommandValidation.RequireIdentifier(command.TransactionId, nameof(command.TransactionId), errors);
        LedgerCommandValidation.RequireIdentifier(command.AccountId, nameof(command.AccountId), errors);
        var amount = LedgerCommandValidation.BuildAmount(command.Amount, command.Currency, errors);
        LedgerCommandValidation.RequireMaximumLength(command.IdempotencyKey, nameof(command.IdempotencyKey), errors);
        LedgerCommandValidation.RequireMaximumLength(command.ExternalReference, nameof(command.ExternalReference), errors);
        LedgerCommandValidation.ThrowIfInvalid(errors);

        var occurredAt = command.OccurredAt ?? _timeProvider.GetUtcNow();

        return await _unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var settlement = await LedgerAccounts.RequireSettlementAsync(_accounts, command.Currency, token);

            // Locked together, in identifier order, before anything is read.
            var locked = await _accounts.GetForUpdateAsync([command.AccountId, settlement.Id], token);
            var wallet = LedgerAccounts.Require(locked, command.AccountId);
            var settlementLocked = LedgerAccounts.Require(locked, settlement.Id);

            var replayed = await IdempotentReplay.FindAsync(
                _transactions, command.IdempotencyKey, LedgerTransactionKind.Deposit, [(wallet.Id, amount)], token);
            if (replayed is not null)
            {
                return replayed;
            }

            var transaction = LedgerTransaction.Deposit(
                command.TransactionId, wallet, settlementLocked, amount,
                occurredAt, command.IdempotencyKey, command.ExternalReference);

            await _transactions.AddAsync(transaction, token);
            await _unitOfWork.SaveChangesAsync(token);

            return LedgerTransactionResult.From(transaction, wasReplayed: false);
        }, cancellationToken);
    }
}

/// <summary>Shared account lookups for the ledger use cases.</summary>
internal static class LedgerAccounts
{
    internal static async Task<Account> RequireSettlementAsync(
        IAccountRepository accounts,
        Currency currency,
        CancellationToken cancellationToken)
    {
        var key = SystemAccountKeys.Settlement(currency);

        return await accounts.FindBySystemKeyAsync(key, cancellationToken)
            ?? throw new NotFoundException("System account", key);
    }

    internal static Account Require(IReadOnlyList<Account> locked, Guid id) =>
        locked.FirstOrDefault(account => account.Id == id)
            ?? throw new NotFoundException(nameof(Account), id);
}
