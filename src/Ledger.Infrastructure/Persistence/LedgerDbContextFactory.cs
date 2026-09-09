using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="LedgerDbContext"/> for the <c>dotnet ef</c> tooling.
/// </summary>
/// <remarks>
/// Without this, the tooling would have to boot the API host to find the
/// context, which means a migration could not be scaffolded unless the host's
/// configuration — including a real connection string — were present. Adding a
/// migration is an offline, design-time act: it needs the provider in order to
/// generate PostgreSQL syntax, but it never opens a connection. The value below
/// is therefore a placeholder shape, not a credential, and it is overridable for
/// anyone who wants the tooling to talk to a real database.
/// </remarks>
internal sealed class LedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    private const string PlaceholderConnectionString = "Host=localhost;Database=ledger;Username=postgres";

    public LedgerDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("LEDGER_DESIGN_TIME_CONNECTION")
            ?? PlaceholderConnectionString;

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new LedgerDbContext(options);
    }
}
