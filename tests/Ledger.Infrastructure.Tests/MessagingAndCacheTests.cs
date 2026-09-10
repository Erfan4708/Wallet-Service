using System.Text;
using Ledger.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Ledger.Infrastructure.Tests;

/// <summary>A real broker and a real cache, started once for this collection.</summary>
public sealed class BrokerAndCacheFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer? _rabbit;
    private readonly RedisContainer? _redis;

    public BrokerAndCacheFixture()
    {
        if (DockerAvailability.ShouldSkip)
        {
            return;
        }

        _rabbit = new RabbitMqBuilder().WithImage("rabbitmq:3.13-alpine").Build();
        _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
    }

    public RabbitMqOptions RabbitOptions { get; private set; } = new();

    public string RedisConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (_rabbit is null || _redis is null)
        {
            return;
        }

        await Task.WhenAll(_rabbit.StartAsync(), _redis.StartAsync());

        RabbitOptions = new RabbitMqOptions
        {
            Host = _rabbit.Hostname,
            Port = _rabbit.GetMappedPublicPort(5672),
            Username = "rabbitmq",
            Password = "rabbitmq",
            Exchange = "ledger.events.test",
        };

        RedisConnectionString = _redis.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_rabbit is not null)
        {
            await _rabbit.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class BrokerAndCacheCollection : ICollectionFixture<BrokerAndCacheFixture>
{
    public const string Name = "BrokerAndCache";
}

/// <summary>
/// Broker and cache connectivity, and what their absence does to readiness.
/// </summary>
/// <remarks>
/// The health decisions tested here are the deliberate ones. Neither Redis nor
/// RabbitMQ may make the API unready: the ledger serves every balance from
/// PostgreSQL, and a broker outage only delays outbox publication. An instance
/// that failed readiness for either would refuse traffic it can serve perfectly
/// well, turning an infrastructure blip into a financial outage.
/// </remarks>
[Collection(BrokerAndCacheCollection.Name)]
public class MessagingAndCacheTests
{
    private const string Unreachable = "127.0.0.1:1,connectTimeout=500,abortConnect=false";

    private readonly BrokerAndCacheFixture _infrastructure;

    public MessagingAndCacheTests(BrokerAndCacheFixture infrastructure) => _infrastructure = infrastructure;

    private static RabbitMqOptions UnreachableBroker() =>
        new() { Host = "127.0.0.1", Port = 1, Username = "none", Password = "none" };

    // ------------------------------------------------------------- publishing

    // The implementation the fakes elsewhere stand in for, against a real broker:
    // the exchange is declared, the message is confirmed, and it arrives.
    [RequiresDockerFact]
    public async Task A_published_message_reaches_the_broker()
    {
        const string messageType = "ledger.transaction.recorded.v1";
        var messageId = Guid.NewGuid();

        var options = _infrastructure.RabbitOptions;

        // The publisher declares the exchange, so it has to exist before a queue
        // can be bound to it. A topic exchange discards anything published while
        // nothing is listening, which is why the binding comes first.
        using var publisher = new RabbitMqMessagePublisher(Options.Create(options));
        using var listener = new QueueListener(options, messageType);

        await publisher.PublishAsync(
            messageType, messageId, Encoding.UTF8.GetBytes("""{"probe":true}"""), CancellationToken.None);

        var received = await listener.NextAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(messageId.ToString(), received.MessageId);
        Assert.Equal(messageType, received.Type);

        // Persistent, so a broker restart does not discard what the ledger has
        // already recorded as published.
        Assert.Equal(2, received.DeliveryMode);
    }

    [RequiresDockerFact]
    public async Task Publishing_to_an_unreachable_broker_fails_rather_than_silently_succeeding()
    {
        using var publisher = new RabbitMqMessagePublisher(Options.Create(UnreachableBroker()));

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(
            "ledger.test", Guid.NewGuid(), Encoding.UTF8.GetBytes("{}"), CancellationToken.None));
    }

    // ---------------------------------------------------------------- health

    [RequiresDockerFact]
    public async Task The_broker_health_check_is_healthy_when_the_broker_is_up()
    {
        var check = new RabbitMqHealthCheck(Options.Create(_infrastructure.RabbitOptions));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [RequiresDockerFact]
    public async Task The_broker_health_check_degrades_rather_than_fails_when_it_is_down()
    {
        var check = new RabbitMqHealthCheck(Options.Create(UnreachableBroker()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [RequiresDockerFact]
    public async Task The_cache_health_check_is_healthy_when_redis_is_up()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(_infrastructure.RedisConnectionString);

        var result = await new RedisHealthCheck(redis).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [RequiresDockerFact]
    public async Task The_cache_health_check_degrades_rather_than_fails_when_redis_is_down()
    {
        var options = ConfigurationOptions.Parse(Unreachable);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(options);

        var result = await new RedisHealthCheck(redis).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    // Running without Redis is a supported configuration, not a broken one:
    // nothing in the ledger depends on it.
    [RequiresDockerFact]
    public async Task The_cache_health_check_is_healthy_when_redis_is_not_configured()
    {
        var result = await new RedisHealthCheck().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // ------------------------------------------------------------- plumbing

    private sealed record ReceivedMessage(string? MessageId, string? Type, byte DeliveryMode);

    /// <summary>A queue bound before anything is published, so nothing is missed.</summary>
    private sealed class QueueListener : IDisposable
    {
        private readonly IConnection _connection;
        private readonly IModel _channel;
        private readonly TaskCompletionSource<ReceivedMessage> _received = new();

        internal QueueListener(RabbitMqOptions options, string routingKey)
        {
            var factory = new ConnectionFactory
            {
                HostName = options.Host,
                Port = options.Port,
                UserName = options.Username,
                Password = options.Password,
            };

            _connection = factory.CreateConnection("ledger-test-consumer");
            _channel = _connection.CreateModel();

            _channel.ExchangeDeclare(options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);

            var queue = _channel.QueueDeclare().QueueName;
            _channel.QueueBind(queue, options.Exchange, routingKey);

            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += (_, delivered) => _received.TrySetResult(new ReceivedMessage(
                delivered.BasicProperties.MessageId,
                delivered.BasicProperties.Type,
                delivered.BasicProperties.DeliveryMode));

            _channel.BasicConsume(queue, autoAck: true, consumer);
        }

        internal async Task<ReceivedMessage> NextAsync(TimeSpan timeout)
        {
            using var expiry = new CancellationTokenSource(timeout);
            await using var registration = expiry.Token.Register(() => _received.TrySetCanceled());

            return await _received.Task;
        }

        public void Dispose()
        {
            _channel.Dispose();
            _connection.Dispose();
        }
    }
}
