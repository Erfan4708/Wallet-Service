using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Ledger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledger.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="LedgerEntry"/> onto the <c>ledger_entries</c> table.</summary>
/// <remarks>
/// The table is append-only. Nothing here can express that — a database trigger
/// and revoked grants do, in the migration — but the mapping is deliberately
/// free of anything that would encourage an update: there is no concurrency
/// token, no modified timestamp, and every property is read-only in the domain.
/// </remarks>
internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    private const int MoneyPrecision = 19;
    private const int MoneyScale = 4;

    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ledger_entries");

        builder.HasKey(entry => entry.Id);

        // The one identifier in this system the database assigns. An entry is
        // never referenced externally — its business identity is the transaction
        // it belongs to, which is caller-supplied — so a monotonic integer buys
        // compact indexes, insertion-order locality, and a cheap cursor for the
        // future outbox to stream from.
        builder.Property(entry => entry.Id)
            .HasColumnName("id")
            .UseIdentityAlwaysColumn();

        builder.Property(entry => entry.TransactionId)
            .HasColumnName("transaction_id")
            .IsRequired();

        builder.Property(entry => entry.AccountId)
            .HasColumnName("account_id")
            .IsRequired();

        builder.Property(entry => entry.EntryIndex)
            .HasColumnName("entry_index")
            .IsRequired();

        builder.Property<DateTimeOffset>("RecordedAt")
            .HasColumnName("recorded_at")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.OwnsOne(entry => entry.Amount, amount =>
        {
            // Signed: positive increases the account's balance, negative
            // decreases it. numeric, never a floating point type — see ADR-004.
            amount.Property(money => money.Amount)
                .HasColumnName("amount")
                .HasPrecision(MoneyPrecision, MoneyScale)
                .IsRequired();

            amount.Property(money => money.Currency)
                .HasColumnName("currency")
                .HasConversion(currency => currency.ToString(), code => Enum.Parse<Currency>(code))
                .HasColumnType("character varying(3)")
                .IsRequired();
        });

        builder.Navigation(entry => entry.Amount).IsRequired();

        // An entry's position within its transaction is unique, which also gives
        // the index used to fetch a transaction's legs — so no separate index on
        // transaction_id is needed.
        builder.HasIndex(entry => new { entry.TransactionId, entry.EntryIndex })
            .HasDatabaseName("ux_ledger_entries_transaction_index")
            .IsUnique();

        // Serves both balance reconstruction (summing an account's entries) and
        // paging through its history. The identifier is monotonic, so ordering by
        // it is also chronological by insertion.
        builder.HasIndex(entry => new { entry.AccountId, entry.Id })
            .HasDatabaseName("ix_ledger_entries_account_id");

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(entry => entry.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
