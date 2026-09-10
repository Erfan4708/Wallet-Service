using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Ledger.Infrastructure.Messaging;

/// <summary>
/// Publishes outbox messages to RabbitMQ and waits for the broker to confirm
/// them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Publisher confirms are the point.</b> Without them <c>BasicPublish</c>
/// returns as soon as the bytes are handed to the socket, which says nothing
/// about whether the broker accepted or persisted anything. Marking a message
/// published on that basis would lose messages whenever a broker died mid-flight
/// — exactly the failure the outbox exists to prevent.
/// </para>
/// <para>
/// The connection is created lazily and rebuilt after a failure, so a broker that
/// is down when the service starts is not a startup failure: the outbox simply
/// accumulates and drains when the broker returns.
/// </para>
/// </remarks>
internal sealed class RabbitMqMessagePublisher : IMessagePublisher, IDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqMessagePublisher(IOptions<RabbitMqOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    public async Task PublishAsync(
        string messageType,
        Guid messageId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        // One channel, used by one publisher loop at a time. RabbitMQ channels
        // are not thread-safe, and sharing one across concurrent publishes is a
        // classic source of protocol errors that look like broker faults.
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var channel = EnsureChannel();

            var properties = channel.CreateBasicProperties();
            properties.ContentType = "application/json";
            properties.MessageId = messageId.ToString();
            properties.Type = messageType;
            properties.DeliveryMode = 2; // Persistent: survives a broker restart.

            channel.BasicPublish(
                exchange: _options.Exchange,
                routingKey: messageType,
                mandatory: false,
                basicProperties: properties,
                body: payload);

            // Throws if the broker nacks or does not answer in time, which leaves
            // the message pending for another attempt.
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(_options.ConfirmTimeoutSeconds));
        }
        catch
        {
            // The channel may be in an unusable state after any failure. Dropping
            // it forces a clean reconnect on the next attempt rather than
            // repeatedly failing against a broken one.
            DiscardChannel();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IModel EnsureChannel()
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        DiscardChannel();

        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.Username,
            Password = _options.Password,
            AutomaticRecoveryEnabled = true,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds),
            SocketReadTimeout = TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds),
            SocketWriteTimeout = TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds),
        };

        _connection = factory.CreateConnection("ledger-outbox-publisher");
        _channel = _connection.CreateModel();

        // Durable, so the exchange survives a broker restart and messages are not
        // silently dropped for want of somewhere to go.
        _channel.ExchangeDeclare(
            exchange: _options.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false);

        _channel.ConfirmSelect();

        return _channel;
    }

    private void DiscardChannel()
    {
        try
        {
            _channel?.Dispose();
            _connection?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Disposing a broken connection frequently throws. There is nothing
            // useful to do about it, and letting it escape would mask the original
            // publishing failure the caller needs to see.
        }
        finally
        {
            _channel = null;
            _connection = null;
        }
    }

    public void Dispose()
    {
        DiscardChannel();
        _gate.Dispose();
    }
}
