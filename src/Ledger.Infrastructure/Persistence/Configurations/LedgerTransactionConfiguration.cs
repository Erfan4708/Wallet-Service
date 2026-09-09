using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledger.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="LedgerTransaction"/> onto the <c>ledger_transactions</c> table.</summary>
internal sealed class LedgerTransactionConfiguration : IEntityTypeConfiguration<LedgerTransaction>
{
    public void Configure(EntityTypeBuilder<LedgerTransaction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ledger_transactions");

        builder.HasKey(transaction => transaction.Id);

        builder.Property(transaction => transaction.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(transaction => transaction.Kind)
            .HasColumnName("kind")
            .HasConversion(kind => kind.ToString(), value => Enum.Parse<LedgerTransactionKind>(value))
            .HasColumnType("character varying(16)")
            .IsRequired();

        // Denormalised onto the transaction so that a composite foreign key can
        // force every entry to share it. That makes a mixed-currency transaction
        // impossible to store rather than merely rejected in code.
        builder.Property(transaction => transaction.Currency)
            .HasColumnName("currency")
            .HasConversion(currency => currency.ToString(), code => Enum.Parse<Currency>(code))
            .HasColumnType("character varying(3)")
            .IsRequired();

        builder.Property(transaction => transaction.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        // Stamped by the database, not the application: the booking time should
        // be the database's own clock, so it cannot drift between hosts.
        builder.Property<DateTimeOffset>("RecordedAt")
            .HasColumnName("recorded_at")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.Property(transaction => transaction.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(200);

        builder.Property(transaction => transaction.ExternalReference)
            .HasColumnName("external_reference")
            .HasMaxLength(200);

        builder.Property(transaction => transaction.ReversesTransactionId)
            .HasColumnName("reverses_transaction_id");

        // Partial, so that the many transactions without a key do not collide on
        // NULL and do not bloat the index.
        builder.HasIndex(transaction => transaction.IdempotencyKey)
            .HasDatabaseName("ux_ledger_transactions_idempotency_key")
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL");

        // A transaction may be reversed at most once. Reversing twice would
        // return the accounts to a state that never legitimately existed.
        builder.HasIndex(transaction => transaction.ReversesTransactionId)
            .HasDatabaseName("ux_ledger_transactions_reverses")
            .IsUnique()
            .HasFilter("reverses_transaction_id IS NOT NULL");

        builder.HasOne<LedgerTransaction>()
            .WithMany()
            .HasForeignKey(transaction => transaction.ReversesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(transaction => transaction.Entries)
            .WithOne()
            .HasForeignKey(entry => entry.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(transaction => transaction.Entries)
            .HasField("_entries")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Domain events live only in memory, on their way to the outbox.
        builder.Ignore(transaction => transaction.DomainEvents);
        builder.Ignore(transaction => transaction.Total);
    }
}
