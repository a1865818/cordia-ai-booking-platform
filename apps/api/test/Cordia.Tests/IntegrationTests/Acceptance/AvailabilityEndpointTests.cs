#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Capacity;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Availability (spec 07; docs §12 gap 10).
///
/// The read and the write ship together on purpose: `capacity_slots` previously had a repository
/// read and NO writer, so it was empty in every deployment — no booking was capacity-checked, and
/// an availability read alone would have reported "nothing available" for every venue. The last
/// test is the one that matters: a slot authored here must be findable under the SAME key
/// `ReserveCapacityCommandHandler` derives, or the table fills up and the tracked path still
/// never engages.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class AvailabilityEndpointTests(AcceptanceApiFixture fixture)
{
    private static readonly DateTimeOffset Slot =
        new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "declaring bookable capacity");
    }

    private async Task<JsonElement> CreateSlotAsync(
        string tenantId, int capacity = 4, DateTimeOffset? startsAt = null,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new
            {
                service_id = "svc-spa",
                location_id = "loc-1",
                starts_at = startsAt ?? Slot,
                total_capacity = capacity,
            }).ToUrl("/api/availability/slots");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(expected);
        });

        if (expected != HttpStatusCode.OK) return default;
        return JsonDocument.Parse(await result.ReadAsTextAsync()).RootElement.Clone();
    }

    private async Task<JsonElement> ReadAsync(string tenantId, DateTimeOffset from, DateTimeOffset to)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/availability?from={Uri.EscapeDataString(from.ToString("o"))}"
                      + $"&to={Uri.EscapeDataString(to.ToString("o"))}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });
        return JsonDocument.Parse(await result.ReadAsTextAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task ADeclaredSlotIsReadableWithItsFreeCapacity()
    {
        var tenantId = TestIds.New("avail");
        await CreateSlotAsync(tenantId, capacity: 4);

        var root = await ReadAsync(tenantId, Slot.AddHours(-1), Slot.AddHours(1));
        var slot = root.GetProperty("slots")[0];

        Assert.Equal(4, slot.GetProperty("total_capacity").GetInt32());
        // Nothing held or booked yet, so free == total.
        Assert.Equal(4, slot.GetProperty("free_capacity").GetInt32());
    }

    [Fact]
    public async Task AnEmptyWindowIsARealAnswerAndNotAClaimTheVenueIsFull()
    {
        var tenantId = TestIds.New("avail-empty");

        var root = await ReadAsync(tenantId, Slot, Slot.AddDays(1));

        // "no slot has been declared here" ≠ "the venue is full". The console must not conflate
        // them, and the API must not either.
        Assert.Equal(0, root.GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public async Task OnlySlotsInsideTheWindowAreReturned()
    {
        var tenantId = TestIds.New("avail-window");
        await CreateSlotAsync(tenantId, startsAt: Slot);
        await CreateSlotAsync(tenantId, startsAt: Slot.AddDays(5));

        var root = await ReadAsync(tenantId, Slot.AddHours(-1), Slot.AddHours(1));

        Assert.Equal(1, root.GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public async Task DeclaringTheSameSlotTwiceDoesNotDuplicateOrResetIt()
    {
        var tenantId = TestIds.New("avail-idem");
        var first = await CreateSlotAsync(tenantId, capacity: 4);
        // A second declaration with a DIFFERENT capacity must not silently reset counts that
        // live bookings depend on.
        var second = await CreateSlotAsync(tenantId, capacity: 99);

        Assert.Equal(
            first.GetProperty("slot_id").GetString(),
            second.GetProperty("slot_id").GetString());
        Assert.Equal(4, second.GetProperty("total_capacity").GetInt32());

        var root = await ReadAsync(tenantId, Slot.AddHours(-1), Slot.AddHours(1));
        Assert.Equal(1, root.GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public async Task RejectsASlotWithNoResourceDimension()
    {
        var tenantId = TestIds.New("avail-nodim");

        await fixture.Host.Scenario(x =>
        {
            // With none of resource/service/location there is nothing to key on, so the slot
            // could never be matched to a booking.
            x.Post.Json(new { starts_at = Slot, total_capacity = 4 }).ToUrl("/api/availability/slots");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task RejectsAnInvertedWindow()
    {
        var tenantId = TestIds.New("avail-badwindow");

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/availability?from={Uri.EscapeDataString(Slot.ToString("o"))}"
                      + $"&to={Uri.EscapeDataString(Slot.AddDays(-1).ToString("o"))}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task NonOwnerCannotDeclareCapacity()
    {
        var tenantId = TestIds.New("avail-role");

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new
            {
                service_id = "svc-spa",
                starts_at = Slot,
                total_capacity = 4,
            }).ToUrl("/api/availability/slots");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task DoesNotReturnAnotherTenantsCapacity()
    {
        var tenantA = TestIds.New("avail-xta");
        var tenantB = TestIds.New("avail-xtb");
        await CreateSlotAsync(tenantA);

        var root = await ReadAsync(tenantB, Slot.AddHours(-1), Slot.AddHours(1));

        Assert.Equal(0, root.GetProperty("slots").GetArrayLength());
    }

    /// <summary>
    /// The whole point of shipping the write with the read: an authored slot must live under the
    /// SAME key the fulfilment engine derives from a booking's canonical payload. If it did not,
    /// the table would fill up and `ReserveCapacityCommandHandler`'s tracked path would still
    /// never engage — capacity would remain unenforced while looking enforced.
    /// </summary>
    [Fact]
    public async Task AnAuthoredSlotUsesTheKeyTheFulfilmentEngineLooksUp()
    {
        var tenantId = TestIds.New("avail-key");
        var created = await CreateSlotAsync(tenantId);

        var expected = CapacitySlotKey.Derive(null, "svc-spa", "loc-1", Slot);

        Assert.Equal(expected, created.GetProperty("slot_id").GetString());
    }
}
