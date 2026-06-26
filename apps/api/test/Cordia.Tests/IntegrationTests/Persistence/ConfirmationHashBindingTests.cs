#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Verifies the BEFORE INSERT trigger on intent_confirmations enforces
/// payload-hash binding (§5.2, §22.1, Inv-1/10).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ConfirmationHashBindingTests(PostgresFixture fixture)
{
    private static async Task<(BookingIntent intent, BookingIntentRevision revision)> SeedIntentAndRevisionAsync(
        Cordia.Infrastructure.EntityFramework.ApplicationDbContext ctx,
        string tenantId,
        string intentId,
        string payloadHash)
    {
        var intent = new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = OperationTypeEnum.Create,
        };
        var revision = new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = "{}",
            PayloadHash = payloadHash,
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            Intent = intent,
        };

        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(revision);
        await ctx.SaveChangesAsync();
        return (intent, revision);
    }

    // ── (a) Correct hash → succeeds ──────────────────────────────────────────
    [Fact]
    public async Task Insert_MatchingHash_Succeeds()
    {
        var tenantId = $"t1-hash-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";

        await using (var seedCtx = fixture.CreateContext(tenantId))
            await SeedIntentAndRevisionAsync(seedCtx, tenantId, intentId, "hash-A");

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.IntentConfirmations.Add(new IntentConfirmation
        {
            TenantId = tenantId,
            ConfirmationId = $"conf-{Guid.NewGuid():N}",
            IntentId = intentId,
            IntentRevision = 1,
            IntentPayloadHash = "hash-A",
            IdentityRef = "anon-session",
            ConfirmationChannel = "voice",
            SourceTurnId = "turn-1",
            ReadbackVersion = "v1",
            Disclosures = "[]",
            VerificationLevel = VerificationLevel.Unverified,
        });

        // Should not throw
        await ctx.SaveChangesAsync();
    }

    // ── (b) Wrong hash → trigger raises P0001 with "payload hash mismatch" ───
    [Fact]
    public async Task Insert_MismatchedHash_TriggerRaises()
    {
        var tenantId = $"t1-hash-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";

        await using (var seedCtx = fixture.CreateContext(tenantId))
            await SeedIntentAndRevisionAsync(seedCtx, tenantId, intentId, "hash-A");

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.IntentConfirmations.Add(new IntentConfirmation
        {
            TenantId = tenantId,
            ConfirmationId = $"conf-{Guid.NewGuid():N}",
            IntentId = intentId,
            IntentRevision = 1,
            IntentPayloadHash = "WRONG",
            IdentityRef = "anon-session",
            ConfirmationChannel = "voice",
            SourceTurnId = "turn-1",
            ReadbackVersion = "v1",
            Disclosures = "[]",
            VerificationLevel = VerificationLevel.Unverified,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal("P0001", pgEx.SqlState); // raise_exception
        Assert.Contains("payload hash mismatch", pgEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── (c) Non-existent revision → trigger raises "not found" or FK violation ─
    [Fact]
    public async Task Insert_NonExistentRevision_Raises()
    {
        var tenantId = $"t1-hash-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";

        await using (var seedCtx = fixture.CreateContext(tenantId))
            await SeedIntentAndRevisionAsync(seedCtx, tenantId, intentId, "hash-A");

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.IntentConfirmations.Add(new IntentConfirmation
        {
            TenantId = tenantId,
            ConfirmationId = $"conf-{Guid.NewGuid():N}",
            IntentId = intentId,
            IntentRevision = 999, // non-existent revision
            IntentPayloadHash = "hash-A",
            IdentityRef = "anon-session",
            ConfirmationChannel = "voice",
            SourceTurnId = "turn-1",
            ReadbackVersion = "v1",
            Disclosures = "[]",
            VerificationLevel = VerificationLevel.Unverified,
        });

        // Trigger raises "not found" (P0001) OR FK constraint raises — both are acceptable
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.True(
            pgEx.SqlState == "P0001" || pgEx.SqlState == PostgresErrorCodes.ForeignKeyViolation,
            $"Expected P0001 or {PostgresErrorCodes.ForeignKeyViolation} but got {pgEx.SqlState}");
    }
}
