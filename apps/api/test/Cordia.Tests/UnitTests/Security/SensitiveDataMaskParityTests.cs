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
        var masked = SensitiveDataMask.Sanitize(input).Contains(SensitiveDataMask.MaskToken);
        Assert.True(
            masked == expectMasked,
            $"{vectorClass}: expected expect_masked={expectMasked} for '{input}'");
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
