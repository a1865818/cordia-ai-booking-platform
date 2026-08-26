using System.Text.Json;
using Cordia.Domain.Security;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// Shared cross-language redaction corpus (spec 14, Inv-12): the same vectors
/// are asserted against the Python filter by
/// <c>tests/unit_tests/security/test_redaction_parity_corpus.py</c> — a change
/// to either lane that diverges from tools/config/redaction-test-vectors.json
/// fails one suite or the other, so the filters cannot silently drift.
/// </summary>
[Trait("Category", "Unit")]
public class SensitiveDataMaskParityTests
{
    private sealed record Vector(string Input, bool ExpectMasked, string Class);

    public static TheoryData<string, bool, string> Vectors()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tools", "config", "redaction-test-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var data = new TheoryData<string, bool, string>();
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            data.Add(
                v.GetProperty("input").GetString()!,
                v.GetProperty("expect_masked").GetBoolean(),
                v.GetProperty("class").GetString()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SharedCorpusVector(string input, bool expectMasked, string vectorClass)
    {
        // "Did the filter change the value", NOT "does the output contain the token".
        // A `data:` vector masks inside its DECODED payload and re-encodes, so for a base64
        // ref the token is present but base64-encoded, and a Contains() check reads a
        // correctly-masked PAN as unmasked. That is precisely the could-not-fire defect this
        // corpus exists to catch, one level up in the harness itself. Where the token and the
        // PAN actually land inside an encoded ref is asserted by SensitiveDataMaskDataUriTests
        // and MenuArtifactRedactionTests, which decode before asserting.
        var masked = SensitiveDataMask.Sanitize(input) != input;
        Assert.True(
            masked == expectMasked,
            $"{vectorClass}: expected expect_masked={expectMasked} for '{input}'");
    }

    public static TheoryData<string, bool, string> PayloadKeyVectors()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tools", "config", "redaction-test-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var data = new TheoryData<string, bool, string>();
        foreach (var v in doc.RootElement.GetProperty("payload_key_vectors").EnumerateArray())
        {
            data.Add(
                v.GetProperty("key").GetString()!,
                v.GetProperty("expect_exempt").GetBoolean(),
                v.GetProperty("class").GetString()!);
        }
        return data;
    }

    // Pins the structured-payload control-plane exemption (including the exact-name
    // system-minted *_ref allowlist) to the shared corpus: an exemption added or
    // removed in only ONE lane fails that lane's suite, so the lists cannot drift.
    [Theory]
    [MemberData(nameof(PayloadKeyVectors))]
    public void SharedPayloadKeyVector(string key, bool expectExempt, string vectorClass)
    {
        const string pan = "4111111111111111";
        var result = SensitiveDataMask.SanitizeJson($$"""{"{{key}}":"{{pan}}"}""");
        Assert.True(
            expectExempt == result.Contains(pan),
            $"{vectorClass}: expected expect_exempt={expectExempt} for key '{key}'");
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
