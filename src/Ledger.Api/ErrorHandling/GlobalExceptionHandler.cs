using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace Ledger.Api.ErrorHandling;

/// <summary>
/// Turns any unhandled exception into a consistent problem details response.
/// </summary>
/// <remarks>
/// <para>
/// Uses the built-in <see cref="IExceptionHandler"/> pipeline rather than custom
/// middleware or per-endpoint try/catch. Handling errors in one place is what
/// makes the error contract consistent: an endpoint added later is covered
/// without its author having to remember anything.
/// </para>
/// <para>
/// Note the asymmetry between what is logged and what is returned. The full
/// exception, stack trace included, goes to the log, where operators can see it.
/// The client gets a status code, a title and — for errors it can act on — a
/// message. Stack traces and exception types describe the internals of the
/// service and are useful to an attacker mapping it, so they never cross the
/// boundary.
/// </para>
/// <para>
/// <b>This handler is the only thing that logs exceptions.</b> ASP.NET Core's
/// own <c>ExceptionHandlerMiddleware</c> logs every exception it routes here at
/// <c>Error</c>, with a stack trace, before this code decides what the exception
/// means — so a refused withdrawal and a missing account would both be recorded
/// as incidents. That logger is silenced in configuration precisely because this
/// one replaces it: unexpected failures are still logged at <c>Error</c> with the
/// whole exception, and expected ones are recorded as the outcomes they are.
/// </para>
/// </remarks>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<GlobalExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(problemDetailsService);
        ArgumentNullException.ThrowIfNull(logger);

        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var problemDetails = ExceptionMapping.ToProblemDetails(exception);
        var status = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        if (status >= StatusCodes.Status500InternalServerError)
        {
            // The whole exception, including the stack trace and any inner
            // database error, goes to the log. This is the only copy of it: the
            // client gets a status code and nothing else, so if it is not here it
            // is nowhere.
            _logger.LogError(exception, "Unhandled exception while processing {Method} {Path}.",
                httpContext.Request.Method, httpContext.Request.Path);

            // The exception never reaches the framework's own instrumentation,
            // because handling it here is what stops it propagating. Marking the
            // span keeps a failed request visible as failed in the traces.
            //
            // The status description is the generic title, not the exception's
            // message: a trace backend is a different audience from a log, usually
            // with wider access, and an exception message can quote a balance or a
            // connection string. The full exception is in the log above.
            Activity.Current?.SetStatus(ActivityStatusCode.Error, problemDetails.Title);
        }
        else
        {
            // A refused request is an outcome, not an incident: insufficient
            // funds and duplicate keys are the system working. Logging them as
            // errors would bury the failures that do need attention, and the rate
            // of each is already a metric. Only the status and the title are
            // recorded -- the detail of a domain failure quotes balances and
            // amounts, which do not belong in a log.
            _logger.LogDebug("Request refused with {StatusCode} ({ExceptionType}): {Title}",
                status, exception.GetType().Name, problemDetails.Title);
        }

        httpContext.Response.StatusCode = status;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception,
        });
    }
}
