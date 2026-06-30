using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class WidenCatalogItemPriceMinorToBigint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "price_minor",
                schema: "public",
                table: "vendor_catalog_items",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "price_minor",
                schema: "public",
                table: "vendor_catalog_items",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");
        }
    }
}
