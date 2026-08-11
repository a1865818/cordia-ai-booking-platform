using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class TenantDisplayNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_display_names",
                schema: "public",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_display_names", x => x.tenant_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_display_names",
                schema: "public");
        }
    }
}
