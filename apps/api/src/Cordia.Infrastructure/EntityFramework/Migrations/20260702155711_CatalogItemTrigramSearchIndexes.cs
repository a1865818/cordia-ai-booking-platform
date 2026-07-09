using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <summary>
    /// Spec 11 §7: <c>search_vendor_catalog</c> targets p95 &lt; 120 ms via an INDEXED search, but the
    /// repository's <c>ILIKE '%…%'</c> over name/description had no supporting index — a per-tenant
    /// heap scan that degrades linearly with catalog size. pg_trgm GIN indexes make the leading-
    /// wildcard ILIKE indexable. Hand-written SQL: EF's model annotations cannot express trigram
    /// operator classes, and CREATE EXTENSION is idempotent (requires a role permitted to create
    /// extensions — the default dev/CI container user is).
    /// </summary>
    public partial class CatalogItemTrigramSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS ix_vendor_catalog_items_name_trgm
                    ON public.vendor_catalog_items USING gin (name gin_trgm_ops);
                """);
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS ix_vendor_catalog_items_description_trgm
                    ON public.vendor_catalog_items USING gin (description gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS public.ix_vendor_catalog_items_description_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS public.ix_vendor_catalog_items_name_trgm;");
            // The pg_trgm extension is left installed — other objects may depend on it.
        }
    }
}
