using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class PreorderSagaColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                schema: "public",
                table: "preorder_sagas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_standalone",
                schema: "public",
                table: "preorder_sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "requested_capacity",
                schema: "public",
                table: "preorder_sagas",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "slot_id",
                schema: "public",
                table: "preorder_sagas",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failure_reason",
                schema: "public",
                table: "preorder_sagas");

            migrationBuilder.DropColumn(
                name: "is_standalone",
                schema: "public",
                table: "preorder_sagas");

            migrationBuilder.DropColumn(
                name: "requested_capacity",
                schema: "public",
                table: "preorder_sagas");

            migrationBuilder.DropColumn(
                name: "slot_id",
                schema: "public",
                table: "preorder_sagas");
        }
    }
}
