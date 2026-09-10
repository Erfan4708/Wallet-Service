using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace Ledger.Infrastructure.Messaging;

/// <summary>
/// Reports whether Redis is reachable.
/// </summary>
/// <remarks>
/// Registered as <see cref="HealthStatus.Degraded"/> rather than unhealthy, and
/// the reason matters. Nothing in the ledger reads or writes Redis: every
/// balance, entry and transaction is served from PostgreSQL. An instance that
/// cannot reach Redis can still do its entire job, so taking it out of the load
/// balancer would remove working capacity for no benefit. What the check is for
/// is telling an operator that something they provisioned has gone away.
/// </remarks>
internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer? _redis;

    public RedisHealthCheck(IConnectionMultiplexer? redis = null) => _redis = redis;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_redis is null)
        {
            // Not configured is a supported state, not a failure.
            return HealthCheckResult.Healthy("Redis is not configured.");
        }

        try
        {
            await _redis.GetDatabase().PingAsync();

            return HealthCheckResult.Healthy();
        }
        catch (RedisException exception)
        {
            return HealthCheckResult.Degraded("Redis is unreachable.", exception);
        }
        catch (TimeoutException exception)
        {
            return HealthCheckResult.Degraded("Redis timed out.", exception);
        }
    }
}

/// <summary>
/// Reports whether the broker is reachable.
/// </summary>
/// <remarks>
/// Also degraded rather than unhealthy, and for a reason that is the heart of the
/// outbox design: a broker outage must never stop the ledger accepting money.
/// Transactions keep committing with their outbox rows, the backlog grows, and it
/// drains when the broker returns. An instance that failed readiness because
/// RabbitMQ was down would refuse traffic it is perfectly able to serve, and
/// would turn a messaging problem into a financial outage.
/// </remarks>
internal sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqOptions _options;

    public RabbitMqHealthCheck(IOptions<RabbitMqOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                VirtualHost = _options.VirtualHost,
                UserName = _options.Username,
                Password = _options.Password,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
            };

            using var connection = factory.CreateConnection("ledger-health");

            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Task.FromResult(
                HealthCheckResult.Degraded("The message broker is unreachable.", exception));
        }
    }
}
