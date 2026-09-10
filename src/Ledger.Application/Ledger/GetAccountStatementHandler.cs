using Ledger.Application.Abstractions;
using Ledger.Application.Observability;
using Ledger.Application.Exceptions;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;

namespace Ledger.Application.Ledger;

/// <summary>A request for an account's recent ledger entries.</summary>
public sealed record GetAccountStatementQuery(Guid AccountId, int Limit = 50);

/// <summary>An account's balance together with the entries that produced it.</summary>
public sealed record AccountStatement(
    Guid AccountId,
    Currency Currency,
    decimal Balance,
    IReadOnlyList<AccountStatementEntry> Entries);

/// <summary>One movement on an account.</summary>
public sealed record AccountStatementEntry(
    long EntryId,
    Guid TransactionId,
    decimal Amount,
    Currency Currency);

/// <summary>
/// Answers "why does this account hold what it holds".
/// </summary>
/// <remarks>
/// This is the auditability requirement made concrete: the balance and the
/// movements behind it come from the same query, so a discrepancy is visible
/// rather than inferred.
/// </remarks>
public sealed class GetAccountStatementHandler
{
    private const int MaximumLimit = 500;

    private readonly IAccountRepository _accounts;
    private readonly ILedgerTransactionRepository _transactions;

    private readonly LedgerTelemetry _telemetry;

    public GetAccountStatementHandler(
        IAccountRepository accounts,
        ILedgerTransactionRepository transactions,
        LedgerTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(telemetry);

        _accounts = accounts;
        _transactions = transactions;
        _telemetry = telemetry;
    }

    public async Task<AccountStatement> HandleAsync(
        GetAccountStatementQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var activity = _telemetry.StartActivity("ledger.account.statement");
        activity?.SetTag("ledger.account_id", query.AccountId);

        var errors = new Dictionary<string, string[]>();
        LedgerCommandValidation.RequireIdentifier(query.AccountId, nameof(query.AccountId), errors);

        if (query.Limit is < 1 or > MaximumLimit)
        {
            errors[nameof(query.Limit)] = [$"The limit must be between 1 and {MaximumLimit}."];
        }

        LedgerCommandValidation.ThrowIfInvalid(errors);

        var account = await _accounts.GetByIdAsync(query.AccountId, cancellationToken)
            ?? throw new NotFoundException(nameof(Account), query.AccountId);

        var entries = await _transactions.GetEntriesForAccountAsync(account.Id, query.Limit, cancellationToken);

        return new AccountStatement(
            account.Id,
            account.Currency,
            account.Balance.Amount,
            entries
                .Select(entry => new AccountStatementEntry(
                    entry.Id, entry.TransactionId, entry.Amount.Amount, entry.Amount.Currency))
                .ToList());
    }
}
