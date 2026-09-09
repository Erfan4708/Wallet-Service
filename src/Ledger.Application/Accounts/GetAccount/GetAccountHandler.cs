using Ledger.Application.Abstractions;
using Ledger.Application.Exceptions;
using Ledger.Domain.Entities;

namespace Ledger.Application.Accounts.GetAccount;

/// <summary>
/// Reads one account.
/// </summary>
/// <remarks>
/// A read does not change anything, so it takes no unit of work and never
/// commits. Keeping that visible in the constructor is the point: a reader that
/// cannot reach a commit cannot accidentally perform one.
/// </remarks>
public sealed class GetAccountHandler
{
    private readonly IAccountRepository _accounts;

    public GetAccountHandler(IAccountRepository accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        _accounts = accounts;
    }

    /// <exception cref="ValidationException">The query is malformed.</exception>
    /// <exception cref="NotFoundException">No such account exists.</exception>
    public async Task<AccountSummary> HandleAsync(
        GetAccountQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.AccountId == Guid.Empty)
        {
            throw new ValidationException(nameof(query.AccountId), "An account identifier is required.");
        }

        var account = await _accounts.GetByIdAsync(query.AccountId, cancellationToken)
            ?? throw new NotFoundException(nameof(Account), query.AccountId);

        return AccountSummary.From(account);
    }
}
