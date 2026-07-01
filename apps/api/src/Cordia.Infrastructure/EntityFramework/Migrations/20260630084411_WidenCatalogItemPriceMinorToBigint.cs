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
            // Fail fast with a clear message if any value would overflow int32 on narrowing — otherwise
            // the ALTER COLUMN would abort with an opaque "integer out of range" mid-rollback.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM public.vendor_catalog_items
        WHERE price_minor > 2147483647 OR price_minor < -2147483648
    ) THEN
        RAISE EXCEPTION 'Cannot narrow vendor_catalog_items.price_minor to integer: one or more values exceed the int32 range. Reduce/remove those rows before rolling back this migration.';
    END IF;
END $$;");

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
