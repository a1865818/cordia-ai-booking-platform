using System;
using Cordia.Domain.Generated;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class VerificationChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "verification_challenges",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    verification_id = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    customer_id = table.Column<string>(type: "text", nullable: true),
                    target_booking_id = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<VerificationActionEnum>(type: "verification_action_enum", nullable: false),
                    channel = table.Column<int>(type: "integer", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    grant_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_verification_challenges", x => new { x.tenant_id, x.verification_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_verification_challenges_tenant_id_expires_at",
                schema: "public",
                table: "verification_challenges",
                columns: new[] { "tenant_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_verification_challenges_tenant_id_idempotency_key",
                schema: "public",
                table: "verification_challenges",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "verification_challenges",
                schema: "public");
        }
    }
}
