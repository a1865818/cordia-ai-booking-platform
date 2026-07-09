using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class MenuItemExtractionSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "extraction_confidence",
                schema: "public",
                table: "vendor_catalog_items",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_location",
                schema: "public",
                table: "vendor_catalog_items",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "extraction_confidence",
                schema: "public",
                table: "vendor_catalog_items");

            migrationBuilder.DropColumn(
                name: "source_location",
                schema: "public",
                table: "vendor_catalog_items");
        }
    }
}
