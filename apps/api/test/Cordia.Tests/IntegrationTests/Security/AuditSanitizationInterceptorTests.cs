#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Spec 14 / Inv-12 — the DbContext-level sanitization choke point: an audit
/// row written through ANY path (here the rawest one, db.AuditEvents.Add)
/// has PAN-shaped metadata, actor, subject_ref, and correlation_id masked at
/// save time, while system-minted digit-run identifiers survive byte-for-byte
/// (control-plane precision).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class AuditSanitizationInterceptorTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DirectAdd_WithPanInMetadata_IsMaskedAtSaveTime()
    {
        var tenantId = $"t14-auditsan-{Guid.NewGuid():N}";
        var eventId = $"evt_{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId,
                EventType = "test.sanitize",
                Actor = "system",
                SubjectRef = "subject-1",
                // A PAN leaked into free-text metadata by a hypothetical writer,
                // alongside a legit reference (no embedded Luhn window) that must survive.
                Metadata = """{"note":"customer said 4111 1111 1111 1111","reference":"5260181590830166"}""",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.EventId == eventId);

        Assert.DoesNotContain("4111 1111 1111 1111", row.Metadata);
        Assert.Contains("[REDACTED]", row.Metadata);
        Assert.Contains("5260181590830166", row.Metadata); // reference with no Luhn window intact
        Assert.Equal("system", row.Actor); // benign actor passes through byte-identical
        Assert.Equal("subject-1", row.SubjectRef); // benign system-minted subject too
    }

    [Fact]
    public async Task DirectAdd_WithPanShapedSubjectRef_IsMaskedAtSaveTime()
    {
        // subject_ref can carry CLIENT-supplied identity on at least one direct write
        // path (grant_issued stores challenge.CustomerId straight from the session-scoped
        // POST /api/verifications body), and correlation_id often carries the same
        // client-influenced session id — the choke point must mask BOTH like
        // actor/metadata (Inv-12), or the value it just deemed a PAN persists one
        // column over.
        var tenantId = $"t14-auditsan-{Guid.NewGuid():N}";
        var eventId = $"evt_{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId,
                EventType = "test.sanitize",
                Actor = "system",
                SubjectRef = "4111111111111111",
                CorrelationId = "4111111111111111",
                Metadata = "{}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.EventId == eventId);

        Assert.Equal("[REDACTED]", row.SubjectRef);
        Assert.Equal("[REDACTED]", row.CorrelationId);
    }

    [Fact]
    public async Task DirectAdd_WithPanShapedActor_IsMaskedAtSaveTime()
    {
        // The actor column is free text that several write sites fill with
        // client-supplied identity (confirmation.Identity, customerRef) — the
        // choke point must mask a PAN-shaped actor on EVERY path, including the
        // rawest db.AuditEvents.Add that bypasses AuditEnvelope (Inv-12).
        var tenantId = $"t14-auditsan-{Guid.NewGuid():N}";
        var eventId = $"evt_{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId,
                EventType = "test.sanitize",
                Actor = "4111111111111111",
                SubjectRef = "subject-1",
                Metadata = "{}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.EventId == eventId);

        Assert.Equal("[REDACTED]", row.Actor);
    }

    /// <summary>
    /// PostgreSQL cannot store <c>U+0000</c>: the <c>jsonb</c> metadata column rejects the JSON escape
    /// (<c>22P05 unsupported Unicode escape sequence</c>) and the <c>text</c> identity columns reject
    /// the raw byte. Every audit write site used to serialize code-authored literals only; the
    /// menu-extraction lane is the first to copy model-generated free text — shaped by an untrusted
    /// uploaded document — into metadata, and <c>JsonSerializer</c> encodes an embedded NUL into a
    /// perfectly valid escape. The resulting throw happens at <c>SaveChanges</c>, AFTER any handler's
    /// own error handling, so it rolls back whatever transaction is open — for an inbox consumer that
    /// un-admits the message and it redelivers forever (Inv-5). The choke point drops control
    /// characters so the INSERT executes; without the strip this test fails with 22P05.
    /// </summary>
    [Fact]
    public async Task DirectAdd_WithNulInMetadataAndIdentity_IsStrippedAndStillPersists()
    {
        const char nul = '\0';
        var tenantId = $"t14-auditsan-{Guid.NewGuid():N}";
        var eventId = $"evt_{Guid.NewGuid():N}";
        var metadata = JsonSerializer.Serialize(new { reason = $"suspicious{nul}content", note = "kept" });
        Assert.Contains(@"\u0000", metadata); // the serializer really does emit the escape Postgres rejects

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId,
                EventType = "test.sanitize",
                Actor = $"sys{nul}tem",
                SubjectRef = "subject-1",
                Metadata = metadata,
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.EventId == eventId);

        Assert.DoesNotContain(@"\u0000", row.Metadata);
        var parsed = JsonDocument.Parse(row.Metadata!).RootElement;
        Assert.Equal("suspiciouscontent", parsed.GetProperty("reason").GetString());
        Assert.Equal("kept", parsed.GetProperty("note").GetString()); // the rest of the row survives untouched
        Assert.Equal("system", row.Actor);
    }

    /// <summary>
    /// <b>The control-character strip must run BEFORE the mask, on every free-text column.</b>
    ///
    /// <para><see cref="Cordia.Domain.Security.SensitiveDataMask"/>'s PAN detector tolerates only
    /// space and dash between digits, so a PAN separated by tabs, newlines or NULs never forms a
    /// candidate run and <c>Sanitize</c> hands it back verbatim. Masking first and stripping second
    /// therefore ASSEMBLED the card number out of fragments the detector had just declined to look
    /// at, and persisted it — this test read <c>4111111111111111</c> straight back off disk.
    /// Stripping first means the detector sees the digits contiguously.</para>
    ///
    /// <para>All three columns, one separator each: they share one helper, so a per-column
    /// regression would otherwise hide behind the other two.</para>
    /// </summary>
    [Fact]
    public async Task DirectAdd_WithControlCharacterSeparatedPanInIdentityColumns_IsMasked()
    {
        var tenantId = $"t14-auditsan-{Guid.NewGuid():N}";
        var eventId = $"evt_{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId,
                EventType = "test.sanitize",
                Actor = "4111\t1111\t1111\t1111",
                SubjectRef = "4111\n1111\n1111\n1111",
                CorrelationId = $"4111{Nul}1111{Nul}1111{Nul}1111",
                Metadata = "{}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.EventId == eventId);

        Assert.Equal("[REDACTED]", row.Actor);
        Assert.Equal("[REDACTED]", row.SubjectRef);
        Assert.Equal("[REDACTED]", row.CorrelationId);

        // The exact regression: the strip must never REASSEMBLE a PAN the mask then never sees.
        Assert.DoesNotContain(Pan, row.Actor, StringComparison.Ordinal);
        Assert.DoesNotContain(Pan, row.SubjectRef, StringComparison.Ordinal);
        Assert.DoesNotContain(Pan, row.CorrelationId!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same class of miss in <c>metadata</c>, which the free-text columns' fix does not cover.
    ///
    /// <para><c>StripControlCharacters</c>' fast path used to probe for the long <c>u00XX</c> escape
    /// and raw control characters only — but <c>System.Text.Json</c> emits five control characters in
    /// SHORT form (measured: a tab serializes as a two-character escape, a NUL as the six-character
    /// long one). A tab- or newline-separated PAN therefore returned from the fast path unparsed,
    /// kept its separators, and sailed past <c>SanitizeJson</c> unmasked. Unlike the NUL case it
    /// always STORED fine — a masking miss, not a poison loop, which is exactly why nothing caught
    /// it.</para>
    ///
    /// <para><b>ONE separator per case, deliberately.</b> A single document carrying a tab-separated
    /// AND a NUL-separated PAN does not exercise this at all: the NUL's long escape trips the fast
    /// path on the whole document, the parse strips every control character including the tabs, and
    /// the test passes against the broken probe. Verified by mutation — the combined form stayed
    /// green with the old fast path restored, and these split cases go red.</para>
    /// </summary>
    [Theory]
    [InlineData("tab", "\t")]
    [InlineData("newline", "\n")]
    [InlineData("carriage-return", "\r")]
    [InlineData("backspace", "\b")]
    [InlineData("form-feed", "\f")]
    [InlineData("nul", "\0")]
    public async Task DirectAdd_WithControlCharacterSeparatedPanInMetadata_IsMasked(
        string label, string separator)
    {
        var tenantId = $"t14-auditsan-{Guid.NewGuid():N}";
        var eventId = $"evt_{Guid.NewGuid():N}";
        var metadata = JsonSerializer.Serialize(new
        {
            separator = label,
            split = $"4111{separator}1111{separator}1111{separator}1111",
            note = "kept",
        });

        // Pin the premise the fix rests on: the serializer escapes every control character (nothing
        // raw survives to be caught by the `Any(char.IsControl)` half of the probe), so the escape
        // form is the ONLY signal the fast path has to go on.
        Assert.False(metadata.Any(char.IsControl), "the serializer escapes control characters");
        Assert.Contains("\\", metadata, StringComparison.Ordinal);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId,
                EventType = "test.sanitize",
                Actor = "system",
                SubjectRef = "subject-1",
                Metadata = metadata,
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.EventId == eventId);

        var parsed = JsonDocument.Parse(row.Metadata!).RootElement;
        Assert.Equal("[REDACTED]", parsed.GetProperty("split").GetString());
        Assert.Equal("kept", parsed.GetProperty("note").GetString()); // the rest of the row survives
        Assert.DoesNotContain(Pan, row.Metadata, StringComparison.Ordinal);
    }

    /// <summary>Luhn-valid standard Visa test number — the value that must never reach disk.</summary>
    private const string Pan = "4111111111111111";

    private const char Nul = '\0';
}
