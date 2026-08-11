using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class GuestContactRetentionClockAndCatalogStatusClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_guest_contacts_tenant_id_created_at",
                schema: "public",
                table: "guest_contacts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_changed_at",
                schema: "public",
                table: "vendor_catalogs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "email_fingerprint",
                schema: "public",
                table: "guest_contacts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_used_at",
                schema: "public",
                table: "guest_contacts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "phone_fingerprint",
                schema: "public",
                table: "guest_contacts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_guest_contacts_tenant_id_email_fingerprint",
                schema: "public",
                table: "guest_contacts",
                columns: new[] { "tenant_id", "email_fingerprint" });

            migrationBuilder.CreateIndex(
                name: "ix_guest_contacts_tenant_id_last_used_at",
                schema: "public",
                table: "guest_contacts",
                columns: new[] { "tenant_id", "last_used_at" });

            migrationBuilder.CreateIndex(
                name: "ix_guest_contacts_tenant_id_phone_fingerprint",
                schema: "public",
                table: "guest_contacts",
                columns: new[] { "tenant_id", "phone_fingerprint" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_guest_contacts_tenant_id_email_fingerprint",
                schema: "public",
                table: "guest_contacts");

            migrationBuilder.DropIndex(
                name: "ix_guest_contacts_tenant_id_last_used_at",
                schema: "public",
                table: "guest_contacts");

            migrationBuilder.DropIndex(
                name: "ix_guest_contacts_tenant_id_phone_fingerprint",
                schema: "public",
                table: "guest_contacts");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                schema: "public",
                table: "vendor_catalogs");

            migrationBuilder.DropColumn(
                name: "email_fingerprint",
                schema: "public",
                table: "guest_contacts");

            migrationBuilder.DropColumn(
                name: "last_used_at",
                schema: "public",
                table: "guest_contacts");

            migrationBuilder.DropColumn(
                name: "phone_fingerprint",
                schema: "public",
                table: "guest_contacts");

            migrationBuilder.CreateIndex(
                name: "ix_guest_contacts_tenant_id_created_at",
                schema: "public",
                table: "guest_contacts",
                columns: new[] { "tenant_id", "created_at" });
        }
    }
}
