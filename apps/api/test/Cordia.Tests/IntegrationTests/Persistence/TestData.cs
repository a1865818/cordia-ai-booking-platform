using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;

namespace Cordia.Tests.IntegrationTests.Persistence;

internal static class TestData
{
    /// <summary>Seeds intent + revision (rev 1, payload_hash "h") + operation in the given
    /// context; returns operationId.  <paramref name="status"/> defaults to
    /// <see cref="OperationStatus.Accepted"/> so existing callers are unaffected; pass a
    /// terminal status (e.g. <see cref="OperationStatus.Booked"/>) for retention-sweep tests.</summary>
    public static async Task<string> SeedOperationAsync(
        ApplicationDbContext ctx,
        string tenantId,
        OperationStatus status = OperationStatus.Accepted,
        CancellationToken ct = default)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

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
            // A guest, so notifications built on this operation have somewhere to go. An empty
            // payload has no destination, and since the send path now settles that as
            // invalid_recipient rather than pretending to deliver, "{}" would quietly turn every
            // notification test into a test of the no-destination branch.
            CanonicalPayload = """{"operation":"create","customer":{"phone_e164":"+61400000000"}}""",
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            Intent = intent,
        };
        var operation = new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = status,
            Version = 0,
        };

        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(revision);
        ctx.BookingOperations.Add(operation);
        await ctx.SaveChangesAsync(ct);

        return operationId;
    }
}
