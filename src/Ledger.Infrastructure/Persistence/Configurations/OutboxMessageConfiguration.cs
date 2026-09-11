using Ledger.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledger.Infrastructure.Persistence.Configurations;

/// <summary>Maps the outbox onto the <c>outbox_messages</c> table.</summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_messages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Id)
            .HasColumnName("id")
            .UseIdentityAlwaysColumn();

        builder.Property(message => message.MessageId)
            .HasColumnName("message_id")
            .IsRequired();

        builder.Property(message => message.MessageType)
            .HasColumnName("message_type")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(message => message.AggregateId)
            .HasColumnName("aggregate_id")
            .IsRequired();

        // jsonb rather than text: it costs nothing to write, and it means an
        // operator can query a stuck backlog by payload field instead of by
        // substring.
        builder.Property(message => message.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(message => message.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(message => message.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(message => message.PublishedAt).HasColumnName("published_at");
        builder.Property(message => message.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(message => message.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired();

        // Truncated on write. A driver's error text can be long, and this column
        // is an operational hint, not a log.
        builder.Property(message => message.LastError)
            .HasColumnName("last_error")
            .HasMaxLength(1000);

        // The same message must never be published under two identifiers, which
        // is what a consumer's de-duplication depends on.
        builder.HasIndex(message => message.MessageId)
            .HasDatabaseName("ux_outbox_messages_message_id")
            .IsUnique();

        // The claim query, and the only query that runs constantly. Partial, so
        // the index covers the pending backlog rather than the entire history of
        // everything ever published -- which is what keeps it small once the
        // table has millions of rows in it.
        //
        // Keyed on id because the claim takes the oldest pending messages first,
        // ORDER BY id. An index on next_attempt_at cannot produce that order, so
        // PostgreSQL ignored it and walked the primary key from the first message
        // ever written, discarding every published row on the way: 263,053 rows
        // and 108 ms per claim once the table held 293,244 messages. Keyed on id,
        // the same scan starts at the oldest pending row and reads only pending
        // rows. See docs/PERFORMANCE.md.
        builder.HasIndex(message => message.Id)
            .HasDatabaseName("ix_outbox_messages_pending")
            .HasFilter("published_at IS NULL");
    }
}
