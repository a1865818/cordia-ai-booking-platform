using System;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class CatalogsPaymentsWaitlistChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .Annotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .Annotation("Npgsql:Enum:deposit_status_enum", "created,expired,link_sent,paid")
                .Annotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .Annotation("Npgsql:Enum:endpoint_status_enum", "active,disabled")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .Annotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,request_accepted,still_pending,waitlist_offer")
                .Annotation("Npgsql:Enum:notification_state_enum", "accepted,delivered,failed_permanent,failed_transient,invalid_recipient,pending,sending,suppressed_duplicate")
                .Annotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .Annotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .Annotation("Npgsql:Enum:preorder_saga_state_enum", "authorizing_payment,committing_booking,committing_preorder,compensating_booking,compensating_hold,compensating_items,completed,failed_final,holding_capacity,manual_review,reconciling,reserving_items,revalidating")
                .Annotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .Annotation("Npgsql:Enum:scheduled_job_status_enum", "claimed,dead,done,pending")
                .Annotation("Npgsql:Enum:signal_type_enum", "directory_slug,email_recipient,messaging_recipient,phone_number,web_origin,widget_key")
                .Annotation("Npgsql:Enum:verification_action_enum", "cancel,modify,owner_override,read_sensitive,refund,use_payment_method")
                .Annotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .Annotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .Annotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal")
                .OldAnnotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .OldAnnotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .OldAnnotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .OldAnnotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .OldAnnotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .OldAnnotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .OldAnnotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,request_accepted,still_pending,waitlist_offer")
                .OldAnnotation("Npgsql:Enum:notification_state_enum", "accepted,delivered,failed_permanent,failed_transient,invalid_recipient,pending,sending,suppressed_duplicate")
                .OldAnnotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .OldAnnotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .OldAnnotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .OldAnnotation("Npgsql:Enum:scheduled_job_status_enum", "claimed,dead,done,pending")
                .OldAnnotation("Npgsql:Enum:verification_action_enum", "cancel,modify,owner_override,read_sensitive,refund,use_payment_method")
                .OldAnnotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .OldAnnotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal");

            migrationBuilder.CreateTable(
                name: "deposit_sessions",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    deposit_session_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    amount_minor = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    stripe_session_id = table.Column<string>(type: "text", nullable: true),
                    payment_link_url = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<DepositStatus>(type: "deposit_status_enum", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deposit_sessions", x => new { x.tenant_id, x.deposit_session_id });
                    table.ForeignKey(
                        name: "fk_deposit_sessions_booking_operations_tenant_id_operation_id",
                        columns: x => new { x.tenant_id, x.operation_id },
                        principalSchema: "public",
                        principalTable: "booking_operations",
                        principalColumns: new[] { "tenant_id", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "preorder_sagas",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    saga_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    saga_state = table.Column<PreorderSagaState>(type: "preorder_saga_state_enum", nullable: false),
                    preorder_reference = table.Column<string>(type: "jsonb", nullable: false),
                    hold_id = table.Column<string>(type: "text", nullable: true),
                    deposit_session_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_preorder_sagas", x => new { x.tenant_id, x.saga_id });
                    table.ForeignKey(
                        name: "fk_preorder_sagas_booking_operations_tenant_id_operation_id",
                        columns: x => new { x.tenant_id, x.operation_id },
                        principalSchema: "public",
                        principalTable: "booking_operations",
                        principalColumns: new[] { "tenant_id", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_channel_endpoints",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    channel_endpoint_id = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<TenantChannel>(type: "channel_enum", nullable: false),
                    signal_type = table.Column<SignalType>(type: "signal_type_enum", nullable: false),
                    signal_value = table.Column<string>(type: "text", nullable: false),
                    configuration_version = table.Column<string>(type: "text", nullable: false),
                    policy_bundle_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<EndpointStatus>(type: "endpoint_status_enum", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_channel_endpoints", x => new { x.tenant_id, x.channel_endpoint_id });
                });

            migrationBuilder.CreateTable(
                name: "vendor_catalogs",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    catalog_id = table.Column<string>(type: "text", nullable: false),
                    version_label = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<CatalogStatus>(type: "catalog_status_enum", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    source_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vendor_catalogs", x => new { x.tenant_id, x.catalog_id });
                });

            migrationBuilder.CreateTable(
                name: "waitlist_enrollments",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    enrollment_id = table.Column<string>(type: "text", nullable: false),
                    customer_ref = table.Column<string>(type: "text", nullable: false),
                    acceptable_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acceptable_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    party_size = table.Column<int>(type: "integer", nullable: false),
                    location_id = table.Column<string>(type: "text", nullable: false),
                    service_id = table.Column<string>(type: "text", nullable: false),
                    offer_lifetime_seconds = table.Column<int>(type: "integer", nullable: false),
                    deposit_terms = table.Column<string>(type: "jsonb", nullable: true),
                    notification_channel = table.Column<string>(type: "text", nullable: false),
                    auto_book = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waitlist_enrollments", x => new { x.tenant_id, x.enrollment_id });
                });

            migrationBuilder.CreateTable(
                name: "vendor_catalog_items",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    item_id = table.Column<string>(type: "text", nullable: false),
                    catalog_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    price_minor = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    menu_metadata = table.Column<string>(type: "jsonb", nullable: true),
                    allergen_info = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vendor_catalog_items", x => new { x.tenant_id, x.item_id });
                    table.ForeignKey(
                        name: "fk_vendor_catalog_items_vendor_catalogs_tenant_id_catalog_id",
                        columns: x => new { x.tenant_id, x.catalog_id },
                        principalSchema: "public",
                        principalTable: "vendor_catalogs",
                        principalColumns: new[] { "tenant_id", "catalog_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "waitlist_offers",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    offer_id = table.Column<string>(type: "text", nullable: false),
                    enrollment_id = table.Column<string>(type: "text", nullable: false),
                    hold_id = table.Column<string>(type: "text", nullable: true),
                    operation_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<WaitlistOfferStatus>(type: "waitlist_offer_status_enum", nullable: false),
                    offer_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    matched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    offered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waitlist_offers", x => new { x.tenant_id, x.offer_id });
                    table.ForeignKey(
                        name: "fk_waitlist_offers_waitlist_enrollments_tenant_id_enrollment_id",
                        columns: x => new { x.tenant_id, x.enrollment_id },
                        principalSchema: "public",
                        principalTable: "waitlist_enrollments",
                        principalColumns: new[] { "tenant_id", "enrollment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deposit_sessions_expiry",
                schema: "public",
                table: "deposit_sessions",
                columns: new[] { "tenant_id", "expires_at" },
                filter: "status = 'link_sent'");

            migrationBuilder.CreateIndex(
                name: "uq_deposit_session_per_operation",
                schema: "public",
                table: "deposit_sessions",
                columns: new[] { "tenant_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_preorder_sagas_tenant_id_operation_id",
                schema: "public",
                table: "preorder_sagas",
                columns: new[] { "tenant_id", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "uq_channel_signal_active",
                schema: "public",
                table: "tenant_channel_endpoints",
                columns: new[] { "channel", "signal_type", "signal_value" },
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_vendor_catalog_items_tenant_id_catalog_id",
                schema: "public",
                table: "vendor_catalog_items",
                columns: new[] { "tenant_id", "catalog_id" });

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_offers_tenant_id_enrollment_id",
                schema: "public",
                table: "waitlist_offers",
                columns: new[] { "tenant_id", "enrollment_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deposit_sessions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "preorder_sagas",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tenant_channel_endpoints",
                schema: "public");

            migrationBuilder.DropTable(
                name: "vendor_catalog_items",
                schema: "public");

            migrationBuilder.DropTable(
                name: "waitlist_offers",
                schema: "public");

            migrationBuilder.DropTable(
                name: "vendor_catalogs",
                schema: "public");

            migrationBuilder.DropTable(
                name: "waitlist_enrollments",
                schema: "public");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .Annotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .Annotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,request_accepted,still_pending,waitlist_offer")
                .Annotation("Npgsql:Enum:notification_state_enum", "accepted,delivered,failed_permanent,failed_transient,invalid_recipient,pending,sending,suppressed_duplicate")
                .Annotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .Annotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .Annotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .Annotation("Npgsql:Enum:scheduled_job_status_enum", "claimed,dead,done,pending")
                .Annotation("Npgsql:Enum:verification_action_enum", "cancel,modify,owner_override,read_sensitive,refund,use_payment_method")
                .Annotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .Annotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal")
                .OldAnnotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .OldAnnotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
                .OldAnnotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .OldAnnotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .OldAnnotation("Npgsql:Enum:deposit_status_enum", "created,expired,link_sent,paid")
                .OldAnnotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .OldAnnotation("Npgsql:Enum:endpoint_status_enum", "active,disabled")
                .OldAnnotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .OldAnnotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .OldAnnotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,request_accepted,still_pending,waitlist_offer")
                .OldAnnotation("Npgsql:Enum:notification_state_enum", "accepted,delivered,failed_permanent,failed_transient,invalid_recipient,pending,sending,suppressed_duplicate")
                .OldAnnotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .OldAnnotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .OldAnnotation("Npgsql:Enum:preorder_saga_state_enum", "authorizing_payment,committing_booking,committing_preorder,compensating_booking,compensating_hold,compensating_items,completed,failed_final,holding_capacity,manual_review,reconciling,reserving_items,revalidating")
                .OldAnnotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .OldAnnotation("Npgsql:Enum:scheduled_job_status_enum", "claimed,dead,done,pending")
                .OldAnnotation("Npgsql:Enum:signal_type_enum", "directory_slug,email_recipient,messaging_recipient,phone_number,web_origin,widget_key")
                .OldAnnotation("Npgsql:Enum:verification_action_enum", "cancel,modify,owner_override,read_sensitive,refund,use_payment_method")
                .OldAnnotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .OldAnnotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .OldAnnotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal");
        }
    }
}
