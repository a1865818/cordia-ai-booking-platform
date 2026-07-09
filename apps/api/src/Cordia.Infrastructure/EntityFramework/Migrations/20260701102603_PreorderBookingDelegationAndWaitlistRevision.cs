using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class PreorderBookingDelegationAndWaitlistRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "identity_ref",
                schema: "public",
                table: "waitlist_enrollments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "verification_level",
                schema: "public",
                table: "waitlist_enrollments",
                type: "text",
                nullable: false,
                defaultValue: "unverified");

            migrationBuilder.AddColumn<bool>(
                name: "is_preorder",
                schema: "public",
                table: "booking_operations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "identity_ref",
                schema: "public",
                table: "waitlist_enrollments");

            migrationBuilder.DropColumn(
                name: "verification_level",
                schema: "public",
                table: "waitlist_enrollments");

            migrationBuilder.DropColumn(
                name: "is_preorder",
                schema: "public",
                table: "booking_operations");
        }
    }
}
