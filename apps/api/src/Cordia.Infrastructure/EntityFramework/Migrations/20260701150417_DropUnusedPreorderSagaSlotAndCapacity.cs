using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class DropUnusedPreorderSagaSlotAndCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "requested_capacity",
                schema: "public",
                table: "preorder_sagas");

            migrationBuilder.DropColumn(
                name: "slot_id",
                schema: "public",
                table: "preorder_sagas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
    }
}
