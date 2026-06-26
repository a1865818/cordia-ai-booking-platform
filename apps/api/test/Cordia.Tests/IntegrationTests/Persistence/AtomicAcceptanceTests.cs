#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Verifies that a single SaveChanges that includes a trigger-failing confirmation
/// rolls back the entire unit — intent + revision row must not persist (Inv-4).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class AtomicAcceptanceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SaveChanges_WithWrongConfirmationHash_RollsBackEntireUnit()
    {
        var tenantId = $"t1-atom-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var confirmationId = $"conf-{Guid.NewGuid():N}";

        // In one DbContext, add intent + revision + a confirmation whose hash is WRONG.
        // EF wraps a single SaveChangesAsync in one transaction.
        await using (var ctx = fixture.CreateContext(tenantId))
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
                PayloadHash = "hash-correct",
                SchemaVersion = "1.0",
                TenantPolicyVersion = "1.0",
                Intent = intent,
            };
            var confirmation = new IntentConfirmation
            {
                TenantId = tenantId,
                ConfirmationId = confirmationId,
                IntentId = intentId,
                IntentRevision = 1,
                IntentPayloadHash = "WRONG-HASH", // triggers the BEFORE INSERT trigger
                IdentityRef = "anon-session",
                ConfirmationChannel = "voice",
                SourceTurnId = "turn-1",
                ReadbackVersion = "v1",
                Disclosures = "[]",
                VerificationLevel = VerificationLevel.Unverified,
                Revision = revision,
            };
            // The full §6.1 acceptance unit also mints the operation row.
            var operation = new BookingOperation
            {
                TenantId = tenantId,
                OperationId = operationId,
                IntentId = intentId,
                IntentRevision = 1,
                OperationType = OperationTypeEnum.Create,
                Status = OperationStatus.Accepted,
            };

            ctx.BookingIntents.Add(intent);
            ctx.BookingIntentRevisions.Add(revision);
            ctx.IntentConfirmations.Add(confirmation);
            ctx.BookingOperations.Add(operation);

            // Must throw due to trigger
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }

        // Verify the ENTIRE unit rolled back — intent + revision + confirmation + operation absent (Inv-4).
        await using var freshCtx = fixture.CreateContext(tenantId);

        var intentExists = await freshCtx.BookingIntents
            .IgnoreQueryFilters()
            .AnyAsync(i => i.TenantId == tenantId && i.IntentId == intentId);

        var revisionExists = await freshCtx.BookingIntentRevisions
            .IgnoreQueryFilters()
            .AnyAsync(r => r.TenantId == tenantId && r.IntentId == intentId);

        var confirmationExists = await freshCtx.IntentConfirmations
            .IgnoreQueryFilters()
            .AnyAsync(c => c.TenantId == tenantId && c.ConfirmationId == confirmationId);

        var operationExists = await freshCtx.BookingOperations
            .IgnoreQueryFilters()
            .AnyAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.False(intentExists, "BookingIntent must be rolled back when confirmation trigger fails.");
        Assert.False(revisionExists, "BookingIntentRevision must be rolled back when confirmation trigger fails.");
        Assert.False(confirmationExists, "IntentConfirmation must be rolled back when confirmation trigger fails.");
        Assert.False(operationExists, "BookingOperation must be rolled back when confirmation trigger fails.");
    }
}
