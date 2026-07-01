using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class UniquePreorderSagaPerOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_preorder_sagas_tenant_id_operation_id",
                schema: "public",
                table: "preorder_sagas");

            migrationBuilder.CreateIndex(
                name: "uq_preorder_saga_per_operation",
                schema: "public",
                table: "preorder_sagas",
                columns: new[] { "tenant_id", "operation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_preorder_saga_per_operation",
                schema: "public",
                table: "preorder_sagas");

            migrationBuilder.CreateIndex(
                name: "ix_preorder_sagas_tenant_id_operation_id",
                schema: "public",
                table: "preorder_sagas",
                columns: new[] { "tenant_id", "operation_id" });
        }
    }
}
