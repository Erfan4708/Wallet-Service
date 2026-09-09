using Ledger.Application.Ledger;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;
using Npgsql;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// What happens when several requests hit the same accounts at once.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests the design exists for. A domain invariant holds in one
/// process, in one thread; it says nothing about two requests arriving in the
/// same millisecond. Only a real database, with real row locks and real
/// concurrent connections, can answer that — which is why none of this can be
/// tested with a fake.
/// </para>
/// <para>
/// Every task below opens its own connection and its own DbContext, exactly as
/// two web requests would.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LedgerConcurrencyTests : IAsyncLifetime
{
    private const string DeadlockDetected = "40P01";

    private static readonly Guid WalletA = new("eeeeeeee-0000-0000-0000-00000000000a");
    private static readonly Guid WalletB = new("eeeeeeee-0000-0000-0000-00000000000b");

    private readonly PostgresFixture _postgres;

    public LedgerConcurrencyTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private LedgerHost NewHost() => new(_postgres.CreateContext());

    /// <summary>Runs an operation on its own connection and reports what happened.</summary>
    private async Task<Exception?> AttemptAsync(Func<LedgerHost, Task> operation)
    {
        try
        {
            await using var host = NewHost();
            await operation(host);

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void AssertNoDeadlocks(IEnumerable<Exception?> outcomes)
    {
        var deadlocks = outcomes
            .OfType<Exception>()
            .Select(Root)
            .OfType<PostgresException>()
            .Where(failure => failure.SqlState == DeadlockDetected)
            .ToList();

        Assert.True(deadlocks.Count == 0, $"PostgreSQL detected {deadlocks.Count} deadlock(s).");
    }

    private static Exception Root(Exception exception) =>
        exception.InnerException is null ? exception : Root(exception.InnerException);

    // ---------------------------------------------------- concurrent withdrawals

    // The scenario the whole concurrency design answers: an account holding just
    // enough for four withdrawals, asked for ten at once. Without a row lock the
    // balance check reads a value another request is about to change, and the
    // account is overdrawn. With one, exactly four succeed.
    [RequiresDockerFact]
    public async Task Concurrent_withdrawals_cannot_overdraw_an_account()
    {
        const int attempts = 10;
        const decimal each = 25m;

        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletA, Currency.USD, funding: 100m);
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ =>
            AttemptAsync(host => host.Withdraw.HandleAsync(
                new WithdrawCommand(Guid.NewGuid(), WalletA, each, Currency.USD)))));

        var succeeded = outcomes.Count(outcome => outcome is null);
        var refused = outcomes
            .OfType<Exception>()
            .Count(outcome => Root(outcome) is InsufficientFundsException);

        Assert.Equal(4, succeeded);
        Assert.Equal(attempts - 4, refused);
        AssertNoDeadlocks(outcomes);

        await using var reader = NewHost();
        Assert.Equal(0m, await reader.BalanceOfAsync(WalletA));

        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    // ------------------------------------------------------ concurrent transfers

    [RequiresDockerFact]
    public async Task Concurrent_transfers_from_one_account_cannot_overdraw_it()
    {
        const int attempts = 8;

        var destinations = Enumerable.Range(1, attempts)
            .Select(index => new Guid($"ffffffff-0000-0000-0000-00000000{index:D4}"))
            .ToList();

        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletA, Currency.USD, funding: 60m);

            foreach (var destination in destinations)
            {
                await host.OpenWalletAsync(destination, Currency.USD);
            }
        }

        var outcomes = await Task.WhenAll(destinations.Select(destination =>
            AttemptAsync(host => host.Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), WalletA, destination, 20m, Currency.USD)))));

        Assert.Equal(3, outcomes.Count(outcome => outcome is null));
        AssertNoDeadlocks(outcomes);

        await using var reader = NewHost();
        Assert.Equal(0m, await reader.BalanceOfAsync(WalletA));

        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    // Both directions at once. Locking in argument order would have each
    // transaction holding the row the other wants, and PostgreSQL would break the
    // tie by killing one with a deadlock error. Sorting the identifiers before
    // locking makes that impossible rather than unlikely — which is why this test
    // runs the pattern many times rather than once.
    [RequiresDockerFact]
    public async Task Transfers_in_opposite_directions_do_not_deadlock()
    {
        const int rounds = 20;

        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletA, Currency.USD, funding: 1000m);
            await host.OpenWalletAsync(WalletB, Currency.USD, funding: 1000m);
        }

        var operations = Enumerable.Range(0, rounds).SelectMany(_ => new[]
        {
            AttemptAsync(host => host.Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), WalletA, WalletB, 1m, Currency.USD))),
            AttemptAsync(host => host.Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), WalletB, WalletA, 1m, Currency.USD))),
        });

        var outcomes = await Task.WhenAll(operations);

        AssertNoDeadlocks(outcomes);
        Assert.All(outcomes, outcome => Assert.Null(outcome));

        // Equal traffic in both directions, so both wallets end where they began.
        await using var reader = NewHost();
        Assert.Equal(1000m, await reader.BalanceOfAsync(WalletA));
        Assert.Equal(1000m, await reader.BalanceOfAsync(WalletB));

        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    // ---------------------------------------------------- concurrent idempotency

    // A client retrying a timed-out request can easily have two attempts in flight
    // at once. Both must not move money. They serialise on the account locks, so
    // the second one's read sees the first one's committed transaction; the unique
    // index on the key is the backstop if that ever fails.
    [RequiresDockerFact]
    public async Task Concurrent_requests_with_the_same_key_move_the_money_once()
    {
        const int attempts = 6;
        const string key = "same-key-many-times";

        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletA, Currency.USD);
        }

        var results = new LedgerTransactionResult?[attempts];

        var outcomes = await Task.WhenAll(Enumerable.Range(0, attempts).Select(index =>
            AttemptAsync(async host =>
                results[index] = await host.Deposit.HandleAsync(
                    new DepositCommand(Guid.NewGuid(), WalletA, 100m, Currency.USD, key)))));

        AssertNoDeadlocks(outcomes);

        // The money moved exactly once, whatever each caller was told.
        await using var reader = NewHost();
        Assert.Equal(100m, await reader.BalanceOfAsync(WalletA));
        Assert.Equal(1, await LedgerInvariants.CountAsync(_postgres.ConnectionString, "ledger_transactions"));

        // And every attempt that returned at all named the same transaction.
        var identifiers = results
            .Where(result => result is not null)
            .Select(result => result!.TransactionId)
            .Distinct()
            .ToList();

        Assert.Single(identifiers);

        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);
    }

    // -------------------------------------------------------- mixed contention

    // Deposits, withdrawals and transfers all hitting the same two accounts at
    // once. The individual outcomes are not predictable; the invariants are, and
    // those are what must never bend.
    [RequiresDockerFact]
    public async Task The_invariants_survive_mixed_concurrent_traffic()
    {
        await using (var host = NewHost())
        {
            await host.OpenWalletAsync(WalletA, Currency.USD, funding: 500m);
            await host.OpenWalletAsync(WalletB, Currency.USD, funding: 500m);
        }

        var operations = Enumerable.Range(0, 12).SelectMany(index => new[]
        {
            AttemptAsync(host => host.Deposit.HandleAsync(
                new DepositCommand(Guid.NewGuid(), WalletA, 10m, Currency.USD))),
            AttemptAsync(host => host.Withdraw.HandleAsync(
                new WithdrawCommand(Guid.NewGuid(), WalletB, 15m, Currency.USD))),
            AttemptAsync(host => host.Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), WalletA, WalletB, 5m, Currency.USD))),
            AttemptAsync(host => host.Transfer.HandleAsync(
                new TransferCommand(Guid.NewGuid(), WalletB, WalletA, 7m, Currency.USD))),
        });

        var outcomes = await Task.WhenAll(operations);

        AssertNoDeadlocks(outcomes);
        await LedgerInvariants.AssertAllAsync(_postgres.ConnectionString);

        await using var reader = NewHost();
        Assert.True(await reader.BalanceOfAsync(WalletA) >= 0m);
        Assert.True(await reader.BalanceOfAsync(WalletB) >= 0m);
    }
}
