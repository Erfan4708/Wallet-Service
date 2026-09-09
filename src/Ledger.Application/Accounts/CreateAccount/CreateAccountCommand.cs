using Ledger.Domain.Enums;

namespace Ledger.Application.Accounts.CreateAccount;

/// <summary>
/// A request to open a new account with a zero balance.
/// </summary>
/// <param name="AccountId">
/// The identifier the new account will have, chosen by the caller.
/// </param>
/// <param name="Currency">The currency the account will hold.</param>
/// <remarks>
/// The identifier is supplied rather than generated here for two reasons. It
/// keeps the use case deterministic, so a test can assert exactly what was
/// created; and it makes the operation naturally idempotent — a client that
/// retries a timed-out request sends the same identifier and gets a conflict
/// instead of a second account. Generating it internally would make a retry
/// indistinguishable from a new request.
/// </remarks>
public sealed record CreateAccountCommand(Guid AccountId, Currency Currency);
