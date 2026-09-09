namespace Ledger.Domain.Enums;

/// <summary>
/// Stable names for the ledger's system accounts.
/// </summary>
/// <remarks>
/// System accounts are addressed by a meaningful key rather than by a hard-coded
/// identifier, so that no magic GUID appears in application code and so that the
/// account a deposit settles against is obvious from the name.
/// </remarks>
public static class SystemAccountKeys
{
    /// <summary>
    /// The counterparty for money entering or leaving the system in a currency.
    /// </summary>
    public static string Settlement(Currency currency) => $"SETTLEMENT:{currency}";
}
