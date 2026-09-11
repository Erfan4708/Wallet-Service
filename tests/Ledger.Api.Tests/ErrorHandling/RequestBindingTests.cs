using System.Net;
using System.Text;
using System.Text.Json;
using Ledger.Api.Tests.Observability;

namespace Ledger.Api.Tests.ErrorHandling;

/// <summary>
/// What a client receives when its request cannot even be read.
/// </summary>
/// <remarks>
/// Binding fails before any use case runs, so these need no database. Without
/// explicit handling the framework answers with a bare 400 in production and lets
/// the exception escape in development, where it became a 500; either way the
/// response carried no problem details and no trace identifier to quote.
/// </remarks>
public class RequestBindingTests : IClassFixture<UnreachableDatabaseFactory>
{
    private static readonly Uri DepositUri =
        new("/accounts/11111111-1111-1111-1111-111111111111/deposits", UriKind.Relative);

    private readonly UnreachableDatabaseFactory _factory;

    public RequestBindingTests(UnreachableDatabaseFactory factory) => _factory = factory;

    [Theory]
    [InlineData("{\"amount\":")]
    [InlineData("{\"amount\":1.00,\"currency\":\"XYZ\"}")]
    [InlineData("")]
    public async Task An_unreadable_request_is_a_bad_request_with_problem_details(string body)
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(DepositUri, content);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal("The request could not be read.", problem.GetProperty("title").GetString());
        Assert.Equal(response.Headers.GetValues("trace-id").Single(), problem.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task An_unreadable_request_reveals_nothing_about_the_server()
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent(
            "{\"amount\":1.00,\"currency\":\"XYZ\"}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(DepositUri, content);
        var text = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Ledger.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MovementRequest", text, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$.currency", text, StringComparison.Ordinal);
    }
}
