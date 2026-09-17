using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class ConsoleScanKeysetIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_booking_operations_tenant_id_updated_at_operation_id",
                schema: "public",
                table: "booking_operations",
                columns: new[] { "tenant_id", "updated_at", "operation_id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_booking_operations_tenant_id_updated_at_operation_id",
                schema: "public",
                table: "booking_operations");
        }
    }
}
