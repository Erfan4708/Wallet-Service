using System.Diagnostics;
using System.Text.Json.Serialization;
using Ledger.Api.Endpoints;
using Ledger.Api.ErrorHandling;
using Ledger.Api.Observability;
using Ledger.Api.OpenApi;
using Ledger.Application;
using Ledger.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseLedgerSerilog();

// One error contract for the whole API. AddProblemDetails supplies the RFC 9457
// writer; the handler decides what each exception means.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        // Every error response carries the identifier of the request that
        // produced it, taken from the ambient Activity rather than from a
        // parallel correlation scheme. A caller can quote it, and it will find the
        // request in the logs and in the traces.
        var traceId = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
        context.ProblemDetails.Extensions["traceId"] = traceId;
    });

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Currencies travel as their ISO 4217 alpha code, never as an enum ordinal. A
// number on the wire would break silently the day a member is reordered, and
// means nothing to anyone reading a log.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// A request that cannot be read -- malformed JSON, an unknown currency code, a
// missing body -- is thrown to the exception handler in every environment, so it
// receives the same problem details, with the same trace identifier, as any other
// refused request. The framework's default outside Development is a bare 400 with
// no body at all.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

var openApiEnabled = builder.IsLedgerOpenApiEnabled();
if (openApiEnabled)
{
    builder.Services.AddLedgerOpenApi();
}

// The composition root, and the only place that knows every layer exists.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddLedgerObservability(builder.Configuration);

var app = builder.Build();

// A one-shot migration mode, run from the same image before the API starts.
// Letting every replica migrate on startup would have them racing to alter the
// same schema, which EF Core takes no lock to prevent.
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await app.Services.MigrateLedgerDatabaseAsync();
    Log.CloseAndFlush();

    return;
}

// Order matters, and this is the order that makes the logs honest.
//
// The trace header is outermost so it is attached to every response, including
// ones written by the exception handler. Request logging comes next, so it
// records the status the client actually received; inside the exception handler
// it would instead see the raw exception and report a refused withdrawal as a
// failed request. The exception handler is innermost of the three, closest to
// the endpoint whose exceptions it exists to translate.
app.UseTraceIdentifierHeader();
app.UseLedgerRequestLogging();
app.UseExceptionHandler();

if (openApiEnabled)
{
    // /swagger/v1/swagger.json and the UI at /swagger. See OpenApiExtensions.
    app.UseLedgerOpenApi();
}

app.MapLedgerHealthChecks();
app.MapLedgerEndpoints();

if (app.Services.GetRequiredService<IConfiguration>()
        .GetValue($"{ObservabilityOptions.SectionName}:Metrics:PrometheusEndpoint", defaultValue: true))
{
    // Serves /metrics in the text format Prometheus scrapes. The Prometheus
    // server and the Grafana dashboards that read it are defined in
    // docker-compose.yml; the application only exposes the endpoint.
    app.MapPrometheusScrapingEndpoint();
}

try
{
    app.Run();
}
finally
{
    // Serilog buffers; without this a crash can lose the events explaining it.
    Log.CloseAndFlush();
}

/// <summary>Exposed so the integration tests can host the application.</summary>
public partial class Program;
