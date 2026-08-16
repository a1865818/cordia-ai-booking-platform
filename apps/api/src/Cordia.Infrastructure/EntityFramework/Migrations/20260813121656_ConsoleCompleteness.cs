using System;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class ConsoleCompleteness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .Annotation("Npgsql:Enum:attendance_status_enum", "attended,no_show,unknown")
                .Annotation("Npgsql:Enum:attributed_source_enum", "agent,operator")
                .Annotation("Npgsql:Enum:booking_lifecycle_status_enum", "cancellation_pending,cancelled,confirmed,modification_pending")
                .Annotation("Npgsql:Enum:campaign_status_enum", "awaiting,rejected,scheduled,sent")
                .Annotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extract_failed,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .Annotation("Npgsql:Enum:deposit_status_enum", "created,expired,link_sent,paid,refunded")
                .Annotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .Annotation("Npgsql:Enum:endpoint_status_enum", "active,disabled")
                .Annotation("Npgsql:Enum:hold_status_enum", "active,expired,finalized,reconciling,released")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .Annotation("Npgsql:Enum:knowledge_status_enum", "answered,needs-answer")
                .Annotation("Npgsql:Enum:message_priority_enum", "critical,high,normal")
                .Annotation("Npgsql:Enum:notification_channel_enum", "channel,email,sms")
                .Annotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,preorder_cancelled,request_accepted,still_pending,waitlist_offer,waitlist_offer_expired")
                .Annotation("Npgsql:Enum:notification_state_enum", "accepted,delivered,failed_permanent,failed_transient,invalid_recipient,pending,sending,suppressed_duplicate")
                .Annotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .Annotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .Annotation("Npgsql:Enum:preorder_saga_state_enum", "authorizing_payment,committing_booking,committing_preorder,compensating_booking,compensating_hold,compensating_items,completed,failed_final,holding_capacity,manual_review,reconciling,reserving_items,revalidating")
                .Annotation("Npgsql:Enum:release_reason_enum", "compensated,expired,finalized,provider_released,superseded")
                .Annotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .Annotation("Npgsql:Enum:scheduled_job_status_enum", "claimed,dead,done,pending")
                .Annotation("Npgsql:Enum:signal_type_enum", "directory_slug,email_recipient,messaging_recipient,phone_number,web_origin,widget_key")
                .Annotation("Npgsql:Enum:verification_action_enum", "cancel,modify,owner_override,read_sensitive,refund,use_payment_method")
                .Annotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .Annotation("Npgsql:Enum:verification_request_channel_enum", "email,sms")
                .Annotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .Annotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal")
                .OldAnnotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .OldAnnotation("Npgsql:Enum:booking_lifecycle_status_enum", "cancellation_pending,cancelled,confirmed,modification_pending")
                .OldAnnotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extract_failed,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
                .OldAnnotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .OldAnnotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .OldAnnotation("Npgsql:Enum:deposit_status_enum", "created,expired,link_sent,paid")
                .OldAnnotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .OldAnnotation("Npgsql:Enum:endpoint_status_enum", "active,disabled")
                .OldAnnotation("Npgsql:Enum:hold_status_enum", "active,expired,finalized,reconciling,released")
                .OldAnnotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .OldAnnotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .OldAnnotation("Npgsql:Enum:message_priority_enum", "critical,high,normal")
                .OldAnnotation("Npgsql:Enum:notification_channel_enum", "channel,email,sms")
                .OldAnnotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,preorder_cancelled,request_accepted,still_pending,waitlist_offer,waitlist_offer_expired")
                .OldAnnotation("Npgsql:Enum:notification_state_enum", "accepted,delivered,failed_permanent,failed_transient,invalid_recipient,pending,sending,suppressed_duplicate")
                .OldAnnotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .OldAnnotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .OldAnnotation("Npgsql:Enum:preorder_saga_state_enum", "authorizing_payment,committing_booking,committing_preorder,compensating_booking,compensating_hold,compensating_items,completed,failed_final,holding_capacity,manual_review,reconciling,reserving_items,revalidating")
                .OldAnnotation("Npgsql:Enum:release_reason_enum", "compensated,expired,finalized,provider_released,superseded")
                .OldAnnotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .OldAnnotation("Npgsql:Enum:scheduled_job_status_enum", "claimed,dead,done,pending")
                .OldAnnotation("Npgsql:Enum:signal_type_enum", "directory_slug,email_recipient,messaging_recipient,phone_number,web_origin,widget_key")
                .OldAnnotation("Npgsql:Enum:verification_action_enum", "cancel,modify,owner_override,read_sensitive,refund,use_payment_method")
                .OldAnnotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .OldAnnotation("Npgsql:Enum:verification_request_channel_enum", "email,sms")
                .OldAnnotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .OldAnnotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal");

            // PostgreSQL cannot drop an enum label, and some Npgsql versions only rewrite the
            // model annotation. ADD VALUE IF NOT EXISTS is the durable way to append Refunded
            // (same approach as AddExtractFailedCatalogStatus for catalog_status_enum).
            migrationBuilder.Sql("ALTER TYPE deposit_status_enum ADD VALUE IF NOT EXISTS 'refunded';");

            migrationBuilder.AddColumn<int>(
                name: "duration_minutes",
                schema: "public",
                table: "vendor_catalog_items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preview",
                schema: "public",
                table: "conversation_sessions",
                type: "character varying(280)",
                maxLength: 280,
                nullable: true);

            migrationBuilder.AddColumn<AttendanceStatus>(
                name: "attendance",
                schema: "public",
                table: "bookings",
                type: "attendance_status_enum",
                nullable: false,
                defaultValue: AttendanceStatus.Unknown);

            migrationBuilder.AddColumn<AttributedSource>(
                name: "attributed_source",
                schema: "public",
                table: "bookings",
                type: "attributed_source_enum",
                nullable: true);

            migrationBuilder.AddColumn<AttributedSource>(
                name: "attributed_source",
                schema: "public",
                table: "booking_operations",
                type: "attributed_source_enum",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_guardrails",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    toggles_json = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_guardrails", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "agent_knowledge_entries",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    entry_id = table.Column<string>(type: "text", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<KnowledgeStatus>(type: "knowledge_status_enum", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_knowledge_entries", x => new { x.tenant_id, x.entry_id });
                });

            migrationBuilder.CreateTable(
                name: "campaigns",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    campaign_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<CampaignStatus>(type: "campaign_status_enum", nullable: false, defaultValue: CampaignStatus.Awaiting),
                    audience_description = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<TenantChannel>(type: "channel_enum", nullable: false),
                    recipients_estimate = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaigns", x => new { x.tenant_id, x.campaign_id });
                });

            migrationBuilder.CreateTable(
                name: "customer_operator_notes",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    customer_ref = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    tags_json = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_operator_notes", x => new { x.tenant_id, x.customer_ref });
                });

            migrationBuilder.CreateTable(
                name: "operators",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    operator_id = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operators", x => new { x.tenant_id, x.operator_id });
                });

            migrationBuilder.CreateTable(
                name: "staff_resources",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_resources", x => new { x.tenant_id, x.resource_id });
                });

            migrationBuilder.CreateTable(
                name: "tenant_deposit_policies",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    require = table.Column<bool>(type: "boolean", nullable: false),
                    amount_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    no_show_only = table.Column<bool>(type: "boolean", nullable: false),
                    refund_window = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true, defaultValue: "AUD"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_deposit_policies", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_profiles",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    business_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    waitlist_auto_book = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    hours_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_profiles", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "operator_sessions",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    operator_id = table.Column<string>(type: "text", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operator_sessions", x => new { x.tenant_id, x.session_id });
                    table.ForeignKey(
                        name: "fk_operator_sessions_operators_tenant_id_operator_id",
                        columns: x => new { x.tenant_id, x.operator_id },
                        principalSchema: "public",
                        principalTable: "operators",
                        principalColumns: new[] { "tenant_id", "operator_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_operator_sessions_session_id",
                schema: "public",
                table: "operator_sessions",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_operator_sessions_tenant_id_operator_id",
                schema: "public",
                table: "operator_sessions",
                columns: new[] { "tenant_id", "operator_id" });

            migrationBuilder.CreateIndex(
                name: "uq_operators_tenant_email",
                schema: "public",
                table: "operators",
                columns: new[] { "tenant_id", "email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_guardrails",
                schema: "public");

            migrationBuilder.DropTable(
                name: "agent_knowledge_entries",
                schema: "public");

            migrationBuilder.DropTable(
                name: "campaigns",
                schema: "public");

            migrationBuilder.DropTable(
                name: "customer_operator_notes",
                schema: "public");

            migrationBuilder.DropTable(
                name: "operator_sessions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "staff_resources",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tenant_deposit_policies",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tenant_profiles",
                schema: "public");

            migrationBuilder.DropTable(
                name: "operators",
                schema: "public");

            migrationBuilder.DropColumn(
                name: "duration_minutes",
                schema: "public",
                table: "vendor_catalog_items");

            migrationBuilder.DropColumn(
                name: "preview",
                schema: "public",
                table: "conversation_sessions");

            migrationBuilder.DropColumn(
                name: "attendance",
                schema: "public",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "attributed_source",
                schema: "public",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "attributed_source",
                schema: "public",
                table: "booking_operations");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .Annotation("Npgsql:Enum:booking_lifecycle_status_enum", "cancellation_pending,cancelled,confirmed,modification_pending")
                .Annotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extract_failed,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                // KEEPS `refunded`. PostgreSQL cannot remove an enum label, so asking
                // Npgsql to drop it fails before any SQL is even generated — the whole rollback
                // aborts. Leaving the label in place is the honest and safe rollback.
                .Annotation("Npgsql:Enum:deposit_status_enum", "created,expired,link_sent,paid,refunded")
                .Annotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .Annotation("Npgsql:Enum:endpoint_status_enum", "active,disabled")
                .Annotation("Npgsql:Enum:hold_status_enum", "active,expired,finalized,reconciling,released")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .Annotation("Npgsql:Enum:message_priority_enum", "critical,high,normal")
                .Annotation("Npgsql:Enum:notification_channel_enum", "channel,email,sms")
                .Annotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,preorder_cancelled,request_accepted,still_pending,waitlist_offer,waitlist_offer_expired")
                .Annotation("Npgsql:Enum:notification_state_enum", "accepted,delivered,failed_permanent,failed_transient,invalid_recipient,pending,sending,suppressed_duplicate")
                .Annotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .Annotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .Annotation("Npgsql:Enum:preorder_saga_state_enum", "authorizing_payment,committing_booking,committing_preorder,compensating_booking,compensating_hold,compensating_items,completed,failed_final,holding_capacity,manual_review,reconciling,reserving_items,revalidating")
                .Annotation("Npgsql:Enum:release_reason_enum", "compensated,expired,finalized,provider_released,superseded")
                .Annotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .Annotation("Npgsql:Enum:scheduled_job_status_enum", "claimed,dead,done,pending")
                .Annotation("Npgsql:Enum:signal_type_enum", "directory_slug,email_recipient,messaging_recipient,phone_number,web_origin,widget_key")
                .Annotation("Npgsql:Enum:verification_action_enum", "cancel,modify,owner_override,read_sensitive,refund,use_payment_method")
                .Annotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .Annotation("Npgsql:Enum:verification_request_channel_enum", "email,sms")
                .Annotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .Annotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal")
                .OldAnnotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .OldAnnotation("Npgsql:Enum:attendance_status_enum", "attended,no_show,unknown")
                .OldAnnotation("Npgsql:Enum:attributed_source_enum", "agent,operator")
                .OldAnnotation("Npgsql:Enum:booking_lifecycle_status_enum", "cancellation_pending,cancelled,confirmed,modification_pending")
                .OldAnnotation("Npgsql:Enum:campaign_status_enum", "awaiting,rejected,scheduled,sent")
                .OldAnnotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extract_failed,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
                .OldAnnotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .OldAnnotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .OldAnnotation("Npgsql:Enum:deposit_status_enum", "created,expired,link_sent,paid,refunded")
                .OldAnnotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .OldAnnotation("Npgsql:Enum:endpoint_status_enum", "active,disabled")
                .OldAnnotation("Npgsql:Enum:hold_status_enum", "active,expired,finalized,reconciling,released")
                .OldAnnotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .OldAnnotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .OldAnnotation("Npgsql:Enum:knowledge_status_enum", "answered,needs-answer")
                .OldAnnotation("Npgsql:Enum:message_priority_enum", "critical,high,normal")
                .OldAnnotation("Npgsql:Enum:notification_channel_enum", "channel,email,sms")
                .OldAnnotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,preorder_cancelled,request_accepted,still_pending,waitlist_offer,waitlist_offer_expired")
                .OldAnnotation("Npgsql:Enum:notification_state_enum", "accepted,delivered,failed_permanent,failed_transient,invalid_recipient,pending,sending,suppressed_duplicate")
                .OldAnnotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .OldAnnotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .OldAnnotation("Npgsql:Enum:preorder_saga_state_enum", "authorizing_payment,committing_booking,committing_preorder,compensating_booking,compensating_hold,compensating_items,completed,failed_final,holding_capacity,manual_review,reconciling,reserving_items,revalidating")
                .OldAnnotation("Npgsql:Enum:release_reason_enum", "compensated,expired,finalized,provider_released,superseded")
                .OldAnnotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .OldAnnotation("Npgsql:Enum:scheduled_job_status_enum", "claimed,dead,done,pending")
                .OldAnnotation("Npgsql:Enum:signal_type_enum", "directory_slug,email_recipient,messaging_recipient,phone_number,web_origin,widget_key")
                .OldAnnotation("Npgsql:Enum:verification_action_enum", "cancel,modify,owner_override,read_sensitive,refund,use_payment_method")
                .OldAnnotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .OldAnnotation("Npgsql:Enum:verification_request_channel_enum", "email,sms")
                .OldAnnotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .OldAnnotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal");
        }
    }
}
