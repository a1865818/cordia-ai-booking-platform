#pragma warning disable xUnit1051
using System.Diagnostics.Metrics;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using Cordia.Infrastructure.Services.Retention;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// The un-erasable guest-contact exposure must be a COUNTER, not only a log line.
///
/// <para>Contact data inside a hashed intent revision cannot be erased — <c>payload_hash</c> binds
/// the confirmation to the payload and the whole idempotency chain descends from it (Inv-2/3), so
/// the sweep counts rather than redacts. A Warning log answers "is there any?" once per sweep; the
/// counter answers "is it growing?", and that is the question the deferred spec 12/14 retention
/// ruling actually turns on — the choice between redaction, out-of-band storage and crypto-shredding
/// depends on whether this is a bounded legacy set or an accumulating one.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class UnerasableContactSignalTests(Acceptance.AcceptanceApiFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Well past the anonymization window, so the row is an erasure candidate.</summary>
    private static readonly DateTimeOffset LongAgo = Now.AddDays(-400);

    private async Task SeedTerminalOperationWithContactAsync(string tenantId, bool withContact)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var payload = withContact
            ? """{"operation":"create","service_id":"svc","customer":{"customer_id":"c1","phone_e164":"+61400111222"}}"""
            : """{"operation":"create","service_id":"svc"}""";

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.BookingIntents.Add(new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = OperationTypeEnum.Create,
            SessionId = "sess",
            CreatedAt = LongAgo,
        });
        ctx.BookingIntentRevisions.Add(new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = payload,
            PayloadHash = $"h-{Guid.NewGuid():N}",
            SchemaVersion = "intent.v1",
            TenantPolicyVersion = "policy.v1",
            SourceSessionId = "sess",
            CreatedAt = LongAgo,
        });
        ctx.BookingOperations.Add(new BookingOperation
        {
            TenantId = tenantId,
            OperationId = $"op-{Guid.NewGuid():N}",
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            // Terminal: a live operation's payload is still operationally needed and is not an
            // erasure candidate under any policy.
            Status = OperationStatus.Booked,
            Version = 0,
            CreatedAt = LongAgo,
            UpdatedAt = LongAgo,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<long> CountEmittedAsync(string tenantId)
    {
        using var metrics = new Metrics();
        using var listener = new MeterListener();
        long emitted = 0;
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == metrics.Meter
                && instrument.Name == MetricNames.RetentionUnerasableContactTotal)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => emitted += measurement);
        listener.Start();

        // The handler directly, scoped to this tenant: the cross-tenant sweeper would also count
        // every other suite's rows, so a shared-database assertion on it could never be exact.
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = new RetentionSweepHandler(ctx);
        var result = await handler.SurveyUnerasableContactAsync(tenantId, Now);

        if (result.Skipped > 0)
            metrics.RetentionUnerasableContactTotal.Add(result.Skipped);

        listener.Dispose();
        return emitted;
    }

    [Fact]
    public async Task ATerminalRevisionStillHoldingAGuestPhoneIsCounted()
    {
        var tenantId = Cordia.Tests.TestIds.New("unerasable-yes");
        await SeedTerminalOperationWithContactAsync(tenantId, withContact: true);

        Assert.Equal(1, await CountEmittedAsync(tenantId));
    }

    /// <summary>
    /// Zero must stay silent. An inflated figure is worse than none: the number exists to inform a
    /// retention ruling, and a counter that ticks for every booking would tell nobody anything.
    /// </summary>
    [Fact]
    public async Task ARevisionWithNoContactIsNotCounted()
    {
        var tenantId = Cordia.Tests.TestIds.New("unerasable-no");
        await SeedTerminalOperationWithContactAsync(tenantId, withContact: false);

        Assert.Equal(0, await CountEmittedAsync(tenantId));
    }
}
