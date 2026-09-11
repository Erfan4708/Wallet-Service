using Ledger.Api.ErrorHandling;
using Ledger.Application.Exceptions;
using Ledger.Domain.Enums;
using Ledger.Domain.Exceptions;
using Ledger.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;

namespace Ledger.Api.Tests.ErrorHandling;

public class ExceptionMappingTests
{
    [Fact]
    public void A_validation_failure_is_a_bad_request()
    {
        var exception = new ValidationException("AccountId", "An account identifier is required.");

        var problem = ExceptionMapping.ToProblemDetails(exception);

        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
    }

    [Fact]
    public void A_validation_failure_reports_the_individual_errors()
    {
        var exception = new ValidationException("AccountId", "An account identifier is required.");

        var problem = ExceptionMapping.ToProblemDetails(exception);

        var errors = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[]>>(problem.Extensions["errors"]);
        Assert.Equal(["An account identifier is required."], errors["AccountId"]);
    }

    [Fact]
    public void A_missing_resource_is_not_found()
    {
        var problem = ExceptionMapping.ToProblemDetails(new NotFoundException("Account", Guid.Empty));

        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }

    [Fact]
    public void A_conflicting_request_is_a_conflict()
    {
        var problem = ExceptionMapping.ToProblemDetails(new ConflictException("Account already exists."));

        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal("Account already exists.", problem.Detail);
    }

    // A well-formed request refused by a business rule is 422, not 400: the
    // client sent nothing malformed, so telling them "bad request" would send
    // them looking for a mistake that is not there.
    [Theory]
    [MemberData(nameof(DomainExceptions))]
    public void A_broken_business_rule_is_unprocessable(DomainException exception)
    {
        var problem = ExceptionMapping.ToProblemDetails(exception);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.Status);
        Assert.Equal(exception.Message, problem.Detail);
    }

    public static TheoryData<DomainException> DomainExceptions() => new()
    {
        new CurrencyMismatchException(Currency.USD, Currency.EUR),
        new InsufficientFundsException(
            Guid.Empty,
            Money.Zero(Currency.USD),
            new Money(10m, Currency.USD)),
        new SameAccountTransferException(Guid.Empty),
        new AccountTypeMismatchException(Guid.Empty, AccountType.Wallet, AccountType.System),
    };

    // A state conflict, not a rule violation: the request was sound but the world
    // moved on. 409 tells the client to look again; 422 would send them hunting
    // for a mistake in their request that is not there.
    [Fact]
    public void Reversing_an_already_reversed_transaction_is_a_conflict()
    {
        var problem = ExceptionMapping.ToProblemDetails(
            new TransactionAlreadyReversedException(Guid.Empty));

        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
    }

    // This one should be unreachable, so it must not be dressed up as a client
    // error. An unbalanced transaction means the defect is ours.
    [Fact]
    public void An_unbalanced_transaction_is_reported_as_an_internal_error_without_detail()
    {
        var problem = ExceptionMapping.ToProblemDetails(
            new UnbalancedTransactionException(Guid.Empty, entryCount: 1));

        Assert.Equal(StatusCodes.Status500InternalServerError, problem.Status);
        Assert.Null(problem.Detail);
    }

    [Fact]
    public void An_unrecognised_exception_is_an_internal_error()
    {
        var problem = ExceptionMapping.ToProblemDetails(new InvalidOperationException("boom"));

        Assert.Equal(StatusCodes.Status500InternalServerError, problem.Status);
    }

    // The response for an unexpected failure must describe nothing about the
    // internals: not the message, not the exception type, not a stack trace.
    // Those go to the log, where operators can see them and attackers cannot.
    [Fact]
    public void An_unrecognised_exception_leaks_nothing_about_the_failure()
    {
        const string secret = "Server=db;Password=hunter2";

        var problem = ExceptionMapping.ToProblemDetails(new InvalidOperationException(secret));

        Assert.Null(problem.Detail);
        Assert.DoesNotContain(secret, problem.Title);
        Assert.DoesNotContain("InvalidOperationException", problem.Title);
        Assert.Empty(problem.Extensions);
    }

    // A request the framework could not read is the client's mistake, but the
    // framework's own message names internal types and JSON paths.
    [Fact]
    public void A_request_that_cannot_be_read_is_a_client_error_without_internal_detail()
    {
        var exception = new BadHttpRequestException(
            "Failed to read parameter \"MovementRequest request\" from the request body as JSON.",
            StatusCodes.Status400BadRequest,
            new System.Text.Json.JsonException("The JSON value could not be converted. Path: $.currency"));

        var problem = ExceptionMapping.ToProblemDetails(exception);

        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.DoesNotContain("MovementRequest", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("$.currency", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapping_requires_an_exception()
    {
        Assert.Throws<ArgumentNullException>(() => ExceptionMapping.ToProblemDetails(null!));
    }
}
