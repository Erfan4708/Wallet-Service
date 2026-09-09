using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Infrastructure;

/// <summary>
/// Registers the infrastructure layer's services.
/// </summary>
/// <remarks>
/// This is the seam where the application layer's abstractions get their
/// implementations. It is intentionally empty: no persistence exists yet, and a
/// stand-in implementation would be code written to be deleted. When the
/// PostgreSQL phase begins, the EF Core <c>DbContext</c>,
/// <c>IAccountRepository</c> and <c>IUnitOfWork</c> are registered here and
/// nothing in the application layer changes.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services;
    }
}
