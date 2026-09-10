using Ledger.Application.Abstractions;
using Ledger.Infrastructure.Messaging;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // The unit of work stamps outbox rows, so it needs a clock. TryAdd
        // because the application layer registers the same one.
        services.TryAddSingleton(TimeProvider.System);

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

        AddMessaging(services, configuration);
        AddCache(services, configuration);

        return services;
    }

    /// <summary>
    /// Applies any pending migrations and returns.
    /// </summary>
    /// <remarks>
    /// Run as a one-shot step before the API starts, never by the API itself.
    /// Several replicas starting at once would otherwise race to migrate the same
    /// database, and EF Core takes no lock that would make that safe. A single
    /// migrator that must finish before the API starts is both simpler and
    /// correct.
    /// </remarks>
    public static async Task MigrateLedgerDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Registers the broker connection, the outbox publisher, and the broker's
    /// health check.
    /// </summary>
    /// <remarks>
    /// The publisher is a hosted service, so several API instances each run one.
    /// That is safe by design: claiming with <c>SKIP LOCKED</c> and a lease is
    /// what stops two of them publishing the same message at the same time.
    /// </remarks>
    private static void AddMessaging(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));

        services.AddSingleton<IMessagePublisher, RabbitMqMessagePublisher>();

        var outbox = configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions();
        if (outbox.PublisherEnabled)
        {
            services.AddHostedService<OutboxPublisher>();
        }

        // Degraded, not unhealthy. A broker outage must never stop the ledger
        // accepting money: transactions keep committing with their outbox rows and
        // the backlog drains later. Failing readiness here would refuse traffic
        // the instance can serve perfectly well.
        services.AddHealthChecks()
            .AddCheck<RabbitMqHealthCheck>(
                name: "rabbitmq",
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "messaging"]);
    }

    /// <summary>
    /// Registers Redis, if a connection string was supplied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately minimal. Nothing in the ledger reads or writes Redis yet, and
    /// inventing a cache to justify the dependency would mean caching financial
    /// state — which is how a stale balance ends up authoritative. PostgreSQL
    /// remains the source of truth for every balance, entry and transaction.
    /// </para>
    /// <para>
    /// What is registered is the connection and its health, so the infrastructure
    /// is ready for a feature that genuinely needs it: rate limiting, idempotency
    /// caching in front of the database, or distributed locks.
    /// </para>
    /// <para>
    /// <c>AbortOnConnectFail</c> is off so that a Redis outage does not prevent
    /// the service starting, and the multiplexer reconnects on its own.
    /// </para>
    /// </remarks>
    private static void AddCache(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));

        var redis = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>() ?? new RedisOptions();

        if (!string.IsNullOrWhiteSpace(redis.ConnectionString))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(redis.ConnectionString);
                options.AbortOnConnectFail = false;

                return ConnectionMultiplexer.Connect(options);
            });
        }

        services.AddHealthChecks()
            .AddCheck<RedisHealthCheck>(
                name: "redis",
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "cache"]);
    }
}
