namespace Ledger.Application.Exceptions;

/// <summary>
/// Thrown when a request cannot be applied because it conflicts with the
/// current state of the system — for example creating something that already
/// exists.
/// </summary>
/// <remarks>
/// This is the exception that will later carry optimistic concurrency failures,
/// where a request was valid when it was made but the state it assumed has since
/// changed. Both cases are "retry with fresh information", which is exactly what
/// 409 Conflict means.
/// </remarks>
public sealed class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }
}
