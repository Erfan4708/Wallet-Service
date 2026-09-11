namespace Ledger.Domain.Exceptions;

/// <summary>
/// Thrown when a transaction's effects have already been undone once: it has been
/// reversed, or it is itself a reversal.
/// </summary>
/// <remarks>
/// <para>
/// Reversing twice would return the accounts to a state that never legitimately
/// existed, so a transaction may be reversed at most once. The database enforces
/// the same rule with a unique constraint; this is the friendlier of the two.
/// </para>
/// <para>
/// Reversing a reversal is refused under the same rule, but with its own message:
/// telling a client that a reversal "has already been reversed" when nothing ever
/// reversed it would be false.
/// </para>
/// </remarks>
public sealed class TransactionAlreadyReversedException : DomainException
{
    public TransactionAlreadyReversedException(Guid transactionId)
        : this(transactionId, $"Transaction {transactionId} has already been reversed.")
    {
    }

    private TransactionAlreadyReversedException(Guid transactionId, string message)
        : base(message)
    {
        TransactionId = transactionId;
    }

    public Guid TransactionId { get; }

    /// <summary>The refusal for an attempt to reverse a transaction that is itself a reversal.</summary>
    public static TransactionAlreadyReversedException ForReversal(Guid reversalId) =>
        new(reversalId, $"Transaction {reversalId} is itself a reversal and cannot be reversed.");
}
