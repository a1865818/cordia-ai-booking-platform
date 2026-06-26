using System;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_enum", "email,messaging,voice,web")
                .Annotation("Npgsql:Enum:consent_state", "alternative_offered,declined,granted,not_required");

            migrationBuilder.CreateTable(
                name: "conversation_sessions",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<TenantChannel>(type: "channel_enum", nullable: false),
                    consent_state = table.Column<ConsentState>(type: "consent_state", nullable: true),
                    consent_policy_version = table.Column<string>(type: "text", nullable: true),
                    jurisdiction_policy_id = table.Column<string>(type: "text", nullable: true),
                    recording_allowed = table.Column<bool>(type: "boolean", nullable: true),
                    consent_recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    customer_ref = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_sessions", x => new { x.tenant_id, x.session_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_sessions",
                schema: "public");
        }
    }
}
