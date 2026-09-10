namespace Ledger.Infrastructure.Messaging;

/// <summary>Where the broker is and what the ledger publishes to it.</summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string VirtualHost { get; set; } = "/";

    /// <remarks>
    /// Supplied by the environment. The defaults here are RabbitMQ's own
    /// well-known development values, which exist so a laptop works out of the
    /// box; a deployment that leaves them unchanged has a configuration problem,
    /// not a code problem.
    /// </remarks>
    public string Username { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    /// <summary>The exchange ledger events are published to.</summary>
    public string Exchange { get; set; } = "ledger.events";

    /// <summary>
    /// How long to wait for the broker to confirm a published message.
    /// </summary>
    /// <remarks>
    /// A message is only marked published once the broker has confirmed it, so
    /// this bounds how long a single attempt can occupy a worker before the
    /// message is left pending and retried.
    /// </remarks>
    public int ConfirmTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// How long to spend trying to reach the broker before giving up on an
    /// attempt.
    /// </summary>
    /// <remarks>
    /// The client's own default is thirty seconds, which against a broker that is
    /// simply gone means each publisher attempt occupies a worker for half a
    /// minute doing nothing. Failing quickly and backing off drains a recovered
    /// backlog sooner than failing slowly does.
    /// </remarks>
    public int ConnectionTimeoutSeconds { get; set; } = 5;
}

/// <summary>Where Redis is, and whether it is expected at all.</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>
    /// A StackExchange.Redis connection string, or empty to run without Redis.
    /// </summary>
    /// <remarks>
    /// Empty is a supported configuration, not a broken one. Nothing in the ledger
    /// depends on Redis yet, so a deployment that has not provisioned one should
    /// start and serve traffic normally rather than fail.
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>How hard the background publisher works.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// Whether this process runs the publisher.
    /// </summary>
    /// <remarks>
    /// Off in tests, which drive the publisher directly and would otherwise be
    /// racing a background loop for the same rows. In a deployment it is on
    /// everywhere: several publishers are safe, because claiming is what makes
    /// them safe.
    /// </remarks>
    public bool PublisherEnabled { get; set; } = true;

    public int BatchSize { get; set; } = 50;

    /// <summary>How often to look for work when the last poll found none.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// How long a claimed message stays claimed.
    /// </summary>
    /// <remarks>
    /// Long enough that a slow publish is not republished underneath itself,
    /// short enough that a worker dying mid-publish does not strand the message
    /// for long.
    /// </remarks>
    public int ClaimLeaseSeconds { get; set; } = 60;

    public int MaximumBackoffSeconds { get; set; } = 300;
}
