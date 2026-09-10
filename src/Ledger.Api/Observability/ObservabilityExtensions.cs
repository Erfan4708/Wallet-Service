using System.Diagnostics;
using System.Reflection;
using Ledger.Application.Observability;
using Ledger.Infrastructure.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace Ledger.Api.Observability;

/// <summary>
/// Wires logging, tracing and metrics into the host.
/// </summary>
/// <remarks>
/// All of it lives at the composition root. The layers below emit signals through
/// base class library primitives — <see cref="ActivitySource"/>,
/// <see cref="System.Diagnostics.Metrics.Meter"/>, <c>ILogger</c> — and this file
/// is the only place that decides who listens and where the output goes. That is
/// what keeps OpenTelemetry and Serilog out of the domain and the application
/// layer entirely.
/// </remarks>
internal static class ObservabilityExtensions
{
    private const string TraceHeader = "trace-id";

    /// <summary>
    /// Configures Serilog as the host's logger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serilog rather than the built-in logger because a financial service is
    /// investigated by querying logs, not by reading them: "every refused
    /// withdrawal on this account today" has to be a filter, which needs the
    /// properties of a log event to survive as data rather than being flattened
    /// into a sentence. The built-in console logger renders a message and loses
    /// the structure.
    /// </para>
    /// <para>
    /// Configuration comes from the <c>Serilog</c> section, so minimum levels and
    /// sinks are deployment decisions rather than compiled-in ones.
    /// </para>
    /// </remarks>
    internal static void UseLedgerSerilog(this IHostBuilder host) =>
        host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            // TraceId and SpanId come from Activity.Current, which ASP.NET Core
            // has already populated from the incoming traceparent header. Logs and
            // traces therefore share identifiers without a bespoke correlation
            // mechanism competing with the standard one.
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service.name", ServiceNameOf(context.Configuration)));

    /// <summary>
    /// Logs one event per request instead of the framework's several.
    /// </summary>
    /// <remarks>
    /// A single completion event per request carries the method, path, status and
    /// elapsed time as properties, which is what makes latency and error rate
    /// answerable from the logs. Health and metrics endpoints are dropped to a
    /// level that is off by default: a probe every few seconds would otherwise be
    /// the overwhelming majority of the log volume and would say nothing.
    /// </remarks>
    internal static void UseLedgerRequestLogging(this IApplicationBuilder app) =>
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

            options.GetLevel = (httpContext, _, exception) =>
                exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Error
                    : IsProbe(httpContext.Request.Path)
                        ? LogEventLevel.Verbose
                        : LogEventLevel.Information;

            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                // Deliberately not the query string and not the body: one carries
                // whatever a caller chose to put in a URL, the other carries
                // amounts. The route template is the shape of the request without
                // the values in it.
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                diagnosticContext.Set("RouteTemplate", RouteTemplateOf(httpContext));
            };
        });

    /// <summary>Registers tracing and metrics collection.</summary>
    internal static IServiceCollection AddLedgerObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ObservabilityOptions>(configuration.GetSection(ObservabilityOptions.SectionName));

        var options = configuration
            .GetSection(ObservabilityOptions.SectionName)
            .Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        var builder = services.AddOpenTelemetry().ConfigureResource(resource => resource
            .AddService(
                serviceName: options.ServiceName,
                serviceVersion: options.ServiceVersion ?? AssemblyVersion(),
                serviceInstanceId: Environment.MachineName)
            // Lets the deployment add its own attributes through
            // OTEL_RESOURCE_ATTRIBUTES, so which cluster or region produced a
            // signal is the environment's business rather than the source's.
            .AddEnvironmentVariableDetector());

        if (options.Tracing.Enabled)
        {
            builder.WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.Tracing.SampleRatio)))
                    .AddSource(LedgerTelemetry.SourceName)
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.RecordException = true;

                        // Probes are the loudest and least interesting traffic a
                        // service receives.
                        instrumentation.Filter = context => !IsProbe(context.Request.Path);
                    })
                    .AddHttpClientInstrumentation()
                    .AddLedgerPersistenceInstrumentation();

                if (options.Tracing.ConsoleExporter)
                {
                    tracing.AddConsoleExporter();
                }
            });
        }

        if (options.Metrics.Enabled)
        {
            builder.WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(LedgerTelemetry.SourceName)
                    // The SDK's default boundaries are shaped for milliseconds,
                    // so a metric recorded in seconds lands almost entirely in
                    // the first bucket and the histogram answers nothing. These
                    // are the boundaries OpenTelemetry uses for HTTP duration,
                    // which is the same order of magnitude as a ledger write.
                    .AddView(
                        instrumentName: "ledger.transaction.duration",
                        new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries =
                            [
                                0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25,
                                0.5, 0.75, 1, 2.5, 5, 7.5, 10,
                            ],
                        })
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (options.Metrics.PrometheusEndpoint)
                {
                    metrics.AddPrometheusExporter();
                }

                if (options.Metrics.ConsoleExporter)
                {
                    metrics.AddConsoleExporter();
                }
            });
        }

        return services;
    }

    /// <summary>
    /// Returns the current trace identifier on every response.
    /// </summary>
    /// <remarks>
    /// The value comes from <see cref="Activity.Current"/> — the identifier
    /// ASP.NET Core already created and that logs and spans already carry — rather
    /// than from a correlation scheme of our own. A caller reporting a problem can
    /// quote this header, and it will find the request in both the logs and the
    /// traces.
    /// </remarks>
    internal static IApplicationBuilder UseTraceIdentifierHeader(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            // Registered on starting, because headers cannot be added once the
            // response has begun.
            context.Response.OnStarting(() =>
            {
                var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
                context.Response.Headers[TraceHeader] = traceId;

                return Task.CompletedTask;
            });

            await next(context);
        });
    }

    /// <summary>
    /// Whether a path is one of the endpoints that exist to be polled.
    /// </summary>
    internal static bool IsProbe(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);

    private static string ServiceNameOf(IConfiguration configuration) =>
        configuration[$"{ObservabilityOptions.SectionName}:{nameof(ObservabilityOptions.ServiceName)}"]
        ?? "ledger-api";

    private static string AssemblyVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private static string? RouteTemplateOf(HttpContext httpContext) =>
        httpContext.GetEndpoint() is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText
            : null;
}
