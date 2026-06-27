using System.Text.Json;
using Cordia.Application.Handlers.DeadLetters;

namespace Cordia.Tests.UnitTests.Messaging;

/// <summary>
/// Verifies <see cref="ReplayDecision"/> serializes by member NAME (string) in API responses,
/// not as its underlying integer (spec 05 T7 — replay endpoint ergonomics).
/// </summary>
[Trait("Category", "Unit")]
public class ReplayDeadLetterResultSerializationTests
{
    [Theory]
    [InlineData(ReplayDecision.PreservedOriginalMessageId, "PreservedOriginalMessageId")]
    [InlineData(ReplayDecision.NewControlMessageId, "NewControlMessageId")]
    [InlineData(ReplayDecision.AckedWithoutReadmit, "AckedWithoutReadmit")]
    public void Decision_SerializesAsStringName(ReplayDecision decision, string expected)
    {
        var json = JsonSerializer.Serialize(
            new ReplayDeadLetterResult(decision, "replay-abc", "ctl-abc"));

        // The [JsonStringEnumConverter] on the enum emits the quoted member name. If it serialized
        // as the integer instead, the quoted name would not appear.
        Assert.Contains($"\"{expected}\"", json);
    }
}
