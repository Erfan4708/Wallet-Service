using Ledger.Domain.Entities;
using Ledger.Domain.Enums;

namespace Ledger.Application.Accounts;

/// <summary>
/// What a caller is told about an account.
/// </summary>
/// <remarks>
/// The use cases return this rather than the <see cref="Account"/> entity
/// itself. Returning the entity would put the domain model on the wire, which
/// means a serializer's needs start dictating the shape of the domain, and any
/// property added for internal reasons silently becomes part of the public API.
/// A separate read model keeps those two rates of change apart.
/// </remarks>
public sealed record AccountSummary(Guid Id, Currency Currency, decimal Balance)
{
    internal static AccountSummary From(Account account) =>
        new(account.Id, account.Currency, account.Balance.Amount);
}
