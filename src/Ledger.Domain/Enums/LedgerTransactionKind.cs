namespace Ledger.Domain.Enums;

/// <summary>
/// Why a set of ledger entries was written.
/// </summary>
public enum LedgerTransactionKind
{
    /// <summary>Money entering the system: system account debited, wallet credited.</summary>
    Deposit = 1,

    /// <summary>Money leaving the system: wallet debited, system account credited.</summary>
    Withdrawal = 2,

    /// <summary>Money moving between two wallets.</summary>
    Transfer = 3,

    /// <summary>The exact negation of an earlier transaction.</summary>
    Reversal = 4,
}
