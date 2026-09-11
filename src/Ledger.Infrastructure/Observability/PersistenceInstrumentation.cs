using Ledger.Infrastructure.Messaging;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Ledger.Infrastructure.Observability;

/// <summary>
/// Subscribes tracing to the database client.
/// </summary>
/// <remarks>
/// This lives in the infrastructure layer so that the composition root can turn
/// database tracing on without knowing which database it is. Npgsql publishes its
/// own spans, one per command, carrying the statement's shape and timing; nesting
/// them inside the request and ledger spans is what turns "this request was slow"
/// into "this request spent its time waiting for a row lock".
/// </remarks>
public static class PersistenceInstrumentation
{
    /// <summary>
    /// The meter the database driver publishes connection-pool and command
    /// metrics under.
    /// </summary>
    public const string DatabaseClientMeterName = "Npgsql";

    /// <summary>Records a span for every database command.</summary>
    /// <remarks>
    /// Npgsql records the command text but not the parameter values bound to it,
    /// so the account identifiers and amounts a statement carries never reach the
    /// trace. That default is deliberate and is not changed here.
    /// </remarks>
    public static TracerProviderBuilder AddLedgerPersistenceInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddNpgsql();
    }

    /// <summary>Collects the outbox backlog, publication and failure metrics.</summary>
    public static MeterProviderBuilder AddLedgerOutboxInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddMeter(OutboxTelemetry.MeterName);
    }
}
