using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class WaitlistOfferMatchAndCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "available_seats",
                schema: "public",
                table: "waitlist_offers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "slot_id",
                schema: "public",
                table: "waitlist_offers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "slot_start_at",
                schema: "public",
                table: "waitlist_offers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                schema: "public",
                table: "waitlist_enrollments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "available_seats",
                schema: "public",
                table: "waitlist_offers");

            migrationBuilder.DropColumn(
                name: "slot_id",
                schema: "public",
                table: "waitlist_offers");

            migrationBuilder.DropColumn(
                name: "slot_start_at",
                schema: "public",
                table: "waitlist_offers");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                schema: "public",
                table: "waitlist_enrollments");
        }
    }
}
