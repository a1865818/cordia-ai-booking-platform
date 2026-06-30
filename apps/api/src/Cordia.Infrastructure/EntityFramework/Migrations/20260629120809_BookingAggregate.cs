using System;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class BookingAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .Annotation("Npgsql:Enum:booking_lifecycle_status_enum", "cancellation_pending,cancelled,confirmed,modification_pending")
                .Annotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .Annotation("Npgsql:Enum:deposit_status_enum", "created,expired,link_sent,paid")
                .Annotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .Annotation("Npgsql:Enum:endpoint_status_enum", "active,disabled")
                .Annotation("Npgsql:Enum:hold_status_enum", "active,expired,finalized,reconciling,released")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .Annotation("Npgsql:Enum:message_priority_enum", "critical,high,normal")
                .Annotation("Npgsql:Enum:notification_channel_enum", "channel,email,sms")
                .Annotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,request_accepted,still_pending,waitlist_offer")
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
                .Annotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .Annotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal")
                .OldAnnotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .OldAnnotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
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
                .OldAnnotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,request_accepted,still_pending,waitlist_offer")
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
                .OldAnnotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .OldAnnotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal");

            migrationBuilder.CreateTable(
                name: "bookings",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    booking_id = table.Column<string>(type: "text", nullable: false),
                    booking_reference = table.Column<string>(type: "text", nullable: false),
                    provider_reference = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    lifecycle_status = table.Column<BookingLifecycleStatus>(type: "booking_lifecycle_status_enum", nullable: false),
                    customer_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bookings", x => new { x.tenant_id, x.booking_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_bookings_tenant_id_booking_reference",
                schema: "public",
                table: "bookings",
                columns: new[] { "tenant_id", "booking_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bookings_tenant_id_customer_id",
                schema: "public",
                table: "bookings",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bookings_tenant_id_provider_reference",
                schema: "public",
                table: "bookings",
                columns: new[] { "tenant_id", "provider_reference" },
                filter: "provider_reference IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bookings",
                schema: "public");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .Annotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .Annotation("Npgsql:Enum:deposit_status_enum", "created,expired,link_sent,paid")
                .Annotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .Annotation("Npgsql:Enum:endpoint_status_enum", "active,disabled")
                .Annotation("Npgsql:Enum:hold_status_enum", "active,expired,finalized,reconciling,released")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
                .Annotation("Npgsql:Enum:message_priority_enum", "critical,high,normal")
                .Annotation("Npgsql:Enum:notification_channel_enum", "channel,email,sms")
                .Annotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,request_accepted,still_pending,waitlist_offer")
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
                .Annotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .Annotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal")
                .OldAnnotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .OldAnnotation("Npgsql:Enum:booking_lifecycle_status_enum", "cancellation_pending,cancelled,confirmed,modification_pending")
                .OldAnnotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
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
                .OldAnnotation("Npgsql:Enum:notification_event_enum", "action_required,booking_confirmed,cancel_or_modify_confirmed,final_failure,no_capacity_alternatives,request_accepted,still_pending,waitlist_offer")
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
                .OldAnnotation("Npgsql:Enum:waitlist_offer_status_enum", "accepted,declined,expired,matched,offered,pending,released")
                .OldAnnotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal");
        }
    }
}
