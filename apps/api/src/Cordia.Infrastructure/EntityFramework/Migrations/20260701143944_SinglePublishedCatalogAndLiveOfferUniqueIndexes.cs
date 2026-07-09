using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class SinglePublishedCatalogAndLiveOfferUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_waitlist_offers_tenant_id_enrollment_id",
                schema: "public",
                table: "waitlist_offers");

            migrationBuilder.CreateIndex(
                name: "uq_live_offer_per_enrollment",
                schema: "public",
                table: "waitlist_offers",
                columns: new[] { "tenant_id", "enrollment_id" },
                unique: true,
                filter: "status IN ('pending', 'matched', 'offered')");

            migrationBuilder.CreateIndex(
                name: "uq_published_catalog_per_tenant",
                schema: "public",
                table: "vendor_catalogs",
                column: "tenant_id",
                unique: true,
                filter: "status = 'published'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_live_offer_per_enrollment",
                schema: "public",
                table: "waitlist_offers");

            migrationBuilder.DropIndex(
                name: "uq_published_catalog_per_tenant",
                schema: "public",
                table: "vendor_catalogs");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_offers_tenant_id_enrollment_id",
                schema: "public",
                table: "waitlist_offers",
                columns: new[] { "tenant_id", "enrollment_id" });
        }
    }
}
