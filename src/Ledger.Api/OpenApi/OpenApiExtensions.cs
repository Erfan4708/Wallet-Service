using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Ledger.Api.OpenApi;

/// <summary>
/// The machine-readable description of the HTTP API, and the UI over it.
/// </summary>
/// <remarks>
/// <para>
/// Generated from the endpoints themselves — their parameters, and the
/// <c>Produces</c> metadata each declares — so the document cannot drift from the
/// code the way a hand-written one would.
/// </para>
/// <para>
/// Served only where it is enabled: by default in Development, and in the local
/// Compose stack through <c>OpenApi__Enabled</c>. A deployed instance decides for
/// itself. The document reveals nothing the endpoints do not, but it is a map of
/// the attack surface, and publishing it is a deliberate choice rather than a
/// default.
/// </para>
/// </remarks>
internal static class OpenApiExtensions
{
    internal const string DocumentPath = "/swagger/v1/swagger.json";

    internal static bool IsLedgerOpenApiEnabled(this WebApplicationBuilder builder) =>
        builder.Configuration.GetValue<bool?>("OpenApi:Enabled") ?? builder.Environment.IsDevelopment();

    internal static IServiceCollection AddLedgerOpenApi(this IServiceCollection services)
    {
        // Swashbuckle describes schemas with the MVC JSON options, not the minimal
        // API options the endpoints serialise with. Without the same converter it
        // documents a currency as the ISO numeric value the enum is backed by, which
        // is neither what the API sends nor what it accepts.
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Ledger API",
                Version = "v1",
                Description =
                    "A double-entry ledger: wallets, deposits, withdrawals, transfers and reversals. " +
                    "Every error is an RFC 9457 problem details body carrying the request's traceId, " +
                    "and every response carries the same value in a trace-id header.",
            });

            options.OperationFilter<LedgerOperationFilter>();
        });

        return services;
    }

    internal static void UseLedgerOpenApi(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint(DocumentPath, "Ledger API v1");
            options.DocumentTitle = "Ledger API";
        });
    }
}

/// <summary>
/// Marks an endpoint that honours the <c>Idempotency-Key</c> request header.
/// </summary>
/// <remarks>
/// The endpoint reads the header from the request itself rather than binding it as
/// a parameter, so the document cannot discover it; this metadata is how it is
/// described.
/// </remarks>
internal sealed class IdempotentEndpointMetadata;

/// <summary>
/// Adds to each operation what the endpoint metadata alone does not describe.
/// </summary>
internal sealed class LedgerOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;

        if (string.IsNullOrEmpty(operation.Summary)
            && metadata.OfType<IEndpointSummaryMetadata>().LastOrDefault() is { } summary)
        {
            operation.Summary = summary.Summary;
        }

        if (string.IsNullOrEmpty(operation.Description)
            && metadata.OfType<IEndpointDescriptionMetadata>().LastOrDefault() is { } description)
        {
            operation.Description = description.Description;
        }

        if (metadata.OfType<IdempotentEndpointMetadata>().Any())
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = false,
                Description =
                    "Makes a retry safe. A repeat of the same request with the same key returns the " +
                    "original transaction with 200 instead of moving money again; the same key used " +
                    "for a different request is refused with 409. At most 200 characters.",
                Schema = new OpenApiSchema { Type = "string", MaxLength = 200 },
            });
        }

        foreach (var response in operation.Responses.Values)
        {
            response.Headers["trace-id"] = new OpenApiHeader
            {
                Description = "The W3C trace identifier of this request, also found in the logs and traces.",
                Schema = new OpenApiSchema { Type = "string" },
            };
        }
    }
}
