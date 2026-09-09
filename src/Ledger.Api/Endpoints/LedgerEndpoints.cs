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
        });

        app.MapGet("/accounts/{id:guid}", async (
            Guid id,
            GetAccountHandler handler,
            CancellationToken cancellationToken) =>
            Results.Ok(await handler.HandleAsync(new GetAccountQuery(id), cancellationToken)));

        app.MapGet("/accounts/{id:guid}/statement", async (
            Guid id,
            int? limit,
            GetAccountStatementHandler handler,
            CancellationToken cancellationToken) =>
            Results.Ok(await handler.HandleAsync(
                new GetAccountStatementQuery(id, limit ?? 50), cancellationToken)));

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
        });

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
        });

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
        });

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
        });
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
