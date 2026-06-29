using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class OpenWorkItemUniquePerOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_operations_work_items_tenant_id_operation_id",
                schema: "public",
                table: "operations_work_items");

            // Resolve any pre-existing duplicate open work items before creating the unique index.
            // Keeps exactly ONE open row (the max(ctid)) per (tenant_id, operation_id); demotes the
            // other duplicates to state='resolved' so the CREATE UNIQUE INDEX succeeds.
            migrationBuilder.Sql(@"
UPDATE operations_work_items o
SET    state      = 'resolved',
       resolution = COALESCE(resolution, '""auto-deduped""')
WHERE  state = 'open'
  AND  ctid <> (
    SELECT max(i.ctid)
    FROM   operations_work_items i
    WHERE  i.tenant_id    = o.tenant_id
      AND  i.operation_id = o.operation_id
      AND  i.state        = 'open'
  );");

            migrationBuilder.CreateIndex(
                name: "uq_open_work_item_per_operation",
                schema: "public",
                table: "operations_work_items",
                columns: new[] { "tenant_id", "operation_id" },
                unique: true,
                filter: "state = 'open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_open_work_item_per_operation",
                schema: "public",
                table: "operations_work_items");

            migrationBuilder.CreateIndex(
                name: "ix_operations_work_items_tenant_id_operation_id",
                schema: "public",
                table: "operations_work_items",
                columns: new[] { "tenant_id", "operation_id" });
        }
    }
}
