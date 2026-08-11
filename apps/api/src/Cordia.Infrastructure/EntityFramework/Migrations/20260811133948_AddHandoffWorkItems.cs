using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddHandoffWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "handoff_work_items",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    work_item_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    handoff_idempotency_key = table.Column<string>(type: "text", nullable: false),
                    queue = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    recommended_action = table.Column<string>(type: "text", nullable: true),
                    assignee = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    causation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_handoff_work_items", x => new { x.tenant_id, x.work_item_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_handoff_work_items_session",
                schema: "public",
                table: "handoff_work_items",
                columns: new[] { "tenant_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "uq_handoff_idempotency_key",
                schema: "public",
                table: "handoff_work_items",
                columns: new[] { "tenant_id", "handoff_idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "handoff_work_items",
                schema: "public");
        }
    }
}
