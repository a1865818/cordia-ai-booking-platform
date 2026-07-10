#pragma warning disable xUnit1051
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;

namespace Cordia.Tests.UnitTests.Storage;

/// <summary>
/// <see cref="EncryptedFileObjectStore"/> (spec 14 §24.3/§24.4, gate decision 2026-07-10):
/// AES-256-GCM round-trip, ciphertext-at-rest, tenant path isolation, lazy key resolution
/// (missing key fails on USE), wrong-key rejection, raw-audio 72h default TTL, TTL sweep,
/// and path-traversal rejection.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EncryptedFileObjectStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"cordia-objstore-ut-{Guid.NewGuid():N}");

    private static readonly string KeyA = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string KeyB = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private sealed class FixedSecretStore(string? value) : ISecretStore
    {
        public string? GetSecret(string name) => value;

        public string GetRequiredSecret(string name)
            => value ?? throw new SecretNotFoundException(name);
    }

    private EncryptedFileObjectStore Store(string? key = null, double? rawAudioTtlHours = null)
    {
        var settings = new Dictionary<string, string?> { ["ObjectStore:Root"] = _root };
        if (rawAudioTtlHours is not null)
            settings["ObjectStore:RawAudioTtlHours"] = rawAudioTtlHours.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new EncryptedFileObjectStore(new FixedSecretStore(key ?? KeyA), config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task PutGet_RoundTrips_AndStoresCiphertextOnDisk()
    {
        var store = Store();
        var content = Encoding.UTF8.GetBytes("caller mentioned card 4111 1111 1111 1111");

        await store.PutAsync("t-a", "transcript", "obj-1", content);

        var roundTripped = await store.GetAsync("t-a", "transcript", "obj-1");
        Assert.Equal(content, roundTripped);

        // At rest: the plaintext never appears in the .bin file.
        var binPath = Path.Combine(_root, "t-a", "transcript", "obj-1.bin");
        Assert.True(File.Exists(binPath));
        var raw = await File.ReadAllBytesAsync(binPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("4111", Encoding.Latin1.GetString(raw));

        // Sidecar carries control-plane metadata only.
        var meta = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(_root, "t-a", "transcript", "obj-1.meta.json"),
                TestContext.Current.CancellationToken));
        Assert.Equal("transcript", meta.RootElement.GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task Get_OtherTenant_ReturnsNull_AndPathsAreIsolated()
    {
        var store = Store();
        await store.PutAsync("t-a", "transcript", "obj-1", new byte[] { 1 });

        Assert.Null(await store.GetAsync("t-b", "transcript", "obj-1"));
        Assert.True(Directory.Exists(Path.Combine(_root, "t-a", "transcript")));
        Assert.False(Directory.Exists(Path.Combine(_root, "t-b")));
    }

    [Fact]
    public async Task MissingKey_FailsOnUse_WithSecretNotFound()
    {
        var store = Store(key: null);
        var noKeyStore = new EncryptedFileObjectStore(
            new FixedSecretStore(null),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ObjectStore:Root"] = _root }).Build());

        // Construction succeeded (lazy key); USE throws the typed secret error.
        await Assert.ThrowsAsync<SecretNotFoundException>(
            () => noKeyStore.PutAsync("t-a", "transcript", "obj-1", new byte[] { 1 }));

        // A valid-key store can still write; reading with the keyless store fails on USE too.
        await store.PutAsync("t-a", "transcript", "obj-1", new byte[] { 1 });
        await Assert.ThrowsAsync<SecretNotFoundException>(
            () => noKeyStore.GetAsync("t-a", "transcript", "obj-1"));
    }

    [Fact]
    public async Task WrongKey_FailsAuthentication()
    {
        await Store(KeyA).PutAsync("t-a", "transcript", "obj-1", new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => Store(KeyB).GetAsync("t-a", "transcript", "obj-1"));
    }

    [Fact]
    public async Task RawAudio_WithoutTtl_GetsTheDefault72hExpiry()
    {
        var store = Store();
        var before = DateTimeOffset.UtcNow;
        await store.PutAsync("t-a", "raw-audio", "call-1", new byte[] { 1 });

        var meta = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(_root, "t-a", "raw-audio", "call-1.meta.json"),
                TestContext.Current.CancellationToken));
        var expiresAt = meta.RootElement.GetProperty("expires_at").GetDateTimeOffset();
        Assert.InRange(expiresAt, before.AddHours(71.9), DateTimeOffset.UtcNow.AddHours(72.1));

        // Purposes with no configured default (GD-3 covers raw-audio/transcript/model-trace
        // only) get NO implicit TTL.
        await store.PutAsync("t-a", "attachment", "obj-1", new byte[] { 1 });
        var attachmentMeta = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(_root, "t-a", "attachment", "obj-1.meta.json"),
                TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, attachmentMeta.RootElement.GetProperty("expires_at").ValueKind);
    }

    [Fact]
    public async Task SweepExpired_DeletesExpired_KeepsLive_AndReportsWhatWasPruned()
    {
        var store = Store();
        await store.PutAsync("t-a", "raw-audio", "expired-1", new byte[] { 1 }, ttl: TimeSpan.FromHours(-1));
        await store.PutAsync("t-a", "raw-audio", "live-1", new byte[] { 2 }, ttl: TimeSpan.FromHours(1));
        await store.PutAsync("t-b", "transcript", "no-ttl-1", new byte[] { 3 });

        var pruned = await store.SweepExpiredAsync(DateTimeOffset.UtcNow);

        var only = Assert.Single(pruned);
        Assert.Equal(("t-a", "raw-audio", "expired-1"), (only.TenantId, only.Purpose, only.ObjectId));
        Assert.Null(await store.GetAsync("t-a", "raw-audio", "expired-1"));
        Assert.NotNull(await store.GetAsync("t-a", "raw-audio", "live-1"));
        Assert.NotNull(await store.GetAsync("t-b", "transcript", "no-ttl-1"));
        Assert.False(File.Exists(Path.Combine(_root, "t-a", "raw-audio", "expired-1.bin")));
        Assert.False(File.Exists(Path.Combine(_root, "t-a", "raw-audio", "expired-1.meta.json")));
    }

    [Fact]
    public async Task ExpiredButNotYetSwept_IsAlreadyUnreadable()
    {
        var store = Store();
        await store.PutAsync("t-a", "raw-audio", "expired-1", new byte[] { 1 }, ttl: TimeSpan.FromHours(-1));

        Assert.Null(await store.GetAsync("t-a", "raw-audio", "expired-1"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData(".hidden")]
    [InlineData("")]
    public async Task TraversalShapedSegments_AreRejected(string hostile)
    {
        var store = Store();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.PutAsync(hostile, "transcript", "obj-1", new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.PutAsync("t-a", hostile, "obj-1", new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.PutAsync("t-a", "transcript", hostile, new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetAsync(hostile, "transcript", "obj-1"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.DeleteAsync("t-a", "transcript", hostile));
    }

    [Fact]
    public async Task ListIds_ReturnsPrefixMatches_OrdinalSorted_TenantIsolated()
    {
        var store = Store();
        await store.PutAsync("t-a", "transcript", "sess-1.000002", new byte[] { 2 });
        await store.PutAsync("t-a", "transcript", "sess-1.000001", new byte[] { 1 });
        await store.PutAsync("t-a", "transcript", "sess-2.000001", new byte[] { 3 });
        await store.PutAsync("t-b", "transcript", "sess-1.000009", new byte[] { 9 });

        var ids = await store.ListIdsAsync("t-a", "transcript", "sess-1.");

        Assert.Equal(["sess-1.000001", "sess-1.000002"], ids);
        Assert.Empty(await store.ListIdsAsync("t-c", "transcript", "sess-1."));
        Assert.Equal(3, (await store.ListIdsAsync("t-a", "transcript")).Count); // empty prefix = all
    }

    [Theory]
    [InlineData("../x")]
    [InlineData(@"..\x")]
    [InlineData("a/b")]
    public async Task ListIds_RejectsTraversalShapedPrefixes(string hostile)
    {
        var store = Store();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ListIdsAsync("t-a", "transcript", hostile));
    }

    [Fact]
    public async Task TranscriptAndModelTrace_GetTheirConfigDefaultTtls_TraceStrictlyShorter()
    {
        // GD-3 (2026-07-11): transcript 90d, model-trace 14d — and the trace TTL must stay
        // STRICTLY shorter than the transcript TTL (spec 13 §25.3 "stricter retention").
        var store = Store();
        var before = DateTimeOffset.UtcNow;
        await store.PutAsync("t-a", "transcript", "sess-1.000001", new byte[] { 1 });
        await store.PutAsync("t-a", "model-trace", "trace-1", new byte[] { 2 });

        static DateTimeOffset ExpiresAt(string root, string purpose, string id)
        {
            using var meta = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "t-a", purpose, id + ".meta.json")));
            return meta.RootElement.GetProperty("expires_at").GetDateTimeOffset();
        }

        var transcriptExpiry = ExpiresAt(_root, "transcript", "sess-1.000001");
        var traceExpiry = ExpiresAt(_root, "model-trace", "trace-1");
        Assert.InRange(transcriptExpiry, before.AddDays(89.9), DateTimeOffset.UtcNow.AddDays(90.1));
        Assert.InRange(traceExpiry, before.AddDays(13.9), DateTimeOffset.UtcNow.AddDays(14.1));
        Assert.True(traceExpiry < transcriptExpiry, "model-trace retention must be strictly shorter than transcript retention");
    }

    [Fact]
    public async Task MissingMeta_FailsClosed_GetReturnsNull()
    {
        // M3: a decryptable blob whose meta sidecar is gone has no provable retention state —
        // it must NOT decrypt-and-return (the previous behavior leaked it fail-open).
        var store = Store();
        await store.PutAsync("t-a", "transcript", "obj-1", new byte[] { 1, 2, 3 });
        File.Delete(Path.Combine(_root, "t-a", "transcript", "obj-1.meta.json"));

        Assert.Null(await store.GetAsync("t-a", "transcript", "obj-1"));
    }

    [Fact]
    public async Task CorruptMeta_FailsClosed_GetReturnsNull()
    {
        var store = Store();
        await store.PutAsync("t-a", "transcript", "obj-1", new byte[] { 1, 2, 3 });
        await File.WriteAllTextAsync(
            Path.Combine(_root, "t-a", "transcript", "obj-1.meta.json"),
            "{ this is not valid json",
            TestContext.Current.CancellationToken);

        Assert.Null(await store.GetAsync("t-a", "transcript", "obj-1"));
    }

    [Fact]
    public async Task NoTtlObject_WithIntactMeta_IsStillServed()
    {
        // Fail-closed must not become fail-shut: a present meta with a null ExpiresAt is a
        // legitimate no-TTL object and must round-trip.
        var store = Store();
        await store.PutAsync("t-a", "attachment", "obj-1", new byte[] { 7, 8, 9 });

        Assert.Equal(new byte[] { 7, 8, 9 }, await store.GetAsync("t-a", "attachment", "obj-1"));
    }

    [Fact]
    public async Task SweepExpired_PrunesOrphanBlobWithMissingMeta_FailClosed()
    {
        // M3: a blob whose meta is absent would be invisible to a meta-driven sweep and linger
        // forever as unreadable ciphertext. The blob-driven sweep prunes it, reason-coded.
        var store = Store();
        await store.PutAsync("t-a", "transcript", "orphan-1", new byte[] { 1, 2, 3 });
        File.Delete(Path.Combine(_root, "t-a", "transcript", "orphan-1.meta.json"));

        var pruned = await store.SweepExpiredAsync(DateTimeOffset.UtcNow);

        var only = Assert.Single(pruned);
        Assert.Equal(("t-a", "transcript", "orphan-1"), (only.TenantId, only.Purpose, only.ObjectId));
        Assert.Equal("missing_or_corrupt_meta", only.Reason);
        Assert.Null(only.ExpiresAt);
        Assert.False(File.Exists(Path.Combine(_root, "t-a", "transcript", "orphan-1.bin")));
    }

    [Fact]
    public async Task TruncatedBlob_ShorterThanNonceAndTag_FailsClosed_NoUnderflow()
    {
        // M3-adjacent: a blob shorter than nonce(12)+tag(16) must return null, not throw an
        // ArgumentOutOfRange from a negative plaintext length.
        var store = Store();
        await store.PutAsync("t-a", "transcript", "obj-1", new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "t-a", "transcript", "obj-1.bin"),
            new byte[10],
            TestContext.Current.CancellationToken);

        Assert.Null(await store.GetAsync("t-a", "transcript", "obj-1"));
    }

    [Fact]
    public async Task EmptyJsonMeta_FailsClosed_GetReturnsNull_AndSweepPrunesIt()
    {
        // M3 gap: a meta corrupted to valid-but-empty JSON ("{}") deserializes with a null
        // Purpose/ExpiresAt. It must be treated as corrupt (fail closed), not as a live no-TTL
        // object served forever.
        var store = Store();
        await store.PutAsync("t-a", "transcript", "obj-1", new byte[] { 1, 2, 3 });
        await File.WriteAllTextAsync(
            Path.Combine(_root, "t-a", "transcript", "obj-1.meta.json"),
            "{}",
            TestContext.Current.CancellationToken);

        Assert.Null(await store.GetAsync("t-a", "transcript", "obj-1"));

        var pruned = await store.SweepExpiredAsync(DateTimeOffset.UtcNow);
        Assert.Contains(pruned, p => p.ObjectId == "obj-1" && p.Reason == "missing_or_corrupt_meta");
    }

    [Fact]
    public async Task SweepExpired_ReapsOrphanMetaPastGrace_ButKeepsRecentOne()
    {
        // A meta-only orphan (blob gone) — e.g. a crash between PutAsync's meta and blob writes.
        var store = Store();
        await store.PutAsync("t-a", "transcript", "orphan-meta-1", new byte[] { 1, 2, 3 });
        File.Delete(Path.Combine(_root, "t-a", "transcript", "orphan-meta-1.bin"));
        var metaPath = Path.Combine(_root, "t-a", "transcript", "orphan-meta-1.meta.json");
        Assert.True(File.Exists(metaPath));

        // Inside the grace window a fresh orphan meta is left alone (must not race an in-flight Put).
        await store.SweepExpiredAsync(DateTimeOffset.UtcNow);
        Assert.True(File.Exists(metaPath), "a fresh orphan meta must not be reaped (grace window)");

        // Well past the grace window it is reaped.
        await store.SweepExpiredAsync(DateTimeOffset.UtcNow.AddHours(2));
        Assert.False(File.Exists(metaPath), "an orphan meta older than the grace window is reaped");
    }

    [Fact]
    public async Task Put_LeavesNoTempFilesBehind()
    {
        var store = Store();
        await store.PutAsync("t-a", "transcript", "obj-1", new byte[] { 1, 2, 3 });

        var stray = Directory.EnumerateFiles(_root, "*.tmp-*", SearchOption.AllDirectories);
        Assert.Empty(stray);
    }

    [Fact]
    public async Task Delete_RemovesObjectAndSidecar()
    {
        var store = Store();
        await store.PutAsync("t-a", "transcript", "obj-1", new byte[] { 1 });

        Assert.True(await store.DeleteAsync("t-a", "transcript", "obj-1"));
        Assert.Null(await store.GetAsync("t-a", "transcript", "obj-1"));
        Assert.False(File.Exists(Path.Combine(_root, "t-a", "transcript", "obj-1.meta.json")));
        Assert.False(await store.DeleteAsync("t-a", "transcript", "obj-1")); // idempotent
    }
}
