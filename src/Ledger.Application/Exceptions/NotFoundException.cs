namespace Ledger.Application.Exceptions;

/// <summary>
/// Thrown when a use case refers to something that does not exist.
/// </summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string resource, object key)
        : base($"{resource} '{key}' was not found.")
    {
        Resource = resource;
        Key = key;
    }

    public string Resource { get; }

    public object Key { get; }
}
