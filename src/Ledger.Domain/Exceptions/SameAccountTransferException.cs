namespace Ledger.Domain.Exceptions;

/// <summary>
/// Thrown when a transfer names the same account as source and destination.
/// </summary>
/// <remarks>
/// Such a transfer would balance perfectly and change nothing, so no invariant
/// would catch it. It is refused because it is certainly a mistake, and because
/// allowing it would let a caller take the same row lock twice.
/// </remarks>
public sealed class SameAccountTransferException : DomainException
{
    public SameAccountTransferException(Guid accountId)
        : base($"Account {accountId} cannot transfer to itself.")
    {
        AccountId = accountId;
    }

    public Guid AccountId { get; }
}
