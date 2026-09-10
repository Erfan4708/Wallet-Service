using Ledger.Api.ErrorHandling;
using Ledger.Application.Exceptions;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;
using Ledger.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ledger.Api.Tests.ErrorHandling;

/// <summary>
/// How loudly each kind of failure is recorded.
/// </summary>
/// <remarks>
/// The distinction this file protects is operational, not cosmetic. If a refused
/// withdrawal is logged as an error, then the error rate of the service is
/// dominated by customers spending money they do not have, and the one genuine
/// failure in a million requests is invisible. Expected outcomes are counted as
/// metrics; only defects are logged as errors.
/// </remarks>
public class ExceptionLoggingTests
{
    private static readonly Guid AccountId = new("44444444-0000-0000-0000-000000000001");

    private static async Task<(RecordingLogger Logger, HttpContext Context)> HandleAsync(Exception exception)
    {
        var logger = new RecordingLogger();
        var context = new DefaultHttpContext();
        var handler = new GlobalExceptionHandler(new StubProblemDetailsService(), logger);

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        return (logger, context);
    }

    [Fact]
    public async Task An_unexpected_failure_is_logged_as_an_error_with_the_exception()
    {
        var failure = new InvalidOperationException("the connection pool is exhausted");

        var (logger, context) = await HandleAsync(failure);

        var entry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);

        // The exception object itself, so the stack trace and any inner database
        // error survive into the log. This is the only copy: the client is told
        // nothing.
        Assert.Same(failure, entry.Exception);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ExpectedFailures))]
    public async Task An_expected_failure_is_not_logged_as_an_error(Exception exception)
    {
        var (logger, _) = await HandleAsync(exception);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Theory]
    [MemberData(nameof(ExpectedFailures))]
    public async Task An_expected_failure_is_still_recorded_quietly(Exception exception)
    {
        var (logger, _) = await HandleAsync(exception);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);
    }

    // The message of a domain failure quotes balances and amounts. It is correct
    // to return it to the account holder, and wrong to write it into a log that a
    // much wider audience can read.
    [Fact]
    public async Task A_refusal_is_logged_without_the_amounts_it_mentions()
    {
        var exception = new InsufficientFundsException(
            AccountId, new Money(100m, Currency.USD), new Money(500m, Currency.USD));

        var (logger, _) = await HandleAsync(exception);

        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain("100.00", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("500.00", entry.Message, StringComparison.Ordinal);
        });
    }

    // Phase 3's behaviour, restated here so that adding logging cannot quietly
    // change what a client receives.
    [Theory]
    [InlineData(typeof(NotFoundException), StatusCodes.Status404NotFound)]
    [InlineData(typeof(ConflictException), StatusCodes.Status409Conflict)]
    [InlineData(typeof(InsufficientFundsException), StatusCodes.Status422UnprocessableEntity)]
    [InlineData(typeof(InvalidOperationException), StatusCodes.Status500InternalServerError)]
    public async Task The_status_code_for_each_failure_is_unchanged(Type exceptionType, int expected)
    {
        var (_, context) = await HandleAsync(ExampleOf(exceptionType));

        Assert.Equal(expected, context.Response.StatusCode);
    }

    public static TheoryData<Exception> ExpectedFailures() => new()
    {
        new NotFoundException("Account", AccountId),
        new ConflictException("Account already exists."),
        new ValidationException("Amount", "The amount must be greater than zero."),
        new InsufficientFundsException(AccountId, Money.Zero(Currency.USD), new Money(10m, Currency.USD)),
        new SameAccountTransferException(AccountId),
        new TransactionAlreadyReversedException(AccountId),
    };

    private static Exception ExampleOf(Type exceptionType) => exceptionType switch
    {
        _ when exceptionType == typeof(NotFoundException) => new NotFoundException("Account", AccountId),
        _ when exceptionType == typeof(ConflictException) => new ConflictException("Already exists."),
        _ when exceptionType == typeof(InsufficientFundsException) =>
            new InsufficientFundsException(AccountId, Money.Zero(Currency.USD), new Money(1m, Currency.USD)),
        _ => new InvalidOperationException("boom"),
    };

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class RecordingLogger : ILogger<GlobalExceptionHandler>
    {
        internal List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class StubProblemDetailsService : IProblemDetailsService
    {
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(true);

        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;
    }
}
