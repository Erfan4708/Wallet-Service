using Ledger.Domain.Entities;
using Ledger.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledger.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Account"/> onto the <c>accounts</c> table.
/// </summary>
/// <remarks>
/// All mapping lives here rather than as attributes on the entity. That is what
/// keeps the domain free of persistence concerns: <see cref="Account"/> has no
/// idea it is stored in a table, and a schema change touches this file only.
/// </remarks>
internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    /// <summary>
    /// Total digits and digits after the point for a monetary column.
    /// </summary>
    /// <remarks>
    /// <c>numeric</c> — never <c>double precision</c> — because it is an exact
    /// base-10 type, matching the <c>decimal</c> the domain uses. A binary
    /// floating point column would reintroduce, at the storage layer, exactly
    /// the drift the domain type was chosen to avoid.
    /// <para>
    /// Scale 4 rather than the 2 that today's currencies use, because currencies
    /// differ: the Kuwaiti dinar has three decimal places. Widening a numeric
    /// column on a large table later is a migration nobody wants to run, and the
    /// extra headroom permits nothing new — the domain remains the authority on
    /// what precision each currency may actually be paid in.
    /// </para>
    /// </remarks>
    private const int MoneyPrecision = 19;
    private const int MoneyScale = 4;

    public void Configure(EntityTypeBuilder<Account> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("accounts", table =>
        {
            // Defence in depth. The domain already refuses to overdraw an
            // account, but that guarantee only holds for changes that go through
            // the domain. This one also holds against a bad migration, a second
            // service, or someone at a psql prompt.
            //
            // It encodes a wallet's rule specifically. An overdraft or credit
            // account is *supposed* to go negative, so introducing one means
            // revisiting this constraint rather than working around it.
            // A wallet holds a customer's money and may never go negative. A
            // system account is the platform's position against the outside
            // world, where a negative balance is the normal state: it is how much
            // value has been issued into wallets. Introducing an overdraft
            // product means revisiting this, not working around it.
            table.HasCheckConstraint(
                "ck_accounts_balance_not_negative",
                "account_type = 'System' OR balance_amount >= 0");

            table.HasCheckConstraint(
                "ck_accounts_type_is_known",
                "account_type IN ('Wallet', 'System')");

            // A system account is meaningless without its key, and a wallet must
            // not have one.
            table.HasCheckConstraint(
                "ck_accounts_system_key_matches_type",
                "(account_type = 'System') = (system_key IS NOT NULL)");

            table.HasCheckConstraint(
                "ck_accounts_currency_is_known",
                "balance_currency IN ('USD', 'EUR', 'IRR')");
        });

        builder.HasKey(account => account.Id);

        builder.Property(account => account.Id)
            .HasColumnName("id")
            .ValueGeneratedNever(); // Identifiers come from the caller, not the database.

        builder.OwnsOne(account => account.Balance, balance =>
        {
            balance.Property(money => money.Amount)
                .HasColumnName("balance_amount")
                .HasPrecision(MoneyPrecision, MoneyScale)
                .IsRequired();

            // Stored as the ISO 4217 alpha code rather than the enum's numeric
            // value. A ledger row has to stay readable to an auditor and
            // portable to every external system that speaks ISO 4217, and text
            // gives that for the cost of two bytes. Crucially it is not the
            // enum's ordinal position, so reordering the enum cannot silently
            // change what a stored row means.
            balance.Property(money => money.Currency)
                .HasColumnName("balance_currency")
                .HasConversion(
                    currency => currency.ToString(),
                    code => Enum.Parse<Currency>(code))
                .HasColumnType("character varying(3)")
                .IsRequired();
        });

        builder.Property(account => account.Type)
            .HasColumnName("account_type")
            .HasConversion(type => type.ToString(), value => Enum.Parse<AccountType>(value))
            .HasColumnType("character varying(16)")
            .IsRequired();

        builder.Property(account => account.SystemKey)
            .HasColumnName("system_key")
            .HasMaxLength(64);

        builder.Property<DateTimeOffset>("CreatedAt")
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.HasIndex(account => account.SystemKey)
            .HasDatabaseName("ux_accounts_system_key")
            .IsUnique()
            .HasFilter("system_key IS NOT NULL");

        builder.Navigation(account => account.Balance).IsRequired();

        // Beyond the primary key and the system-account lookup there are no
        // indexes here: every access path finds an account by its identifier.
        //
        // The unique key on (id, currency) below carries no information the
        // primary key lacks. It exists so that ledger_entries can point a
        // composite foreign key at it, which is what makes posting a USD entry
        // to a EUR account impossible at the storage layer rather than merely
        // checked in code. That constraint is added in the migration, because it
        // spans an owned type's column and EF cannot express it here.
    }
}
