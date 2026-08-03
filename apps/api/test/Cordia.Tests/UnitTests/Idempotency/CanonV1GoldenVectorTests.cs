using System.Text.Json;
using System.Text.Json.Nodes;
using Cordia.Infrastructure.Services;

namespace Cordia.Tests.UnitTests.Idempotency;

/// <summary>
/// Shared cross-language canon.v1 corpus (spec 04 §5, Inv-2/Inv-3).
///
/// <see cref="CanonicalRequestHasher"/> is the authoritative implementation; the expected
/// hashes in <c>tools/config/canon-v1-test-vectors.json</c> were produced by it. The same
/// corpus is asserted against the TypeScript port by
/// <c>apps/web/src/api/canonicalHash.test.ts</c> and the Python port by
/// <c>tests/unit_tests/test_canonical_hash.py</c>, so a change in any one lane that
/// diverges fails that lane's suite — the three canonicalizers cannot silently drift.
///
/// This matters because the acceptance endpoint rejects a submission whose client-computed
/// <c>confirmation.intent_payload_hash</c> does not equal the server's recomputed hash
/// (422 <c>ConfirmationHashMismatchException</c>): a drifted client cannot book at all.
/// </summary>
[Trait("Category", "Unit")]
public class CanonV1GoldenVectorTests
{
    private readonly CanonicalRequestHasher _hasher = new();

    public static TheoryData<string, string, string> Vectors()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var vector in LoadVectors())
        {
            data.Add(
                vector.GetProperty("name").GetString()!,
                vector.GetProperty("payload").GetString()!,
                vector.GetProperty("expected").GetString()!);
        }
        return data;
    }

    /// <summary>Every corpus payload hashes to its pinned value.</summary>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void GoldenVector(string name, string payload, string expected)
    {
        var actual = _hasher.Hash(JsonNode.Parse(payload)).Value;
        Assert.True(actual == expected, $"{name}: expected {expected}, got {actual}");
    }

    /// <summary>The corpus is well-formed: 64-char lowercase hex, unique names.</summary>
    [Fact]
    public void CorpusIsWellFormed()
    {
        var vectors = LoadVectors();
        Assert.NotEmpty(vectors);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in vectors)
        {
            var name = vector.GetProperty("name").GetString()!;
            Assert.True(names.Add(name), $"duplicate vector name: {name}");

            var expected = vector.GetProperty("expected").GetString()!;
            Assert.True(expected.Length == 64, $"{name}: expected a 64-char hash, got {expected.Length}");
            Assert.True(
                expected.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
                $"{name}: expected lowercase hex, got '{expected}'");
        }
    }

    /// <summary>
    /// Payloads sharing an <c>equivalence</c> label must hash identically — this is what
    /// pins the algorithm's equivalence classes (key order, null≡absent, NFC, whitespace,
    /// number forms, timestamp normalisation, array ordering) rather than just its output.
    /// </summary>
    [Fact]
    public void EquivalenceGroupsAgree()
    {
        var groups = new Dictionary<string, (string Name, string Hash)>(StringComparer.Ordinal);

        foreach (var vector in LoadVectors())
        {
            var name = vector.GetProperty("name").GetString()!;
            var label = vector.GetProperty("equivalence").GetString()!;
            var hash = _hasher.Hash(JsonNode.Parse(vector.GetProperty("payload").GetString()!)).Value;

            if (groups.TryGetValue(label, out var first))
            {
                Assert.True(
                    first.Hash == hash,
                    $"equivalence '{label}': '{name}' hashed {hash} but '{first.Name}' hashed {first.Hash}");
            }
            else
            {
                groups[label] = (name, hash);
            }
        }

        Assert.NotEmpty(groups);
    }

    private static List<JsonElement> LoadVectors()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tools", "config", "canon-v1-test-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal("canon.v1", doc.RootElement.GetProperty("version").GetString());

        return doc.RootElement.GetProperty("vectors").EnumerateArray().Select(v => v.Clone()).ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root (nx.json) not found");
    }
}
