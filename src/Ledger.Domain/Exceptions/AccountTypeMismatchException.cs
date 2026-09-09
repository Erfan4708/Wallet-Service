using Ledger.Domain.Enums;

namespace Ledger.Domain.Exceptions;

/// <summary>
/// Thrown when an operation is given an account of the wrong kind — a transfer
/// between system accounts, or a deposit settling against a wallet.
/// </summary>
public sealed class AccountTypeMismatchException : DomainException
{
    public AccountTypeMismatchException(Guid accountId, AccountType expected, AccountType actual)
        : base($"Account {accountId} is a {actual} account where a {expected} account is required.")
    {
        AccountId = accountId;
        Expected = expected;
        Actual = actual;
    }

    public Guid AccountId { get; }

    public AccountType Expected { get; }

    public AccountType Actual { get; }
}
