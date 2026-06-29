using System;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class CapacitySlotsAndHolds : Migration
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
                .Annotation("Npgsql:Enum:hold_status_enum", "active,expired,finalized,reconciling,released")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
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

            migrationBuilder.CreateTable(
                name: "capacity_holds",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    hold_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    workflow_step = table.Column<string>(type: "text", nullable: false),
                    slot_id = table.Column<string>(type: "text", nullable: true),
                    reserved_capacity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<HoldStatus>(type: "hold_status_enum", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    release_reason = table.Column<ReleaseReason>(type: "release_reason_enum", nullable: true),
                    provider_ref = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_capacity_holds", x => new { x.tenant_id, x.hold_id });
                    table.CheckConstraint("ck_capacity_holds_reserved_positive", "reserved_capacity > 0");
                });

            migrationBuilder.CreateTable(
                name: "capacity_slots",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    slot_id = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<string>(type: "text", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    total_capacity = table.Column<int>(type: "integer", nullable: false),
                    held_capacity = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    booked_capacity = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_capacity_slots", x => new { x.tenant_id, x.slot_id });
                    table.CheckConstraint("ck_capacity_slots_no_overcommit", "held_capacity + booked_capacity <= total_capacity");
                    table.CheckConstraint("ck_capacity_slots_nonneg", "total_capacity >= 0 AND held_capacity >= 0 AND booked_capacity >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_capacity_holds_expiry",
                schema: "public",
                table: "capacity_holds",
                column: "expires_at",
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "uq_active_hold_per_op_step",
                schema: "public",
                table: "capacity_holds",
                columns: new[] { "tenant_id", "operation_id", "workflow_step" },
                unique: true,
                filter: "status = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capacity_holds",
                schema: "public");

            migrationBuilder.DropTable(
                name: "capacity_slots",
                schema: "public");

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
                .OldAnnotation("Npgsql:Enum:catalog_status_enum", "approved,draft_ready,extracting,in_review,published,quarantined,scanning,superseded,uploaded")
                .OldAnnotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .OldAnnotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .OldAnnotation("Npgsql:Enum:deposit_status_enum", "created,expired,link_sent,paid")
                .OldAnnotation("Npgsql:Enum:dlq_replay_state_enum", "discarded,quarantined,replayed")
                .OldAnnotation("Npgsql:Enum:endpoint_status_enum", "active,disabled")
                .OldAnnotation("Npgsql:Enum:hold_status_enum", "active,expired,finalized,reconciling,released")
                .OldAnnotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .OldAnnotation("Npgsql:Enum:inbox_outcome_enum", "admitted,completed,rejected")
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
