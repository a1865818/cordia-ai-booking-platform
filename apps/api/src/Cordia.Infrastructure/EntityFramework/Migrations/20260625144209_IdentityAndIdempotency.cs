using System;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class IdentityAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .Annotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .Annotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .OldAnnotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .OldAnnotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required");

            migrationBuilder.CreateTable(
                name: "booking_intents",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    intent_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: true),
                    operation_type = table.Column<OperationTypeEnum>(type: "operation_type_enum", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    superseded_by_intent_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_intents", x => new { x.tenant_id, x.intent_id });
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    operation_type = table.Column<OperationTypeEnum>(type: "operation_type_enum", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    processing_status = table.Column<IdempotencyStatus>(type: "idempotency_status_enum", nullable: false),
                    response_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    retention_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.tenant_id, x.idempotency_key });
                });

            migrationBuilder.CreateTable(
                name: "booking_intent_revisions",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    intent_id = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    canonical_payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<string>(type: "text", nullable: false),
                    tenant_policy_version = table.Column<string>(type: "text", nullable: false),
                    prior_revision = table.Column<int>(type: "integer", nullable: true),
                    supersession_reason = table.Column<string>(type: "text", nullable: true),
                    source_session_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_intent_revisions", x => new { x.tenant_id, x.intent_id, x.revision });
                    table.ForeignKey(
                        name: "fk_booking_intent_revisions_booking_intents_tenant_id_intent_id",
                        columns: x => new { x.tenant_id, x.intent_id },
                        principalSchema: "public",
                        principalTable: "booking_intents",
                        principalColumns: new[] { "tenant_id", "intent_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "booking_operations",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    intent_id = table.Column<string>(type: "text", nullable: false),
                    intent_revision = table.Column<int>(type: "integer", nullable: false),
                    operation_type = table.Column<OperationTypeEnum>(type: "operation_type_enum", nullable: false),
                    status = table.Column<OperationStatus>(type: "operation_status_enum", nullable: false),
                    superseded_operation_id = table.Column<string>(type: "text", nullable: true),
                    booking_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_operations", x => new { x.tenant_id, x.operation_id });
                    table.ForeignKey(
                        name: "fk_booking_operations_booking_intent_revisions_tenant_id_inten",
                        columns: x => new { x.tenant_id, x.intent_id, x.intent_revision },
                        principalSchema: "public",
                        principalTable: "booking_intent_revisions",
                        principalColumns: new[] { "tenant_id", "intent_id", "revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "intent_confirmations",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    confirmation_id = table.Column<string>(type: "text", nullable: false),
                    intent_id = table.Column<string>(type: "text", nullable: false),
                    intent_revision = table.Column<int>(type: "integer", nullable: false),
                    intent_payload_hash = table.Column<string>(type: "text", nullable: false),
                    identity_ref = table.Column<string>(type: "text", nullable: false),
                    confirmation_channel = table.Column<string>(type: "text", nullable: false),
                    source_turn_id = table.Column<string>(type: "text", nullable: false),
                    readback_version = table.Column<string>(type: "text", nullable: false),
                    disclosures = table.Column<string>(type: "jsonb", nullable: false),
                    verification_level = table.Column<VerificationLevel>(type: "verification_level_enum", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intent_confirmations", x => new { x.tenant_id, x.confirmation_id });
                    table.CheckConstraint("ck_confirmation_hash_binding", "intent_payload_hash is not null");
                    table.ForeignKey(
                        name: "fk_intent_confirmations_booking_intent_revisions_tenant_id_int",
                        columns: x => new { x.tenant_id, x.intent_id, x.intent_revision },
                        principalSchema: "public",
                        principalTable: "booking_intent_revisions",
                        principalColumns: new[] { "tenant_id", "intent_id", "revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_booking_operations_tenant_id_intent_id_intent_revision",
                schema: "public",
                table: "booking_operations",
                columns: new[] { "tenant_id", "intent_id", "intent_revision" });

            migrationBuilder.CreateIndex(
                name: "ix_intent_confirmations_tenant_id_intent_id_intent_revision",
                schema: "public",
                table: "intent_confirmations",
                columns: new[] { "tenant_id", "intent_id", "intent_revision" });

            // Defense-in-depth BEFORE INSERT trigger: re-reads the FK-target revision's
            // payload_hash and raises on mismatch or missing revision (§5.2, §22.1, Inv-1/10).
            // EF Core does not model triggers; this raw SQL lives in the migration.
            migrationBuilder.Sql(@"
create or replace function trg_check_confirmation_hash() returns trigger as $$
declare rev_hash text;
begin
  select payload_hash into rev_hash
    from public.booking_intent_revisions
   where tenant_id = NEW.tenant_id and intent_id = NEW.intent_id and revision = NEW.intent_revision;
  if rev_hash is null then
    raise exception 'confirmation %: target revision (%/%/%) not found',
      NEW.confirmation_id, NEW.tenant_id, NEW.intent_id, NEW.intent_revision;
  end if;
  if NEW.intent_payload_hash <> rev_hash then
    raise exception 'confirmation %: payload hash mismatch (got %, revision has %)',
      NEW.confirmation_id, NEW.intent_payload_hash, rev_hash;
  end if;
  return NEW;
end; $$ language plpgsql;

create trigger check_confirmation_hash
  before insert on public.intent_confirmations
  for each row execute function trg_check_confirmation_hash();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop trigger and function before tables are dropped.
            migrationBuilder.Sql(
                "drop trigger if exists check_confirmation_hash on public.intent_confirmations; " +
                "drop function if exists trg_check_confirmation_hash();");

            migrationBuilder.DropTable(
                name: "booking_operations",
                schema: "public");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "public");

            migrationBuilder.DropTable(
                name: "intent_confirmations",
                schema: "public");

            migrationBuilder.DropTable(
                name: "booking_intent_revisions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "booking_intents",
                schema: "public");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .OldAnnotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .OldAnnotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .OldAnnotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .OldAnnotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .OldAnnotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .OldAnnotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified");
        }
    }
}
