namespace Ledger.Domain.Exceptions;

/// <summary>
/// Base type for violations of a business rule.
/// </summary>
/// <remarks>
/// The distinction this base class draws is deliberate. A
/// <see cref="DomainException"/> means a legitimate request was refused because
/// the business rules say so — an outcome an outer layer can translate into a
/// meaningful response for the caller. Contract violations by the calling code
/// (a null argument, an amount of zero where the operation requires a positive
/// amount) are <em>not</em> domain exceptions: they signal a defect in the
/// caller and keep using the <see cref="ArgumentException"/> family.
/// </remarks>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }

    protected DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
