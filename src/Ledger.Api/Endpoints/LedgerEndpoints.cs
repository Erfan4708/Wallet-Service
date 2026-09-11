using Ledger.Api.OpenApi;
using Ledger.Application.Accounts;
using Ledger.Application.Accounts.CreateAccount;
using Ledger.Application.Accounts.GetAccount;
using Ledger.Application.Ledger;
using Ledger.Domain.Enums;

namespace Ledger.Api.Endpoints;

/// <summary>The HTTP surface over the ledger's use cases.</summary>
/// <remarks>
/// <para>
/// Endpoints are deliberately thin: read the request, call one use case, choose a
/// status code. They contain no rules of their own, because a rule that lives in
/// an endpoint applies only to callers who arrive through that endpoint.
/// </para>
/// <para>
/// Nothing here catches exceptions. Every failure travels to the single
/// exception handler that owns the error contract, which is what keeps the
/// contract identical across endpoints written months apart.
/// </para>
/// <para>
/// The <c>WithSummary</c> and <c>Produces</c> calls describe the contract for the
/// OpenAPI document. They change nothing about what an endpoint does.
/// </para>
/// </remarks>
internal static class LedgerEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";

    internal static void MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/accounts", async (
            CreateAccountRequest request,
            CreateAccountHandler handler,
            CancellationToken cancellationToken) =>
        {
            var summary = await handler.HandleAsync(
                new CreateAccountCommand(request.AccountId ?? Guid.NewGuid(), request.Currency),
                cancellationToken);

            return Results.Created($"/accounts/{summary.Id}", summary);
        })
        .WithName("OpenAccount")
        .WithTags("Accounts")
        .WithSummary("Open a wallet in one currency.")
        .WithDescription(
            "Supplying accountId makes a retry safe: a second request for the same identifier is " +
            "refused with 409 rather than opening a second account.")
        .Produces<AccountSummary>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet("/accounts/{id:guid}", async (
            Guid id,
            GetAccountHandler handler,
            CancellationToken cancellationToken) =>
            Results.Ok(await handler.HandleAsync(new GetAccountQuery(id), cancellationToken)))
        .WithName("GetAccount")
        .WithTags("Accounts")
        .WithSummary("Read an account and its current balance.")
        .Produces<AccountSummary>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/accounts/{id:guid}/statement", async (
            Guid id,
            int? limit,
            GetAccountStatementHandler handler,
            CancellationToken cancellationToken) =>
            Results.Ok(await handler.HandleAsync(
                new GetAccountStatementQuery(id, limit ?? 50), cancellationToken)))
        .WithName("GetAccountStatement")
        .WithTags("Accounts")
        .WithSummary("Read an account's balance with its most recent ledger entries.")
        .WithDescription("Entries are newest first. limit defaults to 50 and must be between 1 and 500.")
        .Produces<AccountStatement>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/accounts/{id:guid}/deposits", async (
            Guid id,
            MovementRequest request,
            HttpRequest httpRequest,
            DepositHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(
                new DepositCommand(
                    request.TransactionId ?? Guid.NewGuid(),
                    id,
                    request.Amount,
                    request.Currency,
                    IdempotencyKeyOf(httpRequest),
                    request.ExternalReference),
                cancellationToken);

            return Respond(result);
        })
        .WithName("Deposit")
        .WithTags("Ledger")
        .WithSummary("Deposit money into a wallet.")
        .WithDescription(
            "Credits the wallet and debits the currency's settlement account in one balanced transaction.")
        .MovesMoney();

        app.MapPost("/accounts/{id:guid}/withdrawals", async (
            Guid id,
            MovementRequest request,
            HttpRequest httpRequest,
            WithdrawHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(
                new WithdrawCommand(
                    request.TransactionId ?? Guid.NewGuid(),
                    id,
                    request.Amount,
                    request.Currency,
                    IdempotencyKeyOf(httpRequest),
                    request.ExternalReference),
                cancellationToken);

            return Respond(result);
        })
        .WithName("Withdraw")
        .WithTags("Ledger")
        .WithSummary("Withdraw money from a wallet.")
        .WithDescription(
            "Debits the wallet and credits the currency's settlement account. Refused with 422 when the " +
            "wallet does not hold the amount.")
        .MovesMoney();

        app.MapPost("/transfers", async (
            TransferRequest request,
            HttpRequest httpRequest,
            TransferHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(
                new TransferCommand(
                    request.TransactionId ?? Guid.NewGuid(),
                    request.SourceAccountId,
                    request.DestinationAccountId,
                    request.Amount,
                    request.Currency,
                    IdempotencyKeyOf(httpRequest),
                    request.ExternalReference),
                cancellationToken);

            return Respond(result);
        })
        .WithName("Transfer")
        .WithTags("Ledger")
        .WithSummary("Move money between two wallets.")
        .WithDescription(
            "Both wallets must hold the transfer's currency. Refused with 422 when the source does not " +
            "hold the amount.")
        .MovesMoney();

        app.MapGet("/ledger/transactions/{id:guid}", async (
            Guid id,
            GetLedgerTransactionHandler handler,
            CancellationToken cancellationToken) =>
            Results.Ok(await handler.HandleAsync(new GetLedgerTransactionQuery(id), cancellationToken)))
        .WithName("GetLedgerTransaction")
        .WithTags("Ledger")
        .WithSummary("Read a recorded ledger transaction and its entries.")
        .WithDescription(
            "The resource a money movement's Location header names. wasReplayed is always false here.")
        .Produces<LedgerTransactionResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/ledger/transactions/{id:guid}/reversal", async (
            Guid id,
            HttpRequest httpRequest,
            ReverseTransactionHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(
                new ReverseTransactionCommand(Guid.NewGuid(), id, IdempotencyKeyOf(httpRequest)),
                cancellationToken);

            return Respond(result);
        })
        .WithName("ReverseTransaction")
        .WithTags("Ledger")
        .WithSummary("Reverse a recorded transaction.")
        .WithDescription(
            "Records a new transaction that negates every entry of the original. A transaction can be " +
            "reversed once, and a reversal cannot itself be reversed (409). Refused with 422 when a " +
            "wallet no longer holds the amount being returned.")
        .MovesMoney();
    }

    /// <remarks>
    /// A replayed request returns 200 rather than 201: the resource was created
    /// by the original call, and claiming to have created it again would be a
    /// lie a client could act on.
    /// </remarks>
    private static IResult Respond(LedgerTransactionResult result) =>
        result.WasReplayed
            ? Results.Ok(result)
            : Results.Created($"/ledger/transactions/{result.TransactionId}", result);

    private static string? IdempotencyKeyOf(HttpRequest request) =>
        request.Headers.TryGetValue(IdempotencyHeader, out var values)
            ? values.FirstOrDefault()
            : null;

    /// <summary>The contract every money-moving endpoint shares.</summary>
    private static RouteHandlerBuilder MovesMoney(this RouteHandlerBuilder builder) => builder
        .WithMetadata(new IdempotentEndpointMetadata())
        .Produces<LedgerTransactionResult>(StatusCodes.Status201Created)
        .Produces<LedgerTransactionResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}

/// <param name="AccountId">
/// Optional. A client that supplies one can retry a failed create without
/// risking a second account.
/// </param>
internal sealed record CreateAccountRequest(Currency Currency, Guid? AccountId = null);

internal sealed record MovementRequest(
    decimal Amount,
    Currency Currency,
    Guid? TransactionId = null,
    string? ExternalReference = null);

internal sealed record TransferRequest(
    Guid SourceAccountId,
    Guid DestinationAccountId,
    decimal Amount,
    Currency Currency,
    Guid? TransactionId = null,
    string? ExternalReference = null);
