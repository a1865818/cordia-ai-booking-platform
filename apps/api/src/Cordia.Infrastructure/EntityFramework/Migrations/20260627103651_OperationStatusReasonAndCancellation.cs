using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class OperationStatusReasonAndCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancellation_requested_at",
                schema: "public",
                table: "booking_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_reason",
                schema: "public",
                table: "booking_operations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancellation_requested_at",
                schema: "public",
                table: "booking_operations");

            migrationBuilder.DropColumn(
                name: "status_reason",
                schema: "public",
                table: "booking_operations");
        }
    }
}
