using System.Net;
using System.Text.Json;
using Ledger.Api.Tests.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledger.Api.Tests.OpenApi;

/// <summary>Hosts the application with the OpenAPI document switched on.</summary>
public sealed class OpenApiEnabledFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");
        builder.UseSetting("OpenApi:Enabled", "true");
        builder.UseSetting("Outbox:PublisherEnabled", "false");

        // The document is generated from the endpoints; no database is involved.
        builder.UseSetting(
            "ConnectionStrings:LedgerDatabase",
            "Host=127.0.0.1;Port=1;Database=ledger;Username=none;Password=none;Timeout=1;Command Timeout=1");
    }
}

/// <summary>
/// The OpenAPI document describes the contract the endpoints actually implement.
/// </summary>
public class OpenApiDocumentTests : IClassFixture<OpenApiEnabledFactory>, IClassFixture<UnreachableDatabaseFactory>
{
    private static readonly Uri DocumentUri = new("/swagger/v1/swagger.json", UriKind.Relative);

    private static readonly string[] OperationNames = ["get", "put", "post", "delete", "patch"];

    private readonly OpenApiEnabledFactory _enabled;
    private readonly UnreachableDatabaseFactory _default;

    public OpenApiDocumentTests(OpenApiEnabledFactory enabled, UnreachableDatabaseFactory byDefault)
    {
        _enabled = enabled;
        _default = byDefault;
    }

    private async Task<JsonElement> DocumentAsync()
    {
        using var client = _enabled.CreateClient();
        var body = await client.GetStringAsync(DocumentUri);

        return JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task The_document_is_served_when_enabled()
    {
        var document = await DocumentAsync();

        Assert.StartsWith("3.", document.GetProperty("openapi").GetString(), StringComparison.Ordinal);
    }

    // Production is the default of the test host. Publishing a map of the API is a
    // choice a deployment makes, not something it inherits.
    [Fact]
    public async Task The_document_is_not_served_unless_enabled()
    {
        using var client = _default.CreateClient();

        using var response = await client.GetAsync(DocumentUri);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/accounts", "post")]
    [InlineData("/accounts/{id}", "get")]
    [InlineData("/accounts/{id}/statement", "get")]
    [InlineData("/accounts/{id}/deposits", "post")]
    [InlineData("/accounts/{id}/withdrawals", "post")]
    [InlineData("/transfers", "post")]
    [InlineData("/ledger/transactions/{id}", "get")]
    [InlineData("/ledger/transactions/{id}/reversal", "post")]
    public async Task Every_endpoint_is_described(string path, string method)
    {
        var document = await DocumentAsync();

        Assert.True(
            document.GetProperty("paths").TryGetProperty(path, out var item) && item.TryGetProperty(method, out _),
            $"{method.ToUpperInvariant()} {path} is missing from the document.");
    }

    [Theory]
    [InlineData("/accounts/{id}/deposits")]
    [InlineData("/accounts/{id}/withdrawals")]
    [InlineData("/transfers")]
    [InlineData("/ledger/transactions/{id}/reversal")]
    public async Task Money_movements_document_the_idempotency_key_header(string path)
    {
        var document = await DocumentAsync();
        var operation = document.GetProperty("paths").GetProperty(path).GetProperty("post");

        Assert.Contains(
            operation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key"
                && parameter.GetProperty("in").GetString() == "header");
    }

    [Theory]
    [InlineData("/accounts/{id}/deposits")]
    [InlineData("/accounts/{id}/withdrawals")]
    [InlineData("/transfers")]
    [InlineData("/ledger/transactions/{id}/reversal")]
    public async Task Money_movements_document_every_outcome(string path)
    {
        var document = await DocumentAsync();
        var responses = document.GetProperty("paths").GetProperty(path).GetProperty("post").GetProperty("responses");

        foreach (var status in new[] { "200", "201", "400", "404", "409", "422" })
        {
            Assert.True(responses.TryGetProperty(status, out _), $"{path} does not document {status}.");
        }

        Assert.True(
            responses.GetProperty("422").GetProperty("content").TryGetProperty("application/problem+json", out _),
            "A refusal is documented as problem details.");
    }

    [Fact]
    public async Task Every_response_documents_the_trace_identifier_header()
    {
        var document = await DocumentAsync();

        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(item => OperationNames.Contains(item.Name)))
            {
                foreach (var response in operation.Value.GetProperty("responses").EnumerateObject())
                {
                    Assert.True(
                        response.Value.TryGetProperty("headers", out var headers)
                            && headers.TryGetProperty("trace-id", out _),
                        $"{operation.Name.ToUpperInvariant()} {path.Name} {response.Name} does not document trace-id.");
                }
            }
        }
    }

    [Fact]
    public async Task Currencies_are_described_by_the_codes_sent_on_the_wire()
    {
        var document = await DocumentAsync();

        var codes = document.GetProperty("components").GetProperty("schemas").GetProperty("Currency")
            .GetProperty("enum").EnumerateArray().Select(value => value.ToString()).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["EUR", "IRR", "USD"], codes);
    }

    [Fact]
    public async Task Request_bodies_use_the_property_names_sent_on_the_wire()
    {
        var document = await DocumentAsync();

        var properties = document.GetProperty("components").GetProperty("schemas").GetProperty("MovementRequest")
            .GetProperty("properties");

        Assert.True(properties.TryGetProperty("amount", out _));
        Assert.True(properties.TryGetProperty("currency", out _));
    }
}
