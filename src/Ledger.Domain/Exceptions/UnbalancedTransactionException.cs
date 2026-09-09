using Ledger.Domain.ValueObjects;

namespace Ledger.Domain.Exceptions;

/// <summary>
/// Thrown when a set of ledger entries does not sum to zero, or has fewer than
/// two legs.
/// </summary>
/// <remarks>
/// This should be impossible to trigger from outside the domain: the factories on
/// the transaction aggregate are the only way to obtain entries, and none of them
/// can produce an unbalanced set. It exists as the aggregate's own assertion of
/// the one rule the whole system is built to guarantee — if it is ever thrown,
/// the defect is here, not in the caller.
/// </remarks>
public sealed class UnbalancedTransactionException : DomainException
{
    public UnbalancedTransactionException(Guid transactionId, Money total)
        : base($"Transaction {transactionId} does not balance: its entries sum to {total}.")
    {
        TransactionId = transactionId;
    }

    public UnbalancedTransactionException(Guid transactionId, int entryCount)
        : base($"Transaction {transactionId} has {entryCount} entries; double-entry requires at least two.")
    {
        TransactionId = transactionId;
    }

    public Guid TransactionId { get; }
}
