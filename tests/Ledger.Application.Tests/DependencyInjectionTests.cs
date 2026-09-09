using Ledger.Application.Abstractions;
using Ledger.Application.Accounts.CreateAccount;
using Ledger.Application.Accounts.GetAccount;
using Ledger.Application.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Application.Tests;

/// <summary>
/// Checks that the container can actually build what the application layer
/// claims to offer.
/// </summary>
/// <remarks>
/// Registration errors are invisible at compile time and only surface as a
/// runtime resolution failure on the first request that needs the service.
/// Resolving every use case here turns "someone added a handler and forgot to
/// register it" into a failing test instead of a production incident.
/// </remarks>
public class DependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddApplication();

        // Stands in for the infrastructure layer, which has no implementations
        // yet. The point of the exercise is that the application layer is
        // satisfied by anything implementing its abstractions.
        services.AddScoped<IAccountRepository>(_ => new FakeAccountRepository());
        services.AddScoped<IUnitOfWork>(_ => new FakeUnitOfWork());

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void Every_use_case_can_be_resolved()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CreateAccountHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GetAccountHandler>());
    }

    // Scoped, not singleton: a handler will hold a unit of work bound to one
    // request's transaction, and sharing that across requests would mean sharing
    // a transaction across requests.
    [Fact]
    public void Use_cases_are_scoped_to_a_request()
    {
        using var provider = BuildProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var fromFirst = first.ServiceProvider.GetRequiredService<CreateAccountHandler>();
        var fromSecond = second.ServiceProvider.GetRequiredService<CreateAccountHandler>();
        var againFromFirst = first.ServiceProvider.GetRequiredService<CreateAccountHandler>();

        Assert.Same(fromFirst, againFromFirst);
        Assert.NotSame(fromFirst, fromSecond);
    }
}
