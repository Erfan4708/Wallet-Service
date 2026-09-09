namespace Ledger.Domain.Enums;

/// <summary>
/// What an account is for, which decides whether its balance may go negative.
/// </summary>
public enum AccountType
{
    /// <summary>
    /// A customer's money. May never go negative.
    /// </summary>
    Wallet = 1,

    /// <summary>
    /// The ledger's boundary with the outside world.
    /// </summary>
    /// <remarks>
    /// A deposit has to take money from somewhere: it debits a system account and
    /// credits a wallet. Without that counterparty the ledger would create money
    /// from nothing and could never sum to zero.
    /// <para>
    /// A system account is <em>expected</em> to hold a negative balance, and the
    /// number means something precise: how much value the platform has issued
    /// into wallets, which is what reconciles against the bank rail.
    /// </para>
    /// </remarks>
    System = 2,
}
