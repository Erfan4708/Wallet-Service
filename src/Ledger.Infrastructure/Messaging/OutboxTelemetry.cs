using System.Diagnostics.Metrics;

namespace Ledger.Infrastructure.Messaging;

/// <summary>
/// What the outbox publisher reports about the backlog it is draining.
/// </summary>
/// <remarks>
/// <para>
/// Three numbers answer the questions an outbox raises under load: how many
/// messages are waiting, how fast they are leaving, and how often leaving fails.
/// None carries a label, because none needs one — there is one outbox and one
/// message type — and an unlabelled series cannot grow.
/// </para>
/// <para>
/// The backlog is a cached value, refreshed by the publisher loop rather than
/// queried when Prometheus scrapes. A gauge callback that ran a database query
/// would tie scrape latency to database latency, and a slow database would then
/// make metrics disappear exactly when they were needed.
/// </para>
/// </remarks>
internal sealed class OutboxTelemetry : IDisposable
{
    public const string MeterName = "Ledger.Infrastructure.Outbox";

    private readonly Meter _meter;
    private readonly Counter<long> _published;
    private readonly Counter<long> _failures;

    // -1 until the first refresh, so the gauge reports nothing rather than a zero
    // it has not actually observed.
    private long _pending = -1;

    public OutboxTelemetry()
        : this(MeterName)
    {
    }

    internal OutboxTelemetry(string meterName)
    {
        _meter = new Meter(meterName);

        _published = _meter.CreateCounter<long>(
            "ledger.outbox.published",
            unit: "{message}",
            description: "Outbox messages confirmed by the broker and marked published.");

        _failures = _meter.CreateCounter<long>(
            "ledger.outbox.publish_failures",
            unit: "{message}",
            description: "Publication attempts that failed and left the message pending for retry.");

        _meter.CreateObservableGauge(
            "ledger.outbox.pending",
            ObservePending,
            unit: "{message}",
            description: "Outbox messages not yet published, as of the publisher's last refresh.");
    }

    public void RecordPublished() => _published.Add(1);

    public void RecordFailure() => _failures.Add(1);

    public void SetPending(long pending) => Interlocked.Exchange(ref _pending, pending);

    private IEnumerable<Measurement<long>> ObservePending()
    {
        var pending = Interlocked.Read(ref _pending);

        return pending < 0 ? [] : [new Measurement<long>(pending)];
    }

    public void Dispose() => _meter.Dispose();
}
