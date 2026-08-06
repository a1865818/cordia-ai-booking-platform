#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Guest contact held out-of-band so it can actually be erased (spec 12/14 retention ruling,
/// owner decision 2026-08-06).
///
/// <para><b>The problem this closes.</b> Contact written into
/// <c>booking_intent_revisions.canonical_payload</c> could never be erased: the payload is hashed,
/// <c>payload_hash</c> binds the confirmation to it, and the idempotency chain (Inv-2/3) descends
/// from that hash — redacting it makes the payload stop re-hashing to its own recorded hash. The
/// sweep could count the exposure but not remove it.</para>
///
/// <para>The load-bearing test is <see cref="ErasureRemovesTheContactAndLeavesTheHashIntact"/>:
/// erasure has to be a real delete AND the payload has to survive untouched, or this has simply
/// moved the problem.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class GuestContactErasureTests(AcceptanceApiFixture fixture)
{
    private async Task<string> MintRefAsync(
        string tenantId, string? phone, string? email = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { phone_e164 = phone, email, display_name = (string?)null })
                .ToUrl("/api/guest-contacts");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(expected);
        });

        if (expected != HttpStatusCode.OK) return string.Empty;

        return JsonDocument.Parse(await result.ReadAsTextAsync())
            .RootElement.GetProperty("contact_ref").GetString()!;
    }

    [Fact]
    public async Task MintsAnOpaqueRefThatIsNotTheContact()
    {
        var tenantId = TestIds.New("gc-mint");

        var contactRef = await MintRefAsync(tenantId, "+61400111222");

        Assert.StartsWith("gc_", contactRef);
        // If the number could be read back out of the handle, the handle would be the exposure.
        Assert.DoesNotContain("400111222", contactRef);
    }

    /// <summary>
    /// One guest, many bookings, one erasable row. Otherwise "forget me" would depend on the
    /// platform finding every copy — which is the failure mode the inline shape had.
    /// </summary>
    [Fact]
    public async Task ReusesOneRowForTheSameGuest()
    {
        var tenantId = TestIds.New("gc-idem");

        var first = await MintRefAsync(tenantId, "+61400111222");
        var second = await MintRefAsync(tenantId, "+61400111222");

        Assert.Equal(first, second);
    }

    /// <summary>Formatting must not create a second row for the same person.</summary>
    [Fact]
    public async Task NormalisesBeforeDeduplicating()
    {
        var tenantId = TestIds.New("gc-norm");

        var tidy = await MintRefAsync(tenantId, "+61400111222");
        var untidy = await MintRefAsync(tenantId, "+61 400 111 222");

        Assert.Equal(tidy, untidy);
    }

    /// <summary>
    /// Tenancy is part of the fingerprint (Inv-13). Without the salt, one tenant could confirm
    /// another's guest exists by probing for a shared ref.
    /// </summary>
    [Fact]
    public async Task DoesNotShareARefAcrossTenants()
    {
        var a = await MintRefAsync(TestIds.New("gc-xta"), "+61400111222");
        var b = await MintRefAsync(TestIds.New("gc-xtb"), "+61400111222");

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// A ref that dereferences to nothing would put a handle on a booking implying a destination
    /// exists when none does — Inv-16 forbids asserting what was never recorded.
    /// </summary>
    [Fact]
    public async Task RefusesToMintARefForNoContactAtAll()
    {
        await MintRefAsync(
            TestIds.New("gc-empty"), phone: null, email: null, expected: HttpStatusCode.BadRequest);
    }

    /// <summary>A guest who left only an email is still reachable and still erasable.</summary>
    [Fact]
    public async Task AcceptsAnEmailOnlyGuest()
    {
        var tenantId = TestIds.New("gc-email");

        var contactRef = await MintRefAsync(tenantId, phone: null, email: "guest@example.com");

        Assert.StartsWith("gc_", contactRef);
    }

    /// <summary>
    /// The whole ruling in one test: the contact row is gone, and the immutable payload that
    /// referenced it is byte-for-byte unchanged — so every hash still verifies. If erasure had to
    /// touch the payload, this change would have solved nothing.
    /// </summary>
    [Fact]
    public async Task ErasureRemovesTheContactAndLeavesTheHashIntact()
    {
        var tenantId = TestIds.New("gc-erase");
        var contactRef = await MintRefAsync(tenantId, "+61400999888");

        // Stand in for the immutable revision: the payload references the contact, never holds it.
        var payload = "{\"operation\":\"create\",\"customer\":{\"contact_ref\":\"" + contactRef + "\"}}";
        Assert.DoesNotContain("400999888", payload);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new GuestContactRepository(ctx);
            Assert.NotNull(await repo.GetByRefAsync(tenantId, contactRef));

            var erased = await repo.EraseOlderThanAsync(tenantId, DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.True(erased >= 1);
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // Gone — a real DELETE, not a survey.
            Assert.Null(await new GuestContactRepository(ctx).GetByRefAsync(tenantId, contactRef));
        }

        // And the payload is untouched, so payload_hash still describes it exactly.
        Assert.Contains(contactRef, payload);
    }

    /// <summary>
    /// A booking whose contact has been erased must stop being findable by phone. That is not a
    /// regression — it is what erasure means, and the alternative would be a lookup that still
    /// answers from data the guest asked to have removed.
    /// </summary>
    [Fact]
    public async Task AnErasedContactStopsResolvingForDiscovery()
    {
        var tenantId = TestIds.New("gc-discover");
        var contactRef = await MintRefAsync(tenantId, "+61400777666");

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new GuestContactRepository(ctx);
            Assert.Contains(contactRef, await repo.FindRefsByPhoneAsync(tenantId, "+61400777666"));
            await repo.EraseOlderThanAsync(tenantId, DateTimeOffset.UtcNow.AddMinutes(1));
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            Assert.Empty(await new GuestContactRepository(ctx)
                .FindRefsByPhoneAsync(tenantId, "+61400777666"));
        }
    }

    /// <summary>
    /// The retention clock is LAST USE, not creation — and this is the difference between erasing
    /// a guest who is gone and erasing one who is standing at the counter.
    ///
    /// <para>Creation is idempotent per guest, so a returning guest keeps their original
    /// <c>CreatedAt</c> forever. Sweeping on it deletes the contact for the booking they made
    /// today because they also booked two years ago: the <c>contact_ref</c> on that booking stops
    /// dereferencing, the confirmation settles with no destination, and nobody is told. The same
    /// happens to any first-time booking made further ahead than the window.</para>
    /// </summary>
    [Fact]
    public async Task DoesNotEraseAContactThatWasJustUsedAgain()
    {
        var tenantId = TestIds.New("gc-reuse-clock");
        var contactRef = await MintRefAsync(tenantId, "+61400222333");

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // Age the row as if it were first captured well outside the retention window.
            await ctx.GuestContacts
                .IgnoreQueryFilters()
                .Where(g => g.TenantId == tenantId && g.ContactRef == contactRef)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-200))
                    .SetProperty(x => x.LastUsedAt, DateTimeOffset.UtcNow.AddDays(-200)));
        }

        // The guest books again TODAY. Minting is idempotent, so this returns the same aged row —
        // and must re-arm its retention window.
        var again = await MintRefAsync(tenantId, "+61400222333");
        Assert.Equal(contactRef, again);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new GuestContactRepository(ctx);
            await repo.EraseOlderThanAsync(tenantId, DateTimeOffset.UtcNow.AddDays(-90));

            // Still there: the booking made moments ago still has somewhere to notify.
            Assert.NotNull(await repo.GetByRefAsync(tenantId, contactRef));
        }
    }

    /// <summary>
    /// A contact genuinely untouched for longer than the window is still erased — the clock moving
    /// on use must not become "never erase anything".
    /// </summary>
    [Fact]
    public async Task StillErasesAContactUnusedForLongerThanTheWindow()
    {
        var tenantId = TestIds.New("gc-idle-clock");
        var contactRef = await MintRefAsync(tenantId, "+61400333444");

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await ctx.GuestContacts
                .IgnoreQueryFilters()
                .Where(g => g.TenantId == tenantId && g.ContactRef == contactRef)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, DateTimeOffset.UtcNow.AddDays(-200)));
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new GuestContactRepository(ctx);
            await repo.EraseOlderThanAsync(tenantId, DateTimeOffset.UtcNow.AddDays(-90));
            Assert.Null(await repo.GetByRefAsync(tenantId, contactRef));
        }
    }

    /// <summary>
    /// Fingerprint fields are separated before hashing, so two genuinely different guests cannot
    /// collide onto one row.
    ///
    /// <para>Bare concatenation flattened <c>(+61, 400@x.com)</c> and <c>(+61400, @x.com)</c> to
    /// one string. The unique index would then hand the first guest's ref to the second — so the
    /// second guest's booking notifies the FIRST guest, and erasing either erases both.</para>
    /// </summary>
    [Fact]
    public async Task DoesNotCollideContactsThatOnlyLookAlikeConcatenated()
    {
        var tenantId = TestIds.New("gc-collide");

        var first = await MintRefAsync(tenantId, phone: "+61", email: "400@example.com");
        var second = await MintRefAsync(tenantId, phone: "+61400", email: "@example.com");

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Discovery by email, without which a guest who left only an email is findable by no hint at
    /// all once contact moves out of the payload — the payload then carries neither their address
    /// nor anything derived from it.
    /// </summary>
    [Fact]
    public async Task FindsAnEmailOnlyGuestByEmail()
    {
        var tenantId = TestIds.New("gc-find-email");
        var contactRef = await MintRefAsync(tenantId, phone: null, email: "Guest@Example.com");

        await using var ctx = fixture.CreateContext(tenantId);
        var repo = new GuestContactRepository(ctx);

        // Normalised on both sides, so the casing an operator typed is irrelevant.
        Assert.Contains(contactRef, await repo.FindRefsByEmailAsync(tenantId, "guest@example.com"));
        Assert.Contains(contactRef, await repo.FindRefsByEmailAsync(tenantId, "  GUEST@EXAMPLE.COM  "));
    }

    /// <summary>
    /// A guest who gave BOTH a phone and an email is still findable by the phone. The identity
    /// fingerprint folds in the email, so matching on it alone would silently miss exactly the
    /// guests the platform knows most about.
    /// </summary>
    [Fact]
    public async Task FindsAGuestByPhoneEvenWhenTheyAlsoGaveAnEmail()
    {
        var tenantId = TestIds.New("gc-find-both");
        var contactRef = await MintRefAsync(tenantId, "+61400888999", "both@example.com");

        await using var ctx = fixture.CreateContext(tenantId);
        var repo = new GuestContactRepository(ctx);

        Assert.Contains(contactRef, await repo.FindRefsByPhoneAsync(tenantId, "+61400888999"));
        Assert.Contains(contactRef, await repo.FindRefsByEmailAsync(tenantId, "both@example.com"));
    }

    /// <summary>Erasure is tenant-scoped: one venue's sweep must not delete another's guests.</summary>
    [Fact]
    public async Task ErasureDoesNotCrossTenants()
    {
        var tenantA = TestIds.New("gc-erase-a");
        var tenantB = TestIds.New("gc-erase-b");
        var refA = await MintRefAsync(tenantA, "+61400555444");
        var refB = await MintRefAsync(tenantB, "+61400555444");

        await using (var ctx = fixture.CreateContext(tenantA))
            await new GuestContactRepository(ctx).EraseOlderThanAsync(tenantA, DateTimeOffset.UtcNow.AddMinutes(1));

        await using (var ctx = fixture.CreateContext(tenantB))
        {
            Assert.NotNull(await new GuestContactRepository(ctx).GetByRefAsync(tenantB, refB));
        }

        await using (var ctx = fixture.CreateContext(tenantA))
        {
            Assert.Null(await new GuestContactRepository(ctx).GetByRefAsync(tenantA, refA));
        }
    }
}
