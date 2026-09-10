using Ledger.Application.Abstractions;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Infrastructure;

/// <summary>
/// Registers the infrastructure layer's services.
/// </summary>
/// <remarks>
/// This is the seam where the application layer's abstractions get their
/// implementations. Everything registered here is concrete and PostgreSQL-aware;
/// everything the application layer sees is an interface it declared itself.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// The configuration key holding the PostgreSQL connection string.
    /// </summary>
    public const string ConnectionStringName = "LedgerDatabase";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        // Fail at startup with a sentence that says what to do, rather than on
        // the first request with a connection error. Credentials are never
        // embedded in code: they come from configuration, which in production
        // means an environment variable or a secret store.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. " +
                "Set ConnectionStrings__LedgerDatabase in the environment, or in user secrets for local development.");
        }

        // Scoped, matching the DbContext's own default lifetime. All three share
        // one instance per request, which is what makes a single SaveChanges
        // commit everything the request changed.
        services.AddDbContext<LedgerDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ILedgerTransactionRepository, LedgerTransactionRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Readiness reuses the DbContext the application already has rather than
        // opening a second connection of its own. A probe that checks a different
        // connection than the one serving traffic can report healthy while every
        // request fails -- it would be testing the wrong thing.
        //
        // Tagged rather than named so that liveness can exclude it: whether
        // PostgreSQL is reachable says nothing about whether this process is
        // alive, and restarting the process would not fix it.
        services.AddHealthChecks()
            .AddDbContextCheck<LedgerDbContext>(
                name: "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "db"]);

        return services;
    }
}
