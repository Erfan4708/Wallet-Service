using Ledger.Domain.ValueObjects;

namespace Ledger.Domain.Exceptions;

/// <summary>
/// Thrown when an account is asked to release more money than it holds.
/// </summary>
public sealed class InsufficientFundsException : DomainException
{
    public InsufficientFundsException(Guid accountId, Money balance, Money requested)
        : base($"Account {accountId} holds {balance} but {requested} was requested.")
    {
        AccountId = accountId;
        Balance = balance;
        Requested = requested;
    }

    public Guid AccountId { get; }

    public Money Balance { get; }

    public Money Requested { get; }
}
