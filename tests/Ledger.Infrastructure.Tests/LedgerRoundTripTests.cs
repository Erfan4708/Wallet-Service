using System.Data.Common;
using Ledger.Application.Ledger;
using Ledger.Domain.Enums;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// What a ledger transaction says to the database while it holds row locks.
/// </summary>
/// <remarks>
/// Every round trip made while an account row is locked is time every other
/// movement on that account spends waiting, and the settlement account is locked
/// by every deposit and withdrawal in its currency. These tests pin down the
/// round trips that were removed because they bought nothing, so that they do not
/// quietly come back. See docs/PERFORMANCE.md.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LedgerRoundTripTests : IAsyncLifetime
{
    private static readonly Guid WalletId = new("abababab-0000-0000-0000-000000000001");

    private readonly PostgresFixture _postgres;

    public LedgerRoundTripTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // EF Core wraps SaveChanges inside an explicit transaction in a savepoint, so
    // that a caller can recover from a failed save and continue. The unit of work
    // never continues after a failure, so the savepoint was two round trips inside
    // the locked window that could never be used.
    [RequiresDockerFact]
    public async Task A_deposit_commits_without_creating_a_savepoint()
    {
        await using (var setup = new LedgerHost(_postgres.CreateContext()))
        {
            await setup.OpenWalletAsync(WalletId, Currency.USD);
        }

        var recorder = new TransactionRecorder();

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(_postgres.ConnectionString)
            .AddInterceptors(recorder)
            .Options;

        await using (var host = new LedgerHost(new LedgerDbContext(options)))
        {
            await host.Deposit.HandleAsync(new DepositCommand(Guid.NewGuid(), WalletId, 10m, Currency.USD));
        }

        // The first two prove the recorder was listening; without them a missing
        // savepoint could just as well mean nothing was observed at all.
        Assert.Equal(1, recorder.TransactionsStarted);
        Assert.Equal(1, recorder.Commits);
        Assert.Equal(0, recorder.SavepointsCreated);
    }

    /// <remarks>
    /// A transaction interceptor rather than a command interceptor: a savepoint is
    /// created through the provider's transaction API, not as a command EF Core
    /// executes, so a command interceptor would never see one and the assertion
    /// would pass for the wrong reason.
    /// </remarks>
    private sealed class TransactionRecorder : DbTransactionInterceptor
    {
        public int TransactionsStarted { get; private set; }

        public int Commits { get; private set; }

        public int SavepointsCreated { get; private set; }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            TransactionsStarted++;

            return base.TransactionStartingAsync(connection, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Commits++;

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult> CreatingSavepointAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            SavepointsCreated++;

            return base.CreatingSavepointAsync(transaction, eventData, result, cancellationToken);
        }
    }
}
