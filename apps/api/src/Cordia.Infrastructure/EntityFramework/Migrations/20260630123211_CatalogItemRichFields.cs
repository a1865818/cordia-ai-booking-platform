using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class CatalogItemRichFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "availability_window",
                schema: "public",
                table: "vendor_catalog_items",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_ref",
                schema: "public",
                table: "vendor_catalog_items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "modifiers",
                schema: "public",
                table: "vendor_catalog_items",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "availability_window",
                schema: "public",
                table: "vendor_catalog_items");

            migrationBuilder.DropColumn(
                name: "image_ref",
                schema: "public",
                table: "vendor_catalog_items");

            migrationBuilder.DropColumn(
                name: "modifiers",
                schema: "public",
                table: "vendor_catalog_items");
        }
    }
}
