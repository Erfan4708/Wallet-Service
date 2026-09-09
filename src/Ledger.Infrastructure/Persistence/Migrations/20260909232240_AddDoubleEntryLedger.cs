using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDoubleEntryLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_balance_not_negative",
                table: "accounts");

            migrationBuilder.AddColumn<string>(
                name: "account_type",
                table: "accounts",
                type: "character varying(16)",
                nullable: false,
                // Backfills any account that predates the ledger. The default is
                // dropped immediately below: a new account must state what it is
                // rather than silently becoming a wallet.
                defaultValue: "Wallet");

            migrationBuilder.Sql("ALTER TABLE accounts ALTER COLUMN account_type DROP DEFAULT;");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "accounts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "system_key",
                table: "accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ledger_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    external_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reverses_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_transactions", x => x.id);
                    table.ForeignKey(
                        name: "FK_ledger_transactions_ledger_transactions_reverses_transactio~",
                        column: x => x.reverses_transaction_id,
                        principalTable: "ledger_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", nullable: false),
                    entry_index = table.Column<short>(type: "smallint", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_ledger_entries_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ledger_entries_ledger_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "ledger_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_accounts_system_key",
                table: "accounts",
                column: "system_key",
                unique: true,
                filter: "system_key IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_balance_not_negative",
                table: "accounts",
                sql: "account_type = 'System' OR balance_amount >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_system_key_matches_type",
                table: "accounts",
                sql: "(account_type = 'System') = (system_key IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_type_is_known",
                table: "accounts",
                sql: "account_type IN ('Wallet', 'System')");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_account_id",
                table: "ledger_entries",
                columns: new[] { "account_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_ledger_entries_transaction_index",
                table: "ledger_entries",
                columns: new[] { "transaction_id", "entry_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ledger_transactions_idempotency_key",
                table: "ledger_transactions",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ledger_transactions_reverses",
                table: "ledger_transactions",
                column: "reverses_transaction_id",
                unique: true,
                filter: "reverses_transaction_id IS NOT NULL");

            // ----------------------------------------------------------------
            // Everything below is hand-written because EF Core cannot express it:
            // composite foreign keys reaching into an owned type's column,
            // deferred constraint triggers, privilege changes, and seed data.
            // ----------------------------------------------------------------

            migrationBuilder.Sql("""
                ALTER TABLE accounts
                    ADD CONSTRAINT ux_accounts_id_currency UNIQUE (id, balance_currency);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE ledger_transactions
                    ADD CONSTRAINT ux_ledger_transactions_id_currency UNIQUE (id, currency);
                """);

            // An entry must be in its account's currency, and in its
            // transaction's. Expressed as composite foreign keys, these make a
            // wrong-currency entry and a mixed-currency transaction impossible to
            // store at all -- not validated, not checked by a trigger, but
            // structurally unrepresentable. Foreign exchange will need these
            // relaxed, with a per-currency balance check in their place.
            migrationBuilder.Sql("""
                ALTER TABLE ledger_entries
                    ADD CONSTRAINT fk_ledger_entries_account_currency
                    FOREIGN KEY (account_id, currency)
                    REFERENCES accounts (id, balance_currency);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE ledger_entries
                    ADD CONSTRAINT fk_ledger_entries_transaction_currency
                    FOREIGN KEY (transaction_id, currency)
                    REFERENCES ledger_transactions (id, currency);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE ledger_entries
                    ADD CONSTRAINT ck_ledger_entries_amount_not_zero CHECK (amount <> 0);
                """);

            // The invariant the whole system exists to guarantee. It cannot be a
            // CHECK, because a CHECK cannot see other rows; and it cannot fire
            // immediately, because entries are inserted one at a time and the sum
            // is legitimately non-zero in between. A DEFERRABLE INITIALLY
            // DEFERRED constraint trigger runs at COMMIT, which is the only
            // moment at which the rule is meaningful.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ledger_assert_transaction_balances()
                RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    total numeric;
                    legs  integer;
                BEGIN
                    SELECT COALESCE(SUM(amount), 0), COUNT(*)
                      INTO total, legs
                      FROM ledger_entries
                     WHERE transaction_id = NEW.transaction_id;

                    IF legs < 2 THEN
                        RAISE EXCEPTION
                            'Transaction % has % ledger entry(s); double-entry requires at least two.',
                            NEW.transaction_id, legs;
                    END IF;

                    IF total <> 0 THEN
                        RAISE EXCEPTION
                            'Transaction % does not balance: its entries sum to %.',
                            NEW.transaction_id, total;
                    END IF;

                    RETURN NULL;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE CONSTRAINT TRIGGER ledger_entries_balanced
                AFTER INSERT ON ledger_entries
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION ledger_assert_transaction_balances();
                """);

            // The ledger is the audit record, and a record that can be rewritten
            // is not one. Corrections are made by appending a reversal.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ledger_reject_mutation()
                RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'ledger_entries is append-only: % is not permitted.', TG_OP;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER ledger_entries_append_only
                BEFORE UPDATE OR DELETE ON ledger_entries
                FOR EACH ROW EXECUTE FUNCTION ledger_reject_mutation();
                """);

            // Defence in depth for a least-privilege application role. It has no
            // effect on a superuser, which is why the trigger above -- not this
            // -- is the portable guarantee.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE ON ledger_entries FROM PUBLIC;");

            // The settlement accounts. Without a counterparty a deposit would
            // create money from nothing, so these must exist before any deposit
            // can be recorded, which makes them schema rather than application
            // state. Their identifiers embed the ISO 4217 numeric code of the
            // currency, so a row is recognisable at a glance in a database dump.
            migrationBuilder.Sql("""
                INSERT INTO accounts (id, account_type, system_key, balance_amount, balance_currency)
                VALUES
                    ('00000000-0000-0000-0000-000000000840', 'System', 'SETTLEMENT:USD', 0, 'USD'),
                    ('00000000-0000-0000-0000-000000000978', 'System', 'SETTLEMENT:EUR', 0, 'EUR'),
                    ('00000000-0000-0000-0000-000000000364', 'System', 'SETTLEMENT:IRR', 0, 'IRR')
                ON CONFLICT (id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS ledger_entries_append_only ON ledger_entries;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS ledger_entries_balanced ON ledger_entries;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS ledger_reject_mutation();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS ledger_assert_transaction_balances();");
            migrationBuilder.Sql("DELETE FROM accounts WHERE account_type = 'System';");
            migrationBuilder.Sql("ALTER TABLE ledger_transactions DROP CONSTRAINT IF EXISTS ux_ledger_transactions_id_currency;");
            migrationBuilder.Sql("ALTER TABLE accounts DROP CONSTRAINT IF EXISTS ux_accounts_id_currency;");

            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "ledger_transactions");

            migrationBuilder.DropIndex(
                name: "ux_accounts_system_key",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_balance_not_negative",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_system_key_matches_type",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_type_is_known",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "account_type",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "system_key",
                table: "accounts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_balance_not_negative",
                table: "accounts",
                sql: "balance_amount >= 0");
        }
    }
}
