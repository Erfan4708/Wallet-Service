using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledger.Api.Tests.Observability;

/// <summary>
/// Hosts the real application with a database that cannot be reached.
/// </summary>
/// <remarks>
/// Deliberately unreachable, because that is the interesting case. Liveness must
/// still succeed — the process is fine, and restarting it would not bring the
/// database back — while readiness must fail, so the instance is taken out of the
/// load balancer and left running. Getting these two the wrong way round turns a
/// database outage into a cluster-wide crash loop, which is why it is worth a
/// test rather than a comment.
/// </remarks>
public sealed class UnreachableDatabaseFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // These tests exercise endpoints, not publishing. Left on, the background
        // outbox publisher would log a connection failure every few seconds against
        // the deliberately unreachable database.
        builder.UseSetting("Outbox:PublisherEnabled", "false");

        // UseSetting rather than ConfigureAppConfiguration: the application reads
        // its connection string while composing services, which happens before
        // the configuration callbacks a factory adds would run. A setting is
        // applied to the host's configuration before the entry point executes,
        // which is early enough.
        //
        // Port 1 accepts nothing. The short timeouts keep a readiness probe from
        // outliving the test waiting on it.
        builder.UseSetting(
            "ConnectionStrings:LedgerDatabase",
            "Host=127.0.0.1;Port=1;Database=ledger;Username=none;Password=none;Timeout=1;Command Timeout=1");
    }
}

public class ObservabilityEndpointTests : IClassFixture<UnreachableDatabaseFactory>
{
    private readonly UnreachableDatabaseFactory _factory;

    public ObservabilityEndpointTests(UnreachableDatabaseFactory factory) => _factory = factory;

    // ------------------------------------------------------------- liveness

    [Fact]
    public async Task Liveness_succeeds_even_though_the_database_is_unreachable()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_checks_nothing_external()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        var report = JsonDocument.Parse(body).RootElement;

        Assert.Equal("Healthy", report.GetProperty("status").GetString());
        Assert.Empty(report.GetProperty("checks").EnumerateObject());
    }

    // ------------------------------------------------------------ readiness

    [Fact]
    public async Task Readiness_fails_when_the_database_is_unreachable()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_names_the_failing_check_without_explaining_it()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("postgres", body, StringComparison.Ordinal);

        // A health endpoint is usually the least protected surface a service has.
        // Which check failed is useful; the host, the credentials and the driver's
        // exception belong in the log.
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- metrics

    [Fact]
    public async Task The_metrics_endpoint_is_served()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/metrics", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_metrics_endpoint_speaks_the_prometheus_text_format()
    {
        using var client = _factory.CreateClient();

        // Something has to have been measured before there is anything to scrape.
        using var _ = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var response = await client.GetAsync(new Uri("/metrics", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("# HELP", body, StringComparison.Ordinal);
        Assert.Contains("# TYPE", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_metrics_endpoint_exposes_no_identifier_or_credential()
    {
        using var client = _factory.CreateClient();

        using var _ = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var response = await client.GetAsync(new Uri("/metrics", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- correlation

    // Proof that ASP.NET Core created an Activity for the request and that its
    // identifier reached the response. A 32-character hexadecimal value is a W3C
    // trace id; the framework's fallback TraceIdentifier looks nothing like one.
    [Fact]
    public async Task Every_response_carries_the_trace_identifier()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.True(response.Headers.TryGetValues("trace-id", out var values));

        var traceId = Assert.Single(values!);
        Assert.Equal(32, traceId.Length);
        Assert.True(traceId.All(Uri.IsHexDigit));
    }

    [Fact]
    public async Task Two_requests_are_given_different_trace_identifiers()
    {
        using var client = _factory.CreateClient();

        using var first = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var second = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.NotEqual(
            first.Headers.GetValues("trace-id").Single(),
            second.Headers.GetValues("trace-id").Single());
    }

    // --------------------------------------------------------- error bodies

    [Fact]
    public async Task A_failed_request_returns_problem_details_carrying_the_trace_identifier()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/accounts/11111111-1111-1111-1111-111111111111", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        var problem = JsonDocument.Parse(body).RootElement;
        var traceId = problem.GetProperty("traceId").GetString();

        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.Equal(response.Headers.GetValues("trace-id").Single(), traceId);
    }

    // The database is unreachable, so this request fails inside the driver. What
    // comes back must say nothing about that.
    [Fact]
    public async Task An_internal_failure_leaks_nothing_to_the_client()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/accounts/11111111-1111-1111-1111-111111111111", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Ledger.Infrastructure", body, StringComparison.Ordinal);
    }

    // Phase 2's contract, restated: adding observability must not have changed
    // what a client receives for an ordinary bad request.
    [Fact]
    public async Task Problem_details_for_a_malformed_request_are_unchanged()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/accounts/00000000-0000-0000-0000-000000000000", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Validation failed.", problem.GetProperty("title").GetString());
        Assert.True(problem.TryGetProperty("errors", out _));
    }
}
