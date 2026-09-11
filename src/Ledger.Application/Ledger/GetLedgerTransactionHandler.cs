using Ledger.Application.Abstractions;
using Ledger.Application.Exceptions;
using Ledger.Application.Observability;
using Ledger.Domain.Entities;

namespace Ledger.Application.Ledger;

/// <summary>A request for one recorded ledger transaction.</summary>
public sealed record GetLedgerTransactionQuery(Guid TransactionId);

/// <summary>
/// Reads a recorded transaction and its entries.
/// </summary>
/// <remarks>
/// The resource every money movement points to: a successful deposit, withdrawal,
/// transfer or reversal answers 201 with a <c>Location</c> naming its
/// transaction, and this is what that location returns. A read takes no locks and
/// no unit of work.
/// </remarks>
public sealed class GetLedgerTransactionHandler
{
    private readonly ILedgerTransactionRepository _transactions;
    private readonly LedgerTelemetry _telemetry;

    public GetLedgerTransactionHandler(ILedgerTransactionRepository transactions, LedgerTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(telemetry);

        _transactions = transactions;
        _telemetry = telemetry;
    }

    /// <exception cref="ValidationException">The query is malformed.</exception>
    /// <exception cref="NotFoundException">No such transaction exists.</exception>
    public async Task<LedgerTransactionResult> HandleAsync(
        GetLedgerTransactionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var activity = _telemetry.StartActivity("ledger.transaction.read");
        activity?.SetTag("ledger.transaction_id", query.TransactionId);

        if (query.TransactionId == Guid.Empty)
        {
            throw new ValidationException(nameof(query.TransactionId), "A transaction identifier is required.");
        }

        var transaction = await _transactions.GetByIdAsync(query.TransactionId, cancellationToken)
            ?? throw new NotFoundException(nameof(LedgerTransaction), query.TransactionId);

        return LedgerTransactionResult.From(transaction, wasReplayed: false);
    }
}
