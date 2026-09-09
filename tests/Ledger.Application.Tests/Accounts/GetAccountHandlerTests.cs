using Ledger.Application.Accounts.GetAccount;
using Ledger.Application.Exceptions;
using Ledger.Application.Tests.Fakes;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Ledger.Domain.ValueObjects;

namespace Ledger.Application.Tests.Accounts;

public class GetAccountHandlerTests
{
    private static readonly Guid AccountId = new("22222222-2222-2222-2222-222222222222");

    private readonly FakeAccountRepository _accounts = new();
    private readonly GetAccountHandler _handler;

    public GetAccountHandlerTests() => _handler = new GetAccountHandler(_accounts);

    [Fact]
    public async Task Returns_the_account()
    {
        _accounts.Seed(Account.Open(AccountId, Currency.EUR));

        var summary = await _handler.HandleAsync(new GetAccountQuery(AccountId));

        Assert.Equal(AccountId, summary.Id);
        Assert.Equal(Currency.EUR, summary.Currency);
        Assert.Equal(0m, summary.Balance);
    }

    [Fact]
    public async Task Reports_the_current_balance()
    {
        // Funded through a real deposit rather than by setting a balance:
        // Account.Credit is internal to the domain now, so a balance can only
        // exist because ledger entries put it there.
        var account = LedgerScenario.FundedWallet(AccountId, Currency.USD, 125.50m);
        _accounts.Seed(account);

        var summary = await _handler.HandleAsync(new GetAccountQuery(AccountId));

        Assert.Equal(125.50m, summary.Balance);
    }

    [Fact]
    public async Task An_unknown_account_is_not_found()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _handler.HandleAsync(new GetAccountQuery(AccountId)));

        Assert.Equal(AccountId, exception.Key);
    }

    // An empty identifier is a malformed request, not a missing account. The
    // distinction matters: 400 tells the caller to fix the request, 404 tells
    // them the request was fine but the thing is not there.
    [Fact]
    public async Task An_empty_identifier_is_rejected_before_lookup()
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => _handler.HandleAsync(new GetAccountQuery(Guid.Empty)));

        Assert.Empty(_accounts.CallLog);
    }

    [Fact]
    public async Task A_null_query_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.HandleAsync(null!));
    }
}
