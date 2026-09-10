namespace Ledger.Infrastructure.Messaging;

/// <summary>
/// Hands a message to the broker and waits for it to be accepted.
/// </summary>
/// <remarks>
/// An interface so the outbox publisher can be tested without a broker: the
/// behaviours worth testing — that a message stays pending when publishing
/// fails, that attempts are counted, that a restart picks up what was left —
/// are the ones a real broker makes hardest to arrange. A real RabbitMQ
/// container proves the implementation separately.
/// </remarks>
internal interface IMessagePublisher
{
    /// <summary>
    /// Publishes a message, returning only once the broker has confirmed it.
    /// </summary>
    /// <exception cref="Exception">
    /// The broker refused it or could not be reached. The caller must treat the
    /// message as unpublished.
    /// </exception>
    Task PublishAsync(
        string messageType,
        Guid messageId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}
