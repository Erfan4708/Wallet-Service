using Ledger.Application.Accounts.CreateAccount;
using Ledger.Application.Accounts.GetAccount;
using Ledger.Application.Ledger;
using Ledger.Domain.Enums;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Persistence.Repositories;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// The real use cases wired to the real repositories over one DbContext.
/// </summary>
/// <remarks>
/// The handlers here are exactly the ones the API resolves from the container —
/// no test doubles anywhere below the HTTP boundary. That is what makes these
/// tests worth their runtime: they exercise the domain, the application layer,
/// EF Core, and PostgreSQL together, which is the only place a mapping mistake
/// or a lock-ordering mistake can actually show up.
/// </remarks>
internal sealed class LedgerHost : IAsyncDisposable
{
    private readonly LedgerDbContext _context;

    internal LedgerHost(LedgerDbContext context)
    {
        _context = context;

        var accounts = new AccountRepository(context);
        var transactions = new LedgerTransactionRepository(context);
        var unitOfWork = new UnitOfWork(context);

        Accounts = accounts;
        Transactions = transactions;

        CreateAccount = new CreateAccountHandler(accounts, unitOfWork);
        GetAccount = new GetAccountHandler(accounts);
        Statement = new GetAccountStatementHandler(accounts, transactions);
        Deposit = new DepositHandler(accounts, transactions, unitOfWork, TimeProvider.System);
        Withdraw = new WithdrawHandler(accounts, transactions, unitOfWork, TimeProvider.System);
        Transfer = new TransferHandler(accounts, transactions, unitOfWork, TimeProvider.System);
        Reverse = new ReverseTransactionHandler(accounts, transactions, unitOfWork, TimeProvider.System);
    }

    internal AccountRepository Accounts { get; }

    internal LedgerTransactionRepository Transactions { get; }

    internal CreateAccountHandler CreateAccount { get; }

    internal GetAccountHandler GetAccount { get; }

    internal GetAccountStatementHandler Statement { get; }

    internal DepositHandler Deposit { get; }

    internal WithdrawHandler Withdraw { get; }

    internal TransferHandler Transfer { get; }

    internal ReverseTransactionHandler Reverse { get; }

    /// <summary>Opens a wallet and, optionally, funds it through a real deposit.</summary>
    /// <remarks>
    /// Funding goes through the ledger rather than by setting a balance, because
    /// setting a balance is no longer possible — <c>Account.Credit</c> is internal
    /// to the domain. A fixture that could conjure money would be testing a system
    /// that does not exist.
    /// </remarks>
    internal async Task<Guid> OpenWalletAsync(Guid id, Currency currency, decimal funding = 0m)
    {
        await CreateAccount.HandleAsync(new CreateAccountCommand(id, currency));

        if (funding > 0m)
        {
            await Deposit.HandleAsync(new DepositCommand(Guid.NewGuid(), id, funding, currency));
        }

        return id;
    }

    internal async Task<decimal> BalanceOfAsync(Guid accountId) =>
        (await GetAccount.HandleAsync(new GetAccountQuery(accountId))).Balance;

    public ValueTask DisposeAsync() => _context.DisposeAsync();
}
