using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class GuestContactStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guest_contacts",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    contact_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    phone_e164 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    contact_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guest_contacts", x => new { x.tenant_id, x.contact_ref });
                });

            migrationBuilder.CreateIndex(
                name: "ix_guest_contacts_tenant_id_contact_fingerprint",
                schema: "public",
                table: "guest_contacts",
                columns: new[] { "tenant_id", "contact_fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guest_contacts_tenant_id_created_at",
                schema: "public",
                table: "guest_contacts",
                columns: new[] { "tenant_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guest_contacts",
                schema: "public");
        }
    }
}
