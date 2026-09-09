using System.Globalization;
using Ledger.Application.Accounts.CreateAccount;
using Ledger.Application.Accounts.GetAccount;
using Ledger.Application.Exceptions;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Ledger.Domain.ValueObjects;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// Drives the Phase 2 use cases against a real PostgreSQL database.
/// </summary>
/// <remarks>
/// The handlers here are the ones the API will use, unchanged; only the
/// repository and unit of work behind them are now real. That is the claim being
/// tested: the application layer was written against abstractions and needed no
/// edit when a database appeared beneath it.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class AccountPersistenceTests : IAsyncLifetime
{
    private static readonly Guid AccountId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAccountId = new("aaaaaaaa-0000-0000-0000-000000000002");

    private readonly PostgresFixture _postgres;

    public AccountPersistenceTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [RequiresDockerFact]
    public async Task An_account_created_through_the_use_case_is_stored_in_postgres()
    {
        await using var context = _postgres.CreateContext();

        var summary = await HandlerFor(context).HandleAsync(new CreateAccountCommand(AccountId, Currency.EUR));

        Assert.Equal(AccountId, summary.Id);
        Assert.Equal(1, await CountRowsAsync());
    }

    // The heart of this phase: written through one DbContext, read through a
    // second. A second context starts with an empty change tracker, so a passing
    // read here cannot be an object that merely stayed in memory.
    [RequiresDockerFact]
    public async Task An_account_survives_being_read_through_a_different_context()
    {
        await using (var writeContext = _postgres.CreateContext())
        {
            await HandlerFor(writeContext).HandleAsync(new CreateAccountCommand(AccountId, Currency.EUR));
        }

        await using var readContext = _postgres.CreateContext();
        var summary = await new GetAccountHandler(new AccountRepository(readContext))
            .HandleAsync(new GetAccountQuery(AccountId));

        Assert.Equal(AccountId, summary.Id);
        Assert.Equal(Currency.EUR, summary.Currency);
        Assert.Equal(0m, summary.Balance);
    }

    [RequiresDockerFact]
    public async Task A_balance_changed_by_the_domain_is_persisted()
    {
        await using (var writeContext = _postgres.CreateContext())
        {
            var account = Account.Open(AccountId, Currency.USD);
            account.Credit(new Money(250.75m, Currency.USD));

            await new AccountRepository(writeContext).AddAsync(account);
            await new UnitOfWork(writeContext).SaveChangesAsync();
        }

        await using var readContext = _postgres.CreateContext();
        var reloaded = await new AccountRepository(readContext).GetByIdAsync(AccountId);

        Assert.NotNull(reloaded);
        Assert.Equal(new Money(250.75m, Currency.USD), reloaded.Balance);
    }

    [RequiresDockerFact]
    public async Task An_unknown_account_is_reported_as_not_found()
    {
        await using var context = _postgres.CreateContext();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            new GetAccountHandler(new AccountRepository(context))
                .HandleAsync(new GetAccountQuery(OtherAccountId)));
    }

    [RequiresDockerFact]
    public async Task Reusing_an_identifier_is_refused_and_the_stored_account_is_untouched()
    {
        await using (var first = _postgres.CreateContext())
        {
            await HandlerFor(first).HandleAsync(new CreateAccountCommand(AccountId, Currency.USD));
        }

        await using (var second = _postgres.CreateContext())
        {
            await Assert.ThrowsAsync<ConflictException>(() =>
                HandlerFor(second).HandleAsync(new CreateAccountCommand(AccountId, Currency.EUR)));
        }

        await using var reader = _postgres.CreateContext();
        var stored = await new AccountRepository(reader).GetByIdAsync(AccountId);

        Assert.Equal(1, await CountRowsAsync());
        Assert.Equal(Currency.USD, stored!.Currency);
    }

    // The use case looks for an existing row first, but two concurrent requests
    // can both pass that check. This bypasses it to prove the primary key is
    // what actually decides, and that its verdict still reaches the caller as a
    // conflict rather than an unhandled database error.
    [RequiresDockerFact]
    public async Task The_primary_key_refuses_a_duplicate_even_when_the_use_case_check_is_bypassed()
    {
        await using (var first = _postgres.CreateContext())
        {
            await new AccountRepository(first).AddAsync(Account.Open(AccountId, Currency.USD));
            await new UnitOfWork(first).SaveChangesAsync();
        }

        await using var second = _postgres.CreateContext();
        await new AccountRepository(second).AddAsync(Account.Open(AccountId, Currency.EUR));

        await Assert.ThrowsAsync<ConflictException>(() => new UnitOfWork(second).SaveChangesAsync());
    }

    // Atomicity of a single save: two accounts are registered, the second
    // collides with an existing row, and neither may end up stored.
    [RequiresDockerFact]
    public async Task A_failed_save_persists_nothing_from_that_unit_of_work()
    {
        await using (var seed = _postgres.CreateContext())
        {
            await new AccountRepository(seed).AddAsync(Account.Open(AccountId, Currency.USD));
            await new UnitOfWork(seed).SaveChangesAsync();
        }

        await using (var context = _postgres.CreateContext())
        {
            var repository = new AccountRepository(context);

            await repository.AddAsync(Account.Open(OtherAccountId, Currency.USD));
            await repository.AddAsync(Account.Open(AccountId, Currency.EUR));

            await Assert.ThrowsAsync<ConflictException>(() => new UnitOfWork(context).SaveChangesAsync());
        }

        await using var reader = _postgres.CreateContext();

        Assert.Null(await new AccountRepository(reader).GetByIdAsync(OtherAccountId));
        Assert.Equal(1, await CountRowsAsync());
    }

    // Amounts chosen because a binary floating point column would mangle them:
    // 0.1 + 0.2 is not 0.3 in IEEE-754, and 9999999999999.99 is past the range
    // in which a double can represent every value exactly.
    [RequiresDockerTheory]
    [InlineData("0.30")]
    [InlineData("0.10")]
    [InlineData("0.01")]
    [InlineData("9999999999999.99")]
    public async Task Monetary_amounts_survive_the_round_trip_exactly(string amount)
    {
        var expected = decimal.Parse(amount, CultureInfo.InvariantCulture);

        await using (var writeContext = _postgres.CreateContext())
        {
            var account = Account.Open(AccountId, Currency.USD);
            account.Credit(new Money(expected, Currency.USD));

            await new AccountRepository(writeContext).AddAsync(account);
            await new UnitOfWork(writeContext).SaveChangesAsync();
        }

        await using var readContext = _postgres.CreateContext();
        var reloaded = await new AccountRepository(readContext).GetByIdAsync(AccountId);

        Assert.Equal(expected, reloaded!.Balance.Amount);
        Assert.Equal(new Money(expected, Currency.USD), reloaded.Balance);
    }

    [RequiresDockerFact]
    public async Task Accumulating_cents_and_storing_the_total_does_not_drift()
    {
        await using (var writeContext = _postgres.CreateContext())
        {
            var account = Account.Open(AccountId, Currency.USD);
            for (var i = 0; i < 100; i++)
            {
                account.Credit(new Money(0.01m, Currency.USD));
            }

            await new AccountRepository(writeContext).AddAsync(account);
            await new UnitOfWork(writeContext).SaveChangesAsync();
        }

        await using var readContext = _postgres.CreateContext();
        var reloaded = await new AccountRepository(readContext).GetByIdAsync(AccountId);

        Assert.Equal(new Money(1.00m, Currency.USD), reloaded!.Balance);
    }

    [RequiresDockerTheory]
    [InlineData(Currency.USD)]
    [InlineData(Currency.EUR)]
    [InlineData(Currency.IRR)]
    public async Task Every_currency_survives_the_round_trip(Currency currency)
    {
        await using (var writeContext = _postgres.CreateContext())
        {
            await new AccountRepository(writeContext).AddAsync(Account.Open(AccountId, currency));
            await new UnitOfWork(writeContext).SaveChangesAsync();
        }

        await using var readContext = _postgres.CreateContext();
        var reloaded = await new AccountRepository(readContext).GetByIdAsync(AccountId);

        Assert.Equal(currency, reloaded!.Currency);
    }

    // Stored as the ISO 4217 alpha code, not the enum's ordinal position.
    // Reading the raw column is the only way to prove it: a round trip alone
    // would pass just as happily if the column held 0, 1 or 2.
    [RequiresDockerFact]
    public async Task Currency_is_stored_as_its_iso_alpha_code()
    {
        await using (var writeContext = _postgres.CreateContext())
        {
            await new AccountRepository(writeContext).AddAsync(Account.Open(AccountId, Currency.EUR));
            await new UnitOfWork(writeContext).SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT balance_currency FROM accounts WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", AccountId);

        Assert.Equal("EUR", (string?)await command.ExecuteScalarAsync());
    }

    private static CreateAccountHandler HandlerFor(LedgerDbContext context) =>
        new(new AccountRepository(context), new UnitOfWork(context));

    private async Task<int> CountRowsAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM accounts", connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
