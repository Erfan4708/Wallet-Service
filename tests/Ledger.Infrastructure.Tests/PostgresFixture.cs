using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// One real PostgreSQL container, shared by every test in the collection.
/// </summary>
/// <remarks>
/// Shared rather than one container per test: starting PostgreSQL costs seconds,
/// and paying that per test would make the suite slow enough that people stop
/// running it. Isolation comes from truncating the tables between tests instead,
/// which costs milliseconds and gives each test the clean database it needs.
/// <para>
/// The schema is created by running the real migrations rather than by
/// <c>EnsureCreated</c>. That is the point of the exercise: it proves the
/// migrations in the repository can build the schema on an empty database, which
/// is exactly what will happen in production.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _container;

    public PostgresFixture()
    {
        // Built whenever the tests are going to run at all. If Docker was
        // demanded but is absent, starting this container is what produces the
        // real error, rather than an empty connection string further downstream.
        if (DockerAvailability.ShouldSkip)
        {
            return;
        }

        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("ledger")
            .WithUsername("ledger")
            .WithPassword("ledger")
            .Build();
    }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (_container is null)
        {
            return;
        }

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// A brand new context, with its own change tracker and connection.
    /// </summary>
    /// <remarks>
    /// Tests that write with one context and read with another rely on this:
    /// a second context has an empty change tracker, so anything it returns
    /// genuinely came back from PostgreSQL.
    /// </remarks>
    public LedgerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString);

        // Set LEDGER_SQL_LOG=1 to see the statements a test actually issues.
        if (Environment.GetEnvironmentVariable("LEDGER_SQL_LOG") is "1")
        {
            options.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
                .EnableSensitiveDataLogging();
        }

        return new LedgerDbContext(options.Options);
    }

    /// <summary>
    /// Returns the database to the state the migrations left it in.
    /// </summary>
    /// <remarks>
    /// The system accounts are seeded by a migration, so they are part of the
    /// schema rather than test data: they are zeroed, not deleted. Wiping them
    /// would leave a database in which no deposit could ever be recorded, which
    /// is not a state production can reach.
    /// </remarks>
    public async Task ResetAsync()
    {
        await using var context = CreateContext();

        await context.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE ledger_entries, ledger_transactions RESTART IDENTITY;
            DELETE FROM accounts WHERE account_type = 'Wallet';
            UPDATE accounts SET balance_amount = 0 WHERE account_type = 'System';
            """);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL";
}
