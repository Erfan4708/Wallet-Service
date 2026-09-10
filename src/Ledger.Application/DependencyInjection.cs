using Ledger.Application.Accounts.CreateAccount;
using Ledger.Application.Accounts.GetAccount;
using Ledger.Application.Ledger;
using Ledger.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledger.Application;

/// <summary>
/// Registers the application layer's services.
/// </summary>
/// <remarks>
/// The layer registers its own services so that adding a use case does not
/// require editing the host. The host still owns composition: it decides which
/// layers to add and in what order, and nothing here reaches out to configure
/// the wider application.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered as their concrete types. Introducing an interface per
        // handler would create a one-to-one abstraction that no second
        // implementation justifies and that no test needs, since the handlers
        // are already isolated through the repository and unit-of-work seams.
        services.AddScoped<CreateAccountHandler>();
        services.AddScoped<GetAccountHandler>();
        services.AddScoped<GetAccountStatementHandler>();

        services.AddScoped<DepositHandler>();
        services.AddScoped<WithdrawHandler>();
        services.AddScoped<TransferHandler>();
        services.AddScoped<ReverseTransactionHandler>();

        // The clock is an input, not something the code reaches for. Registering
        // it here keeps every use case a pure function of what it is given, and
        // lets a test decide what "now" means.
        services.TryAddSingleton(TimeProvider.System);

        // Singleton because an ActivitySource and a Meter are process-wide
        // publishers: creating one per request would leak instruments and hide
        // the signals from anything that subscribed at start-up.
        services.TryAddSingleton<LedgerTelemetry>();

        return services;
    }
}
