using Ledger.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// The readiness check against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// The API tests prove readiness fails when nothing is listening, which is easy
/// to arrange and proves only half of the contract. A check that always reported
/// unhealthy would pass that test too. This one runs against the real container,
/// so healthy means the check actually reached PostgreSQL and got an answer.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class HealthCheckTests
{
    private const string ReadyTag = "ready";

    private readonly PostgresFixture _postgres;

    public HealthCheckTests(PostgresFixture postgres) => _postgres = postgres;

    private static ServiceProvider BuildFor(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:LedgerDatabase"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // The same registration the API uses. Testing a health check that was
        // configured differently from the running service would prove nothing
        // about the running service.
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    private static async Task<HealthReport> CheckReadinessAsync(ServiceProvider provider)
    {
        var health = provider.GetRequiredService<HealthCheckService>();

        return await health.CheckHealthAsync(registration => registration.Tags.Contains(ReadyTag));
    }

    // The database check is the one that decides whether the instance can serve
    // traffic at all, so it is asserted directly rather than through the overall
    // status -- which also reflects the optional dependencies and is allowed to
    // be degraded without meaning the API is unusable.
    [RequiresDockerFact]
    public async Task Readiness_is_healthy_against_a_real_database()
    {
        await using var provider = BuildFor(_postgres.ConnectionString);

        var report = await CheckReadinessAsync(provider);

        Assert.Equal(HealthStatus.Healthy, report.Entries["postgres"].Status);
        Assert.NotEqual(HealthStatus.Unhealthy, report.Status);
    }

    // A database outage is the one dependency failure that must take the instance
    // out of the load balancer: without it the API cannot answer anything.
    [RequiresDockerFact]
    public async Task Readiness_is_unhealthy_when_the_database_cannot_be_reached()
    {
        await using var provider = BuildFor(
            "Host=127.0.0.1;Port=1;Database=ledger;Username=none;Password=none;Timeout=1");

        var report = await CheckReadinessAsync(provider);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["postgres"].Status);
    }

    // The check has to use the same DbContext the application serves requests
    // with. One that opened its own connection could report healthy while every
    // request failed, which is the failure mode a readiness probe exists to
    // prevent.
    [RequiresDockerFact]
    public async Task Readiness_checks_the_context_the_application_actually_uses()
    {
        await using var provider = BuildFor(_postgres.ConnectionString);

        var report = await CheckReadinessAsync(provider);

        var check = report.Entries["postgres"];
        Assert.Contains("db", check.Tags);

        // The database is the only dependency whose absence makes the instance
        // unable to do its job; the others are registered as degradable.
        Assert.Equal(
            ["postgres"],
            report.Entries
                .Where(entry => entry.Value.Tags.Contains("db"))
                .Select(entry => entry.Key));
    }

    // The deliberate decision, stated as a test: with the database healthy and
    // both optional dependencies gone, readiness degrades but does not fail, so
    // the instance keeps serving traffic. Redis is used by nothing, and a broker
    // outage only delays outbox publication -- neither is a reason to refuse
    // requests the instance can answer correctly.
    [RequiresDockerFact]
    public async Task Readiness_degrades_but_does_not_fail_when_only_the_optional_dependencies_are_down()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:LedgerDatabase"] = _postgres.ConnectionString,
                ["Redis:ConnectionString"] = "127.0.0.1:1,connectTimeout=500,abortConnect=false",
                ["RabbitMq:Host"] = "127.0.0.1",
                ["RabbitMq:Port"] = "1",
                ["Outbox:PublisherEnabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);

        await using var provider = services.BuildServiceProvider();

        var report = await CheckReadinessAsync(provider);

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["postgres"].Status);
        Assert.Equal(HealthStatus.Degraded, report.Entries["rabbitmq"].Status);
        Assert.Equal(HealthStatus.Degraded, report.Entries["redis"].Status);
    }

    // Liveness must never depend on the database, so nothing carrying the
    // readiness tag may be registered without one.
    [RequiresDockerFact]
    public async Task Nothing_untagged_is_registered_that_liveness_would_run()
    {
        await using var provider = BuildFor(_postgres.ConnectionString);

        var health = provider.GetRequiredService<HealthCheckService>();
        var liveness = await health.CheckHealthAsync(_ => false);

        Assert.Equal(HealthStatus.Healthy, liveness.Status);
        Assert.Empty(liveness.Entries);
    }
}
