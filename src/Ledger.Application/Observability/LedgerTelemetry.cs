using System.Diagnostics;
using System.Diagnostics.Metrics;
using Ledger.Application.Exceptions;
using Ledger.Application.Ledger;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;

namespace Ledger.Application.Observability;

/// <summary>
/// The traces and metrics the ledger emits about itself.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately built on <see cref="ActivitySource"/> and <see cref="Meter"/>
/// from the base class library rather than on OpenTelemetry's own types. Those
/// are the .NET instrumentation primitives; OpenTelemetry is one possible
/// listener and it subscribes by name from the composition root. The application
/// layer therefore emits telemetry without taking a dependency on any telemetry
/// vendor, and a test can listen to exactly the same signals with nothing more
/// than a <see cref="MeterListener"/>.
/// </para>
/// <para>
/// It is injected rather than reached through a static field, so a test can
/// isolate one instance and nothing in the system depends on ambient global
/// state.
/// </para>
/// <para>
/// <b>Nothing here records an amount or a balance.</b> Traces carry identifiers
/// so a failure can be investigated; metrics carry only dimensions with a small,
/// fixed set of values. What money moved is in the ledger, which is the correct
/// and access-controlled place for it.
/// </para>
/// </remarks>
public sealed class LedgerTelemetry : IDisposable
{
    /// <summary>The name the composition root subscribes to for both signals.</summary>
    public const string SourceName = "Ledger.Application";

    private const string Success = "success";
    private const string Failure = "failure";
    private const string UnknownCurrency = "unknown";

    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<long> _transactions;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _accountsOpened;

    public LedgerTelemetry()
        : this(SourceName)
    {
    }

    /// <param name="sourceName">
    /// The name both signals publish under. Only a test passes anything but the
    /// default: an <see cref="ActivitySource"/> and a <see cref="Meter"/> are
    /// process-wide, so two tests running in parallel would otherwise observe each
    /// other's measurements and spans. Giving a test its own name is what makes
    /// what it observes unambiguously its own.
    /// </param>
    internal LedgerTelemetry(string sourceName)
    {
        var version = typeof(LedgerTelemetry).Assembly.GetName().Version?.ToString();

        _activitySource = new ActivitySource(sourceName, version);
        _meter = new Meter(sourceName, version);

        _transactions = _meter.CreateCounter<long>(
            "ledger.transactions",
            unit: "{transaction}",
            description: "Ledger transactions attempted, by operation, currency and outcome.");

        _failures = _meter.CreateCounter<long>(
            "ledger.transaction.failures",
            unit: "{transaction}",
            description: "Ledger transactions refused, by operation and the reason they were refused.");

        _duration = _meter.CreateHistogram<double>(
            "ledger.transaction.duration",
            unit: "s",
            description: "Wall-clock time taken by a ledger operation, including waiting for row locks.");

        _accountsOpened = _meter.CreateCounter<long>(
            "ledger.accounts.opened",
            unit: "{account}",
            description: "Accounts opened, by currency.");
    }

    /// <summary>
    /// Runs a ledger operation as one span, and records its outcome and duration.
    /// </summary>
    /// <remarks>
    /// Centralised rather than repeated in each handler so that six use cases
    /// cannot drift into six slightly different definitions of "a failure", and
    /// so that adding a seventh cannot forget to measure itself.
    /// </remarks>
    /// <param name="knownCurrency">
    /// The currency when the request names one. A reversal does not: which
    /// currency it moves is only known once the original transaction has been
    /// loaded, so it passes <see langword="null"/> and the currency is taken from
    /// the result instead.
    /// </param>
    public async Task<LedgerTransactionResult> TrackTransactionAsync(
        LedgerTransactionKind kind,
        Currency? knownCurrency,
        Func<Activity?, Task<LedgerTransactionResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var operationName = OperationTag(kind);
        var currencyName = knownCurrency is { } named ? CurrencyTag(named) : UnknownCurrency;

        using var activity = _activitySource.StartActivity($"ledger.{operationName}", ActivityKind.Internal);
        activity?.SetTag("ledger.operation", operationName);

        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await operation(activity);

            if (knownCurrency is null)
            {
                currencyName = CurrencyTag(result.Currency);
            }

            activity?.SetTag("ledger.currency", currencyName);
            Record(operationName, currencyName, Success, started);

            return result;
        }
        catch (Exception exception)
        {
            var reason = FailureReason(exception);

            activity?.SetStatus(ActivityStatusCode.Error, reason);
            activity?.SetTag("ledger.currency", currencyName);
            activity?.SetTag("ledger.failure_reason", reason);

            Record(operationName, currencyName, Failure, started);
            _failures.Add(1,
                new KeyValuePair<string, object?>("ledger.operation", operationName),
                new KeyValuePair<string, object?>("ledger.failure_reason", reason));

            throw;
        }
    }

    /// <summary>Starts a span for an operation that does not move money.</summary>
    public Activity? StartActivity(string name) =>
        _activitySource.StartActivity(name, ActivityKind.Internal);

    public void RecordAccountOpened(Currency currency) =>
        _accountsOpened.Add(1, new KeyValuePair<string, object?>("ledger.currency", CurrencyTag(currency)));

    private void Record(string operation, string currency, string outcome, long startedAt)
    {
        _transactions.Add(1,
            new KeyValuePair<string, object?>("ledger.operation", operation),
            new KeyValuePair<string, object?>("ledger.currency", currency),
            new KeyValuePair<string, object?>("ledger.outcome", outcome));

        _duration.Record(
            Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
            new KeyValuePair<string, object?>("ledger.operation", operation),
            new KeyValuePair<string, object?>("ledger.outcome", outcome));
    }

    private static string OperationTag(LedgerTransactionKind kind) => kind switch
    {
        LedgerTransactionKind.Deposit => "deposit",
        LedgerTransactionKind.Withdrawal => "withdrawal",
        LedgerTransactionKind.Transfer => "transfer",
        LedgerTransactionKind.Reversal => "reversal",
        _ => "unknown",
    };

    /// <remarks>
    /// An undefined currency reaches here whenever a client sends one, and its
    /// <c>ToString</c> is the number they sent. Passing that through would make a
    /// metric label out of arbitrary input, which is how a caller accidentally —
    /// or deliberately — creates an unbounded number of time series and takes the
    /// metrics backend down. Anything the system does not recognise is collapsed
    /// into a single bucket.
    /// </remarks>
    private static string CurrencyTag(Currency currency) =>
        Enum.IsDefined(currency) ? currency.ToString() : UnknownCurrency;

    /// <remarks>
    /// A closed set, for the same reason: a reason label derived from an
    /// exception message would carry identifiers and amounts straight into the
    /// metrics backend.
    /// </remarks>
    private static string FailureReason(Exception exception) => exception switch
    {
        ValidationException => "validation",
        NotFoundException => "not_found",
        ConflictException => "conflict",
        InsufficientFundsException => "insufficient_funds",
        CurrencyMismatchException => "currency_mismatch",
        SameAccountTransferException => "same_account",
        AccountTypeMismatchException => "account_type",
        TransactionAlreadyReversedException => "already_reversed",
        UnbalancedTransactionException => "unbalanced",
        DomainException => "domain_rule",
        OperationCanceledException => "cancelled",
        _ => "error",
    };

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }
}
