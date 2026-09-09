namespace Ledger.Application.Exceptions;

/// <summary>
/// Thrown when a request is malformed: a required value is missing, or a value
/// is outside the range the operation accepts.
/// </summary>
/// <remarks>
/// This is distinct from a <see cref="Ledger.Domain.Exceptions.DomainException"/>.
/// A validation failure means the request was never well-formed enough for the
/// business rules to have an opinion about it; a domain exception means a
/// well-formed request was refused because the rules say no. They deserve
/// different status codes, so they are different types.
/// </remarks>
public sealed class ValidationException : Exception
{
    private const string DefaultMessage = "One or more validation errors occurred.";

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base(DefaultMessage)
    {
        ArgumentNullException.ThrowIfNull(errors);

        Errors = errors;
    }

    public ValidationException(string field, string error)
        : base(DefaultMessage)
    {
        Errors = new Dictionary<string, string[]> { [field] = [error] };
    }

    /// <summary>
    /// The failures, keyed by the name of the field they belong to.
    /// </summary>
    /// <remarks>
    /// Keyed by field rather than a flat list so a client can attach each
    /// message to the input that caused it, and so one response can report every
    /// problem at once instead of making the caller fix them one round trip at a
    /// time.
    /// </remarks>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
