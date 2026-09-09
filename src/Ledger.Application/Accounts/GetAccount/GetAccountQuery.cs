namespace Ledger.Application.Accounts.GetAccount;

/// <summary>
/// A request for the current state of one account.
/// </summary>
public sealed record GetAccountQuery(Guid AccountId);
