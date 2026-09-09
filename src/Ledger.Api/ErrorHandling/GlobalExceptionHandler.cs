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
            _logger.LogError(exception, "Unhandled exception while processing {Method} {Path}.",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogInformation("Request refused with {StatusCode}: {Title}", status, problemDetails.Title);
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
