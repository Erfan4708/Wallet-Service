using Ledger.Application.Exceptions;
using Ledger.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Api.ErrorHandling;

/// <summary>
/// Translates an exception into the RFC 9457 problem details returned to the
/// client.
/// </summary>
/// <remarks>
/// <para>
/// A pure function on purpose: it takes an exception and returns a value, with
/// no <see cref="HttpContext"/>, no writing and no logging. That makes the error
/// contract — the part of the API every client depends on — directly testable
/// without a server, and keeps the decision about <em>what</em> to say separate
/// from the mechanics of sending it.
/// </para>
/// <para>
/// The status codes encode a distinction worth being explicit about. A
/// <see cref="ValidationException"/> is 400 because the request was never
/// well-formed. A <see cref="DomainException"/> is 422 because the request was
/// perfectly well-formed and the business rules refused it — the client did
/// nothing syntactically wrong, so telling them "bad request" would be
/// misleading.
/// </para>
/// </remarks>
public static class ExceptionMapping
{
    private const string UnexpectedErrorTitle = "An unexpected error occurred.";

    public static ProblemDetails ToProblemDetails(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            ValidationException validation => Validation(validation),
            NotFoundException notFound => Problem(StatusCodes.Status404NotFound, "Not found.", notFound.Message),
            ConflictException conflict => Problem(StatusCodes.Status409Conflict, "Conflict.", conflict.Message),
            DomainException domain => Problem(StatusCodes.Status422UnprocessableEntity, "Business rule violated.", domain.Message),

            // Everything else is a defect rather than an outcome the client can
            // act on, so it is reported without detail. An unrecognised
            // exception type deliberately lands here instead of being guessed
            // at: a wrong status code is worse than an honest 500.
            _ => Problem(StatusCodes.Status500InternalServerError, UnexpectedErrorTitle, detail: null),
        };
    }

    private static ProblemDetails Validation(ValidationException exception)
    {
        var problem = Problem(StatusCodes.Status400BadRequest, "Validation failed.", exception.Message);
        problem.Extensions["errors"] = exception.Errors;

        return problem;
    }

    private static ProblemDetails Problem(int status, string title, string? detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
    };
}
