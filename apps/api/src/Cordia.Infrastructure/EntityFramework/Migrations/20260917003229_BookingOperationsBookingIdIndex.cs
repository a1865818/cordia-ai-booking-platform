using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class BookingOperationsBookingIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_booking_operations_tenant_id_booking_id",
                schema: "public",
                table: "booking_operations",
                columns: new[] { "tenant_id", "booking_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_booking_operations_tenant_id_booking_id",
                schema: "public",
                table: "booking_operations");
        }
    }
}
