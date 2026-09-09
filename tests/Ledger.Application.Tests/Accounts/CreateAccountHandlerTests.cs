using Ledger.Application.Accounts.CreateAccount;
using Ledger.Application.Exceptions;
using Ledger.Application.Tests.Fakes;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;

namespace Ledger.Application.Tests.Accounts;

public class CreateAccountHandlerTests
{
    private static readonly Guid AccountId = new("11111111-1111-1111-1111-111111111111");

    private readonly List<string> _callLog = [];
    private readonly FakeAccountRepository _accounts;
    private readonly FakeUnitOfWork _unitOfWork;
    private readonly CreateAccountHandler _handler;

    public CreateAccountHandlerTests()
    {
        _accounts = new FakeAccountRepository(_callLog);
        _unitOfWork = new FakeUnitOfWork(_callLog);
        _handler = new CreateAccountHandler(_accounts, _unitOfWork);
    }

    [Fact]
    public async Task Opens_an_account_with_a_zero_balance_in_the_requested_currency()
    {
        var summary = await _handler.HandleAsync(new CreateAccountCommand(AccountId, Currency.EUR));

        Assert.Equal(AccountId, summary.Id);
        Assert.Equal(Currency.EUR, summary.Currency);
        Assert.Equal(0m, summary.Balance);
    }

    [Fact]
    public async Task Stores_the_account()
    {
        await _handler.HandleAsync(new CreateAccountCommand(AccountId, Currency.USD));

        var stored = _accounts.Find(AccountId);

        Assert.NotNull(stored);
        Assert.Equal(Currency.USD, stored.Currency);
    }

    // The commit has to happen after the account is registered, and exactly
    // once. Asserting the order is what stops a future refactor from saving an
    // empty unit of work and reporting success.
    [Fact]
    public async Task Adds_the_account_before_committing_and_commits_once()
    {
        await _handler.HandleAsync(new CreateAccountCommand(AccountId, Currency.USD));

        Assert.Equal(["GetByIdAsync", "AddAsync", "SaveChangesAsync"], _callLog);
        Assert.Equal(1, _unitOfWork.SaveCount);
    }

    [Fact]
    public async Task An_empty_identifier_is_rejected()
    {
        var command = new CreateAccountCommand(Guid.Empty, Currency.USD);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(command));

        Assert.Contains("AccountId", exception.Errors.Keys);
    }

    [Fact]
    public async Task An_undefined_currency_is_rejected()
    {
        var command = new CreateAccountCommand(AccountId, (Currency)9999);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(command));

        Assert.Contains("Currency", exception.Errors.Keys);
    }

    // Reporting every problem at once, rather than the first one, is the reason
    // the errors are a keyed collection instead of a single message.
    [Fact]
    public async Task Every_validation_failure_is_reported_together()
    {
        var command = new CreateAccountCommand(Guid.Empty, (Currency)9999);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(command));

        Assert.Equal(2, exception.Errors.Count);
    }

    [Fact]
    public async Task Nothing_is_stored_or_committed_when_validation_fails()
    {
        var command = new CreateAccountCommand(Guid.Empty, Currency.USD);

        await Assert.ThrowsAsync<ValidationException>(() => _handler.HandleAsync(command));

        Assert.Equal(0, _accounts.Count);
        Assert.Equal(0, _unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Reusing_an_existing_identifier_is_a_conflict()
    {
        _accounts.Seed(Account.Open(AccountId, Currency.USD));

        await Assert.ThrowsAsync<ConflictException>(
            () => _handler.HandleAsync(new CreateAccountCommand(AccountId, Currency.EUR)));
    }

    [Fact]
    public async Task A_conflicting_request_does_not_commit_or_overwrite()
    {
        _accounts.Seed(Account.Open(AccountId, Currency.USD));

        await Assert.ThrowsAsync<ConflictException>(
            () => _handler.HandleAsync(new CreateAccountCommand(AccountId, Currency.EUR)));

        Assert.Equal(0, _unitOfWork.SaveCount);
        Assert.Equal(Currency.USD, _accounts.Find(AccountId)!.Currency);
    }

    [Fact]
    public async Task A_null_command_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.HandleAsync(null!));
    }
}
