using System.Diagnostics;
using System.Diagnostics.Metrics;
using Ledger.Application.Exceptions;
using Ledger.Application.Ledger;
using Ledger.Application.Observability;
using Ledger.Application.Tests.Fakes;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;

namespace Ledger.Application.Tests.Observability;

/// <summary>
/// What the ledger says about itself while it works.
/// </summary>
/// <remarks>
/// Listened to with <see cref="MeterListener"/> and <see cref="ActivityListener"/>
/// — the same base class library mechanisms OpenTelemetry itself uses. Nothing
/// here references a telemetry vendor, which is the point: the application layer
/// emits signals that any listener can read, and these tests read them directly.
/// </remarks>
public sealed class LedgerTelemetryTests : IDisposable
{
    private static readonly Guid WalletId = new("33333333-0000-0000-0000-000000000001");

    // A private source name per test instance: the spans and measurements this
    // test observes are then unambiguously the ones it caused, even while other
    // test classes are running in parallel against the real source.
    private readonly string _sourceName = $"{LedgerTelemetry.SourceName}.Test.{Guid.NewGuid():N}";
    private readonly LedgerTelemetry _telemetry;
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeLedgerTransactionRepository _transactions = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private DepositHandler Deposit =>
        new(_accounts, _transactions, _unitOfWork, TimeProvider.System, _telemetry);

    private WithdrawHandler Withdraw =>
        new(_accounts, _transactions, _unitOfWork, TimeProvider.System, _telemetry);

    public LedgerTelemetryTests()
    {
        _telemetry = new LedgerTelemetry(_sourceName);
        _accounts.Seed(LedgerScenario.Settlement(Currency.USD));
    }

    public void Dispose() => _telemetry.Dispose();

    private void SeedWallet(decimal funding = 0m)
    {
        var settlement = _accounts.Find(LedgerScenario.SettlementId(Currency.USD))!;
        _accounts.Seed(LedgerScenario.FundedWallet(WalletId, Currency.USD, funding, settlement));
    }

    // ------------------------------------------------------------- metrics

    [Fact]
    public async Task A_successful_deposit_is_counted()
    {
        SeedWallet();
        using var meter = new RecordedMeasurements(_sourceName);

        await Deposit.HandleAsync(new DepositCommand(Guid.NewGuid(), WalletId, 100m, Currency.USD));

        var measurement = Assert.Single(meter.For("ledger.transactions"));

        Assert.Equal(1L, measurement.Value);
        Assert.Equal("deposit", measurement.Tag("ledger.operation"));
        Assert.Equal("USD", measurement.Tag("ledger.currency"));
        Assert.Equal("success", measurement.Tag("ledger.outcome"));
    }

    [Fact]
    public async Task A_successful_deposit_is_timed()
    {
        SeedWallet();
        using var meter = new RecordedMeasurements(_sourceName);

        await Deposit.HandleAsync(new DepositCommand(Guid.NewGuid(), WalletId, 100m, Currency.USD));

        var measurement = Assert.Single(meter.For("ledger.transaction.duration"));

        Assert.True(Convert.ToDouble(measurement.Value) >= 0d);
        Assert.Equal("deposit", measurement.Tag("ledger.operation"));
    }

    // A refused request is still a request the system handled, so it is counted as
    // an attempt with a failed outcome as well as against its reason.
    [Fact]
    public async Task A_refused_withdrawal_is_counted_as_a_failure_with_its_reason()
    {
        SeedWallet(funding: 10m);
        using var meter = new RecordedMeasurements(_sourceName);

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            Withdraw.HandleAsync(new WithdrawCommand(Guid.NewGuid(), WalletId, 500m, Currency.USD)));

        var attempt = Assert.Single(meter.For("ledger.transactions"));
        Assert.Equal("failure", attempt.Tag("ledger.outcome"));

        var failure = Assert.Single(meter.For("ledger.transaction.failures"));
        Assert.Equal("withdrawal", failure.Tag("ledger.operation"));
        Assert.Equal("insufficient_funds", failure.Tag("ledger.failure_reason"));
    }

    [Fact]
    public async Task A_validation_failure_is_counted_before_anything_is_locked()
    {
        using var meter = new RecordedMeasurements(_sourceName);

        await Assert.ThrowsAsync<ValidationException>(() =>
            Deposit.HandleAsync(new DepositCommand(Guid.Empty, WalletId, 100m, Currency.USD)));

        var failure = Assert.Single(meter.For("ledger.transaction.failures"));
        Assert.Equal("validation", failure.Tag("ledger.failure_reason"));
    }

    // The label that would otherwise be arbitrary caller input. A client sending
    // an unknown currency must not be able to create a new time series, which is
    // how a metrics backend is taken down by accident or on purpose.
    [Fact]
    public async Task An_unrecognised_currency_never_becomes_its_own_label()
    {
        using var meter = new RecordedMeasurements(_sourceName);

        await Assert.ThrowsAsync<ValidationException>(() =>
            Deposit.HandleAsync(new DepositCommand(Guid.NewGuid(), WalletId, 100m, (Currency)9999)));

        var attempt = Assert.Single(meter.For("ledger.transactions"));

        Assert.Equal("unknown", attempt.Tag("ledger.currency"));
        Assert.DoesNotContain("9999", attempt.Tag("ledger.currency"));
    }

    // Metric labels must never carry an identifier: one series per account or per
    // transaction is unbounded growth.
    [Fact]
    public async Task No_metric_carries_an_identifier_or_an_amount()
    {
        SeedWallet();
        using var meter = new RecordedMeasurements(_sourceName);

        await Deposit.HandleAsync(new DepositCommand(Guid.NewGuid(), WalletId, 100m, Currency.USD));

        var allowed = new[]
        {
            "ledger.operation", "ledger.currency", "ledger.outcome", "ledger.failure_reason",
        };

        Assert.All(meter.All, measurement =>
            Assert.All(measurement.Tags.Keys, key => Assert.Contains(key, allowed)));
    }

    [Fact]
    public void Opening_an_account_is_counted_by_currency()
    {
        using var meter = new RecordedMeasurements(_sourceName);

        _telemetry.RecordAccountOpened(Currency.EUR);

        var measurement = Assert.Single(meter.For("ledger.accounts.opened"));

        Assert.Equal(1L, measurement.Value);
        Assert.Equal("EUR", measurement.Tag("ledger.currency"));
    }

    // -------------------------------------------------------------- traces

    [Fact]
    public async Task A_deposit_produces_a_span_naming_the_operation()
    {
        SeedWallet();
        using var traces = new RecordedActivities(_sourceName);

        await Deposit.HandleAsync(new DepositCommand(Guid.NewGuid(), WalletId, 100m, Currency.USD));

        var activity = Assert.Single(traces.All);

        Assert.Equal("ledger.deposit", activity.OperationName);
        Assert.Equal("deposit", activity.GetTagItem("ledger.operation"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
    }

    // A span carries identifiers, because investigating a failure means finding
    // the account it happened to. It does not carry the amount: a trace backend is
    // a wider audience than the ledger itself.
    [Fact]
    public async Task A_span_carries_identifiers_but_no_amount()
    {
        SeedWallet();
        var transactionId = Guid.NewGuid();
        using var traces = new RecordedActivities(_sourceName);

        await Deposit.HandleAsync(new DepositCommand(transactionId, WalletId, 123.45m, Currency.USD));

        var activity = Assert.Single(traces.All);

        Assert.Equal(WalletId, activity.GetTagItem("ledger.account_id"));
        Assert.Equal(transactionId, activity.GetTagItem("ledger.transaction_id"));
        Assert.DoesNotContain(activity.TagObjects, tag =>
            tag.Value?.ToString()?.Contains("123.45", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_refused_operation_marks_its_span_as_failed()
    {
        SeedWallet(funding: 1m);
        using var traces = new RecordedActivities(_sourceName);

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            Withdraw.HandleAsync(new WithdrawCommand(Guid.NewGuid(), WalletId, 500m, Currency.USD)));

        var activity = Assert.Single(traces.All);

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("insufficient_funds", activity.GetTagItem("ledger.failure_reason"));
    }

    // ------------------------------------------------------------ listeners

    private sealed record Measurement(string Instrument, object Value, Dictionary<string, object?> Tags)
    {
        internal string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    /// <summary>Collects everything the ledger's meter publishes while it lives.</summary>
    private sealed class RecordedMeasurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Measurement> _measurements = [];

        internal RecordedMeasurements(string sourceName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == sourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>(
                (instrument, value, tags, _) => Add(instrument, value, tags));

            _listener.Start();
        }

        internal IReadOnlyList<Measurement> All => _measurements;

        internal IEnumerable<Measurement> For(string instrument) =>
            _measurements.Where(measurement => measurement.Instrument == instrument);

        private void Add<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct
        {
            var copied = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copied[tag.Key] = tag.Value;
            }

            _measurements.Add(new Measurement(instrument.Name, value, copied));
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Collects the spans the ledger finishes while it lives.</summary>
    private sealed class RecordedActivities : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _activities = [];

        internal RecordedActivities(string sourceName)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _activities.Add,
            };

            ActivitySource.AddActivityListener(_listener);
        }

        internal IReadOnlyList<Activity> All => _activities;

        public void Dispose() => _listener.Dispose();
    }
}
