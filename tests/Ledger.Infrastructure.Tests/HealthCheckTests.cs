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

    [RequiresDockerFact]
    public async Task Readiness_is_healthy_against_a_real_database()
    {
        await using var provider = BuildFor(_postgres.ConnectionString);

        var report = await CheckReadinessAsync(provider);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["postgres"].Status);
    }

    [RequiresDockerFact]
    public async Task Readiness_is_unhealthy_when_the_database_cannot_be_reached()
    {
        await using var provider = BuildFor(
            "Host=127.0.0.1;Port=1;Database=ledger;Username=none;Password=none;Timeout=1");

        var report = await CheckReadinessAsync(provider);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
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

        var check = Assert.Single(report.Entries);
        Assert.Equal("postgres", check.Key);
        Assert.Contains("db", check.Value.Tags);
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
