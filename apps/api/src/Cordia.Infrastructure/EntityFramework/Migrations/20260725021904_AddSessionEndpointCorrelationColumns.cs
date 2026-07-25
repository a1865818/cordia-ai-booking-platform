using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionEndpointCorrelationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "channel_endpoint_id",
                schema: "public",
                table: "conversation_sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "public",
                table: "conversation_sessions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "channel_endpoint_id",
                schema: "public",
                table: "conversation_sessions");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "public",
                table: "conversation_sessions");
        }
    }
}
