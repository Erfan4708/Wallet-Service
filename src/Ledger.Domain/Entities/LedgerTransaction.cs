using Ledger.Domain.Enums;
using Ledger.Domain.Events;
using Ledger.Domain.Exceptions;
using Ledger.Domain.ValueObjects;

namespace Ledger.Domain.Entities;

/// <summary>
/// A balanced set of ledger entries recording one financial event, and the only
/// thing in the system that can move an account balance.
/// </summary>
/// <remarks>
/// <para>
/// This is the aggregate root for double-entry bookkeeping. The invariant it
/// owns — that the entries of a transaction sum to zero — spans several entries,
/// which is precisely the definition of an aggregate boundary. Because
/// <see cref="LedgerEntry"/> has no public constructor and
/// <c>Account.Credit</c>/<c>Account.Debit</c> are internal, there is no sequence
/// of public calls anywhere in the solution that can produce a lone entry or a
/// balance change without one.
/// </para>
/// <para>
/// Every factory here both writes the entries <em>and</em> applies them to the
/// accounts it was given, in one indivisible step. Splitting those apart is how
/// a stored balance drifts from its ledger.
/// </para>
/// <para>
/// A transaction holds exactly one currency. That is enforced structurally in the
/// database by a composite foreign key, so a mixed-currency transaction cannot be
/// stored at all. Foreign exchange would need explicit conversion legs against an
/// FX position account and a per-currency balance check; see ADR-005.
/// </para>
/// </remarks>
public sealed class LedgerTransaction
{
    private readonly List<LedgerEntry> _entries = [];
    private readonly List<IDomainEvent> _domainEvents = [];

    private LedgerTransaction(
        Guid id,
        LedgerTransactionKind kind,
        Currency currency,
        DateTimeOffset occurredAt,
        string? idempotencyKey,
        string? externalReference,
        Guid? reversesTransactionId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A transaction requires an identifier.", nameof(id));
        }

        Id = id;
        Kind = kind;
        Currency = currency;
        OccurredAt = occurredAt;
        IdempotencyKey = NormaliseKey(idempotencyKey);
        ExternalReference = NormaliseKey(externalReference);
        ReversesTransactionId = reversesTransactionId;
    }

    /// <summary>Only for the persistence layer's materializer. See <see cref="Account"/>.</summary>
    private LedgerTransaction()
    {
    }

    public Guid Id { get; }

    public LedgerTransactionKind Kind { get; }

    public Currency Currency { get; }

    /// <summary>
    /// When the event happened in the business's world.
    /// </summary>
    /// <remarks>
    /// Distinct from the recording time the database stamps on each row. A
    /// backdated correction has a value date that differs from its booking date,
    /// and that distinction cannot be retrofitted once there are a million rows
    /// carrying only one timestamp. Supplied by the caller, never read from the
    /// clock here — the domain stays a pure function of its inputs.
    /// </remarks>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>The client's key for making a retry safe, if it supplied one.</summary>
    public string? IdempotencyKey { get; }

    /// <summary>An identifier from whatever external system caused this, if any.</summary>
    public string? ExternalReference { get; }

    /// <summary>The transaction this one reverses, for a reversal.</summary>
    public Guid? ReversesTransactionId { get; }

    public IReadOnlyList<LedgerEntry> Entries => _entries;

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>The signed total of every entry. Always zero for a stored transaction.</summary>
    public Money Total => _entries.Aggregate(
        Money.Zero(Currency), (running, entry) => running.Add(entry.Amount));

    /// <summary>
    /// Money entering the system: the settlement account is debited, the wallet
    /// credited.
    /// </summary>
    /// <remarks>
    /// The settlement leg is what makes a deposit honest. Crediting the wallet
    /// alone would create money from nothing, and the ledger could then never be
    /// proven to sum to zero — losing the strongest assertion this system has.
    /// </remarks>
    public static LedgerTransaction Deposit(
        Guid id,
        Account wallet,
        Account settlement,
        Money amount,
        DateTimeOffset occurredAt,
        string? idempotencyKey = null,
        string? externalReference = null)
    {
        EnsureKinds(wallet, settlement);
        EnsureMovableAmount(amount, wallet, settlement);

        var transaction = new LedgerTransaction(
            id, LedgerTransactionKind.Deposit, amount.Currency,
            occurredAt, idempotencyKey, externalReference, reversesTransactionId: null);

        transaction.Post(wallet, amount);
        transaction.Post(settlement, amount.Negate());

        return transaction.Seal();
    }

    /// <summary>
    /// Money leaving the system: the wallet is debited, the settlement account
    /// credited.
    /// </summary>
    public static LedgerTransaction Withdraw(
        Guid id,
        Account wallet,
        Account settlement,
        Money amount,
        DateTimeOffset occurredAt,
        string? idempotencyKey = null,
        string? externalReference = null)
    {
        EnsureKinds(wallet, settlement);
        EnsureMovableAmount(amount, wallet, settlement);

        var transaction = new LedgerTransaction(
            id, LedgerTransactionKind.Withdrawal, amount.Currency,
            occurredAt, idempotencyKey, externalReference, reversesTransactionId: null);

        transaction.Post(wallet, amount.Negate());
        transaction.Post(settlement, amount);

        return transaction.Seal();
    }

    /// <summary>Money moving between two wallets.</summary>
    public static LedgerTransaction Transfer(
        Guid id,
        Account source,
        Account destination,
        Money amount,
        DateTimeOffset occurredAt,
        string? idempotencyKey = null,
        string? externalReference = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        EnsureType(source, AccountType.Wallet);
        EnsureType(destination, AccountType.Wallet);

        if (source.Id == destination.Id)
        {
            throw new SameAccountTransferException(source.Id);
        }

        EnsureMovableAmount(amount, source, destination);

        var transaction = new LedgerTransaction(
            id, LedgerTransactionKind.Transfer, amount.Currency,
            occurredAt, idempotencyKey, externalReference, reversesTransactionId: null);

        transaction.Post(source, amount.Negate());
        transaction.Post(destination, amount);

        return transaction.Seal();
    }

    /// <summary>
    /// The exact negation of an earlier transaction.
    /// </summary>
    /// <remarks>
    /// Corrections are made by recording another transaction, never by editing or
    /// deleting the original. The ledger is the audit record; a record you can
    /// rewrite is not one.
    /// </remarks>
    /// <param name="accounts">
    /// Every account touched by <paramref name="original"/>, already locked. The
    /// caller supplies them because loading is not the domain's job.
    /// </param>
    public static LedgerTransaction Reverse(
        Guid id,
        LedgerTransaction original,
        IReadOnlyDictionary<Guid, Account> accounts,
        DateTimeOffset occurredAt,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(accounts);

        if (original.Kind == LedgerTransactionKind.Reversal)
        {
            // Reversing a reversal would restore a state that was already
            // determined to be wrong. If the correction itself was wrong, the
            // answer is a new transaction describing the intended position.
            throw TransactionAlreadyReversedException.ForReversal(original.Id);
        }

        var reversal = new LedgerTransaction(
            id, LedgerTransactionKind.Reversal, original.Currency,
            occurredAt, idempotencyKey, original.ExternalReference, original.Id);

        foreach (var entry in original.Entries)
        {
            if (!accounts.TryGetValue(entry.AccountId, out var account))
            {
                throw new ArgumentException(
                    $"Account {entry.AccountId} is required to reverse transaction {original.Id}.",
                    nameof(accounts));
            }

            reversal.Post(account, entry.Amount.Negate());
        }

        return reversal.Seal();
    }

    /// <summary>
    /// Whether a replayed request describes the same movement as this transaction.
    /// </summary>
    /// <remarks>
    /// A retry that reuses an idempotency key for different parameters must not
    /// quietly receive the original's result — that would report success for an
    /// operation that never happened. The fingerprint is the kind of movement and
    /// every leg the request asked for: which account, in which direction, and how
    /// much. A deposit of the same amount into a different wallet, or a transfer
    /// between the same two wallets in the opposite direction, is a different
    /// operation. A fuller fingerprint would hash the whole request and store it
    /// alongside the key.
    /// </remarks>
    /// <param name="kind">The kind of movement the request asks for.</param>
    /// <param name="legs">
    /// Each account the request moves money on, with the signed amount it asks
    /// for: positive increases the account's balance, negative decreases it.
    /// </param>
    public bool Matches(LedgerTransactionKind kind, IReadOnlyCollection<(Guid AccountId, Money Amount)> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);

        return Kind == kind
            && legs.Count > 0
            && legs.All(leg => _entries.Any(entry => entry.AccountId == leg.AccountId && entry.Amount == leg.Amount));
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    private void Post(Account account, Money signedAmount)
    {
        _entries.Add(new LedgerEntry(Id, account.Id, signedAmount, (short)_entries.Count));

        // Applying the balance change here, rather than leaving it to the caller,
        // is what makes the projection and the entries impossible to write apart.
        if (signedAmount.IsPositive)
        {
            account.Credit(signedAmount);
        }
        else
        {
            account.Debit(signedAmount.Negate());
        }
    }

    /// <summary>
    /// Asserts the aggregate's own invariant before it is allowed to exist.
    /// </summary>
    private LedgerTransaction Seal()
    {
        if (_entries.Count < 2)
        {
            throw new UnbalancedTransactionException(Id, _entries.Count);
        }

        var total = Total;
        if (!total.IsZero)
        {
            throw new UnbalancedTransactionException(Id, total);
        }

        _domainEvents.Add(new LedgerTransactionRecorded(Id, Kind, Currency, OccurredAt));

        return this;
    }

    private static void EnsureKinds(Account wallet, Account settlement)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(settlement);

        EnsureType(wallet, AccountType.Wallet);
        EnsureType(settlement, AccountType.System);
    }

    private static void EnsureType(Account account, AccountType expected)
    {
        if (account.Type != expected)
        {
            throw new AccountTypeMismatchException(account.Id, expected, account.Type);
        }
    }

    private static void EnsureMovableAmount(Money amount, Account first, Account second)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (!amount.IsPositive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "The amount must be positive; direction is expressed by the entries.");
        }

        // Checked before any balance is touched, so a mismatch cannot leave one
        // side of the transaction applied.
        EnsureCurrency(first, amount);
        EnsureCurrency(second, amount);
    }

    private static void EnsureCurrency(Account account, Money amount)
    {
        if (account.Currency != amount.Currency)
        {
            throw new CurrencyMismatchException(account.Currency, amount.Currency);
        }
    }

    private static string? NormaliseKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
