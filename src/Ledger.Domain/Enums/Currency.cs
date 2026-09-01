namespace Ledger.Domain.Enums;

/// <summary>
/// The currencies this ledger can hold, identified by their ISO 4217 code.
/// </summary>
/// <remarks>
/// The underlying values are the ISO 4217 <em>numeric</em> codes rather than the
/// implicit 0, 1, 2 the compiler would assign. Enum members are persisted by
/// value, so relying on declaration order would mean that reordering or
/// inserting a member silently changes the meaning of every stored row.
/// </remarks>
public enum Currency
{
    /// <summary>United States dollar.</summary>
    USD = 840,

    /// <summary>Euro.</summary>
    EUR = 978,

    /// <summary>
    /// Iranian rial. Note that the <em>toman</em> (10 rial) is a colloquial unit,
    /// not an ISO 4217 currency: it is a presentation concern and must not be
    /// stored as if it were a currency of its own.
    /// </summary>
    IRR = 364,
}
