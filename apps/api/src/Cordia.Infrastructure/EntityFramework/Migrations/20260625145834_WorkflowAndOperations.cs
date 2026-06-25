using System;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowAndOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .Annotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .Annotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .Annotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .Annotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal")
                .OldAnnotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .OldAnnotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .OldAnnotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .OldAnnotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .OldAnnotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .OldAnnotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified");

            migrationBuilder.CreateTable(
                name: "operations_work_items",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    work_item_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    sla_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assignee = table.Column<string>(type: "text", nullable: true),
                    resolution = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operations_work_items", x => new { x.tenant_id, x.work_item_id });
                    table.ForeignKey(
                        name: "fk_operations_work_items_booking_operations_tenant_id_operatio",
                        columns: x => new { x.tenant_id, x.operation_id },
                        principalSchema: "public",
                        principalTable: "booking_operations",
                        principalColumns: new[] { "tenant_id", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_attempts",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    attempt_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    workflow_step = table.Column<string>(type: "text", nullable: false),
                    provider_idempotency_key = table.Column<string>(type: "text", nullable: true),
                    provider_name = table.Column<string>(type: "text", nullable: false),
                    provider_account = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<AttemptOutcome>(type: "attempt_outcome_enum", nullable: false),
                    retry_classification = table.Column<RetryClassification>(type: "retry_classification_enum", nullable: true),
                    provider_ref = table.Column<string>(type: "text", nullable: true),
                    request_meta = table.Column<string>(type: "jsonb", nullable: false),
                    response_meta = table.Column<string>(type: "jsonb", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    next_eligible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciliation = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_attempts", x => new { x.tenant_id, x.attempt_id });
                });

            migrationBuilder.CreateTable(
                name: "workflow_steps",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    workflow_step_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    step_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<WorkflowStepStatus>(type: "workflow_step_status_enum", nullable: false),
                    provider_idempotency_key = table.Column<string>(type: "text", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_steps", x => new { x.tenant_id, x.workflow_step_id });
                    table.ForeignKey(
                        name: "fk_workflow_steps_booking_operations_tenant_id_operation_id",
                        columns: x => new { x.tenant_id, x.operation_id },
                        principalSchema: "public",
                        principalTable: "booking_operations",
                        principalColumns: new[] { "tenant_id", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_operations_work_items_tenant_id_operation_id",
                schema: "public",
                table: "operations_work_items",
                columns: new[] { "tenant_id", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "uq_workflow_step_per_type",
                schema: "public",
                table: "workflow_steps",
                columns: new[] { "tenant_id", "operation_id", "step_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operations_work_items",
                schema: "public");

            migrationBuilder.DropTable(
                name: "workflow_attempts",
                schema: "public");

            migrationBuilder.DropTable(
                name: "workflow_steps",
                schema: "public");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .Annotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .Annotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .Annotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .Annotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .OldAnnotation("Npgsql:Enum:attempt_outcome_enum", "abandoned,ambiguous,dispatching,planned,reconciled,rejected,succeeded,transient_failure")
                .OldAnnotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .OldAnnotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required")
                .OldAnnotation("Npgsql:Enum:idempotency_status_enum", "completed,failed,in_progress")
                .OldAnnotation("Npgsql:Enum:operation_status_enum", "accepted,alternatives_required,awaiting_payment,awaiting_user_action,booked,compensation_required,expired,failed_final,finalizing,held,manual_review,reconciling,rejected,reserving_capacity,validating,waitlisted")
                .OldAnnotation("Npgsql:Enum:operation_type_enum", "cancel,create,modify,reschedule")
                .OldAnnotation("Npgsql:Enum:retry_classification_enum", "ambiguous,authentication,authorization,conflict,internal_defect,no_capacity,payment_action,permanent_provider,policy,transient,validation")
                .OldAnnotation("Npgsql:Enum:verification_level_enum", "channel_verified,high_assurance,identity_verified,unverified")
                .OldAnnotation("Npgsql:Enum:workflow_step_status_enum", "failed,in_progress,pending,succeeded,terminal");
        }
    }
}
