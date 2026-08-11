using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cordia.Infrastructure.EntityFramework.Migrations
{
    /// <summary>
    /// Repairs <c>guest_contacts</c> rows whose phone/email lookup fingerprints are NULL.
    ///
    /// <para><b>How they can be null.</b> <c>20260806010659_GuestContactStore</c> created the table
    /// with no fingerprint columns; <c>20260806042423_GuestContactRetentionClockAndCatalogStatusClock</c>
    /// added them nullable, with their indexes, and no backfill. Any row written against the
    /// earlier schema therefore survives with NULLs — and a NULL fingerprint can never match
    /// (NULL ≠ NULL), so <c>FindRefsByPhoneAsync</c>/<c>FindRefsByEmailAsync</c> silently return
    /// nothing. For a booking using the current <c>contact_ref</c> payload shape those lookups are
    /// the ONLY discovery route, so the guest becomes permanently unfindable by phone or email.</para>
    ///
    /// <para><b>Scope, stated honestly.</b> Both of those migrations shipped in the same commit on
    /// an unmerged branch, and the application at that commit always writes fingerprints (its
    /// INSERT names the columns, so it cannot even run against the earlier schema). No deployed or
    /// CI database can hold an affected row; only a developer database stopped between the two
    /// migrations can. This is cheap, idempotent insurance and makes the schema self-consistent —
    /// not a response to known data loss.</para>
    ///
    /// <para><b>Why SQL and not an application job.</b> The fingerprint is UNKEYED SHA-256 over
    /// <c>tenant_id || U+001F || value</c>, hex-lowercase (<c>GuestContactRepository.Hash</c>) —
    /// the "salt" is the tenant id itself, folded into the plaintext, not a secret from config. It
    /// is therefore reproducible in Postgres, and the value hashed is the ALREADY-NORMALISED column
    /// content, so no re-implementation of <c>PhoneMatching.Normalise</c> /
    /// <c>ContactAddress.NormaliseEmail</c> is needed here. <c>sha256()</c> is built in from
    /// PostgreSQL 11 (this deployment runs 16), so no <c>pgcrypto</c> and no extension-creation
    /// privilege is required. An integration test asserts the SQL and the C# agree rather than
    /// trusting the equivalence by eye.</para>
    ///
    /// <para>Scoped per row by <c>tenant_id</c>, so a contact can never become discoverable under
    /// another tenant (Inv-13).</para>
    /// </summary>
    public partial class BackfillGuestContactFingerprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only rows that are actually missing a fingerprint AND have something to hash, so this
            // is idempotent and re-running it is a no-op. The whitespace guard mirrors
            // `ChannelFingerprint`, which returns null rather than hashing a blank value — without
            // it this could write a fingerprint where the application would have written none.
            migrationBuilder.Sql(@"
UPDATE public.guest_contacts
SET    phone_fingerprint = encode(
           sha256(convert_to(tenant_id || chr(31) || phone_e164, 'UTF8')), 'hex')
WHERE  phone_fingerprint IS NULL
  AND  phone_e164 IS NOT NULL
  AND  btrim(phone_e164) <> '';");

            migrationBuilder.Sql(@"
UPDATE public.guest_contacts
SET    email_fingerprint = encode(
           sha256(convert_to(tenant_id || chr(31) || email, 'UTF8')), 'hex')
WHERE  email_fingerprint IS NULL
  AND  email IS NOT NULL
  AND  btrim(email) <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately a no-op. This migration repairs data; it adds no schema to remove.
            //
            // Nulling the fingerprints back out would be worse than doing nothing: it cannot tell
            // the rows it filled from the ones the application wrote correctly, so a rollback would
            // silently destroy working lookup data for every guest contact in the table and
            // re-create the exact discovery failure this exists to fix. Rolling past this migration
            // leaves the repaired rows repaired, which is harmless and correct.
        }
    }
}
