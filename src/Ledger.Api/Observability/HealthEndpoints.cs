using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Api.Observability;

/// <summary>
/// The two questions an orchestrator asks, which are not the same question.
/// </summary>
/// <remarks>
/// <para>
/// <b>Liveness</b> asks whether the process is still working, and its only
/// correct answer is about the process. It deliberately checks nothing external:
/// a probe that fails because PostgreSQL is unreachable would have the
/// orchestrator kill and restart every instance of the service, removing the
/// capacity that would have served traffic the moment the database came back, and
/// turning a database outage into a crash loop.
/// </para>
/// <para>
/// <b>Readiness</b> asks whether this instance can serve a request right now,
/// which does depend on the database. An instance that cannot reach PostgreSQL
/// should be taken out of the load balancer and left running.
/// </para>
/// </remarks>
internal static class HealthEndpoints
{
    internal const string LivePath = "/health/live";
    internal const string ReadyPath = "/health/ready";

    private const string ReadyTag = "ready";

    internal static void MapLedgerHealthChecks(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // No checks run: reaching this handler at all is the proof.
        app.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteStatusAsync,
        });

        app.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteStatusAsync,
        });
    }

    /// <summary>
    /// Writes the overall status, the name of each check, and nothing else.
    /// </summary>
    /// <remarks>
    /// The default writer returns exception messages from failed checks, which for
    /// a database check means the connection string, the host and the credentials
    /// it tried. Health endpoints are usually the least protected surface a
    /// service exposes, so the response says which check failed and leaves why it
    /// failed in the logs, where access is controlled.
    /// </remarks>
    private static async Task WriteStatusAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Status.ToString()),
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
