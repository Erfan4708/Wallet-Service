namespace Ledger.Domain.Exceptions;

/// <summary>
/// Thrown when a transaction that has already been reversed is reversed again.
/// </summary>
/// <remarks>
/// Reversing twice would return the accounts to a state that never legitimately
/// existed, so a transaction may be reversed at most once. The database enforces
/// the same rule with a unique constraint; this is the friendlier of the two.
/// </remarks>
public sealed class TransactionAlreadyReversedException : DomainException
{
    public TransactionAlreadyReversedException(Guid transactionId)
        : base($"Transaction {transactionId} has already been reversed.")
    {
        TransactionId = transactionId;
    }

    public Guid TransactionId { get; }
}
