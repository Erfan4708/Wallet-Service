namespace Ledger.Api.Observability;

/// <summary>
/// How much telemetry this deployment produces and where it goes.
/// </summary>
/// <remarks>
/// Bound from the <c>Observability</c> configuration section, so the same binary
/// runs with console exporters on a laptop and with none of them in production,
/// without a rebuild and without an environment name being hard-coded anywhere in
/// the source.
/// </remarks>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// The service identity every trace and metric is attributed to.
    /// </summary>
    /// <remarks>
    /// Not the deployment. Which cluster, region or instance produced a signal is
    /// something the environment knows and injects — through
    /// <c>OTEL_RESOURCE_ATTRIBUTES</c> or the platform's own enrichment — because
    /// baking it into the source means a rebuild to move the service.
    /// </remarks>
    public string ServiceName { get; set; } = "ledger-api";

    /// <summary>
    /// Overrides the version reported with the service identity. Normally left
    /// unset so the assembly's own version is used.
    /// </summary>
    public string? ServiceVersion { get; set; }

    public TracingOptions Tracing { get; set; } = new();

    public MetricsOptions Metrics { get; set; } = new();

    public sealed class TracingOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Writes finished spans to stdout. Useful on a laptop, ruinous in
        /// production — every span becomes a log line — so it defaults to off and
        /// development turns it on.
        /// </summary>
        public bool ConsoleExporter { get; set; }

        /// <summary>
        /// The share of requests sampled, from 0 to 1. One means every request,
        /// which is right for a service at this size and wrong for one under real
        /// load.
        /// </summary>
        public double SampleRatio { get; set; } = 1.0;
    }

    public sealed class MetricsOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Serves the Prometheus scrape endpoint. Metrics are still collected when
        /// this is off; they simply have nowhere to be read from.
        /// </summary>
        public bool PrometheusEndpoint { get; set; } = true;

        public bool ConsoleExporter { get; set; }
    }
}
