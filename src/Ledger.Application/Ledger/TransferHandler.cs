using Ledger.Application.Abstractions;
using Ledger.Application.Observability;
using Ledger.Application.Exceptions;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;

namespace Ledger.Application.Ledger;

/// <summary>A request to move money between two wallets.</summary>
public sealed record TransferCommand(
    Guid TransactionId,
    Guid SourceAccountId,
    Guid DestinationAccountId,
    decimal Amount,
    Currency Currency,
    string? IdempotencyKey = null,
    string? ExternalReference = null,
    DateTimeOffset? OccurredAt = null);

/// <summary>
/// Moves money from one wallet to another as a single balanced transaction.
/// </summary>
public sealed class TransferHandler
{
    private readonly IAccountRepository _accounts;
    private readonly ILedgerTransactionRepository _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly LedgerTelemetry _telemetry;

    public TransferHandler(
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
        TransferCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // One span and one measurement per operation, wrapped around the whole
        // use case so that validation failures are counted too -- a request the
        // system refused is still a request it handled.
        return await _telemetry.TrackTransactionAsync(
            LedgerTransactionKind.Transfer,
            command.Currency,
            activity =>
            {
                activity?.SetTag("ledger.source_account_id", command.SourceAccountId);
                activity?.SetTag("ledger.destination_account_id", command.DestinationAccountId);
                activity?.SetTag("ledger.transaction_id", command.TransactionId);

                return ExecuteAsync(command, cancellationToken);
            });
    }

    private async Task<LedgerTransactionResult> ExecuteAsync(
        TransferCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        LedgerCommandValidation.RequireIdentifier(command.TransactionId, nameof(command.TransactionId), errors);
        LedgerCommandValidation.RequireIdentifier(command.SourceAccountId, nameof(command.SourceAccountId), errors);
        LedgerCommandValidation.RequireIdentifier(command.DestinationAccountId, nameof(command.DestinationAccountId), errors);

        if (command.SourceAccountId != Guid.Empty && command.SourceAccountId == command.DestinationAccountId)
        {
            errors[nameof(command.DestinationAccountId)] = ["An account cannot transfer to itself."];
        }

        var amount = LedgerCommandValidation.BuildAmount(command.Amount, command.Currency, errors);
        LedgerCommandValidation.ThrowIfInvalid(errors);

        var occurredAt = command.OccurredAt ?? _timeProvider.GetUtcNow();

        return await _unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            // Both wallets locked together, sorted by identifier inside the
            // repository. Locking in argument order would let a transfer A to B
            // and a transfer B to A deadlock against each other.
            var locked = await _accounts.GetForUpdateAsync(
                [command.SourceAccountId, command.DestinationAccountId], token);

            var source = LedgerAccounts.Require(locked, command.SourceAccountId);
            var destination = LedgerAccounts.Require(locked, command.DestinationAccountId);

            var replayed = await IdempotentReplay.FindAsync(
                _transactions, command.IdempotencyKey, LedgerTransactionKind.Transfer, amount, token);
            if (replayed is not null)
            {
                return replayed;
            }

            var transaction = LedgerTransaction.Transfer(
                command.TransactionId, source, destination, amount,
                occurredAt, command.IdempotencyKey, command.ExternalReference);

            await _transactions.AddAsync(transaction, token);
            await _unitOfWork.SaveChangesAsync(token);

            return LedgerTransactionResult.From(transaction, wasReplayed: false);
        }, cancellationToken);
    }
}

/// <summary>A request to undo an earlier transaction.</summary>
public sealed record ReverseTransactionCommand(
    Guid TransactionId,
    Guid OriginalTransactionId,
    string? IdempotencyKey = null,
    DateTimeOffset? OccurredAt = null);

/// <summary>
/// Records the exact negation of an earlier transaction.
/// </summary>
/// <remarks>
/// Corrections never edit or delete history. The original entries stay exactly as
/// written and a second, opposite transaction is appended, so the reason an
/// account holds what it holds remains reconstructible from the ledger alone.
/// </remarks>
public sealed class ReverseTransactionHandler
{
    private readonly IAccountRepository _accounts;
    private readonly ILedgerTransactionRepository _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly LedgerTelemetry _telemetry;

    public ReverseTransactionHandler(
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
        ReverseTransactionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // One span and one measurement per operation, wrapped around the whole
        // use case so that validation failures are counted too -- a request the
        // system refused is still a request it handled.
        return await _telemetry.TrackTransactionAsync(
            LedgerTransactionKind.Reversal,
            knownCurrency: null,
            activity =>
            {
                activity?.SetTag("ledger.original_transaction_id", command.OriginalTransactionId);
                activity?.SetTag("ledger.transaction_id", command.TransactionId);

                return ExecuteAsync(command, cancellationToken);
            });
    }

    private async Task<LedgerTransactionResult> ExecuteAsync(
        ReverseTransactionCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        LedgerCommandValidation.RequireIdentifier(command.TransactionId, nameof(command.TransactionId), errors);
        LedgerCommandValidation.RequireIdentifier(command.OriginalTransactionId, nameof(command.OriginalTransactionId), errors);
        LedgerCommandValidation.ThrowIfInvalid(errors);

        var occurredAt = command.OccurredAt ?? _timeProvider.GetUtcNow();

        return await _unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var original = await _transactions.GetByIdAsync(command.OriginalTransactionId, token)
                ?? throw new NotFoundException(nameof(LedgerTransaction), command.OriginalTransactionId);

            // Every account the original touched, locked in identifier order.
            var accountIds = original.Entries.Select(entry => entry.AccountId).Distinct().ToList();
            var locked = await _accounts.GetForUpdateAsync(accountIds, token);
            var byId = locked.ToDictionary(account => account.Id);

            if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
            {
                var existing = await _transactions.FindByIdempotencyKeyAsync(command.IdempotencyKey, token);
                if (existing is not null)
                {
                    return LedgerTransactionResult.From(existing, wasReplayed: true);
                }
            }

            // Checked after the locks so that two concurrent reversals of the
            // same transaction cannot both pass. The unique constraint on the
            // reversed-transaction column is the ultimate authority.
            if (await _transactions.HasReversalAsync(original.Id, token))
            {
                throw new Domain.Exceptions.TransactionAlreadyReversedException(original.Id);
            }

            var reversal = LedgerTransaction.Reverse(
                command.TransactionId, original, byId, occurredAt, command.IdempotencyKey);

            await _transactions.AddAsync(reversal, token);
            await _unitOfWork.SaveChangesAsync(token);

            return LedgerTransactionResult.From(reversal, wasReplayed: false);
        }, cancellationToken);
    }
}
