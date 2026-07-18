using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddCorrelationToChainTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "causation_id",
                schema: "public",
                table: "workflow_attempts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "public",
                table: "workflow_attempts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "causation_id",
                schema: "public",
                table: "scheduled_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "public",
                table: "scheduled_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "causation_id",
                schema: "public",
                table: "operations_work_items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "public",
                table: "operations_work_items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "causation_id",
                schema: "public",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "public",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "causation_id",
                schema: "public",
                table: "intent_confirmations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "public",
                table: "intent_confirmations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "causation_id",
                schema: "public",
                table: "booking_operations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "public",
                table: "booking_operations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "causation_id",
                schema: "public",
                table: "workflow_attempts");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "public",
                table: "workflow_attempts");

            migrationBuilder.DropColumn(
                name: "causation_id",
                schema: "public",
                table: "scheduled_jobs");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "public",
                table: "scheduled_jobs");

            migrationBuilder.DropColumn(
                name: "causation_id",
                schema: "public",
                table: "operations_work_items");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "public",
                table: "operations_work_items");

            migrationBuilder.DropColumn(
                name: "causation_id",
                schema: "public",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "public",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "causation_id",
                schema: "public",
                table: "intent_confirmations");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "public",
                table: "intent_confirmations");

            migrationBuilder.DropColumn(
                name: "causation_id",
                schema: "public",
                table: "booking_operations");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "public",
                table: "booking_operations");
        }
    }
}
