using Ledger.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// The EF Core model for the ledger database.
/// </summary>
/// <remarks>
/// This type lives in the infrastructure layer and never crosses out of it: the
/// application layer knows only <c>IAccountRepository</c> and <c>IUnitOfWork</c>,
/// so no use case can reach a <see cref="DbSet{TEntity}"/>, start a query, or
/// depend on EF Core's change tracker.
/// </remarks>
public sealed class LedgerDbContext : DbContext
{
    public LedgerDbContext(DbContextOptions<LedgerDbContext> options)
        : base(options)
    {
    }

    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Configuration is discovered rather than listed, so adding an entity
        // means adding one file instead of also remembering to register it.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
