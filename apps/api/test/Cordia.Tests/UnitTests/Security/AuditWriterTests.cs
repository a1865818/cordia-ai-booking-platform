using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Moq;

namespace Cordia.Tests.UnitTests.Security;

[Trait("Category", "Unit")]
public class AuditWriterTests
{
    private readonly Mock<IAuditEventRepository> _repo = new();

    private static AuditRecord MakeRecord(object? extra = null)
        => new(
            TenantId: "tenant-a",
            ActorType: "operator",
            ActorId: "ops.jane",
            Action: "dead_letter.replayed",
            TargetType: "dead_letter_message",
            TargetId: "msg-1",
            Decision: "allow",
            DataClasses: ["operations"],
            ReasonCode: "provider_outage_recovered",
            CorrelationId: "corr-1",
            OperationId: "op-1",
            Extra: extra);

    [Fact]
    public async Task WriteAsync_MapsOntoTheRatifiedRowShape()
    {
        AuditEvent? written = null;
        _repo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => written = e)
            .Returns(Task.CompletedTask);

        await new AuditWriter(_repo.Object).WriteAsync(MakeRecord(), TestContext.Current.CancellationToken);

        Assert.NotNull(written);
        Assert.Equal("tenant-a", written.TenantId);
        Assert.Equal("dead_letter.replayed", written.EventType);
        Assert.Equal("ops.jane", written.Actor);
        Assert.Equal("msg-1", written.SubjectRef);
        Assert.Equal("corr-1", written.CorrelationId);
        Assert.Equal("op-1", written.OperationId);
        Assert.StartsWith("evt_", written.EventId);

        using var metadata = JsonDocument.Parse(written.Metadata);
        Assert.Equal("operator", metadata.RootElement.GetProperty("actor_type").GetString());
        Assert.Equal("dead_letter_message", metadata.RootElement.GetProperty("target_type").GetString());
        Assert.Equal("allow", metadata.RootElement.GetProperty("decision").GetString());
        Assert.Equal("provider_outage_recovered", metadata.RootElement.GetProperty("reason_code").GetString());
        Assert.Equal("operations", metadata.RootElement.GetProperty("data_classes")[0].GetString());
    }

    [Fact]
    public async Task WriteAsync_MergesExtraFieldsIntoMetadata()
    {
        AuditEvent? written = null;
        _repo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => written = e)
            .Returns(Task.CompletedTask);

        await new AuditWriter(_repo.Object)
            .WriteAsync(MakeRecord(extra: new { replay_command_id = "replay-1" }), TestContext.Current.CancellationToken);

        using var metadata = JsonDocument.Parse(written!.Metadata);
        Assert.Equal("replay-1", metadata.RootElement.GetProperty("replay_command_id").GetString());
    }

    [Fact]
    public async Task WriteAsync_MasksPanShapedValuesInMetadata()
    {
        AuditEvent? written = null;
        _repo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => written = e)
            .Returns(Task.CompletedTask);

        await new AuditWriter(_repo.Object)
            .WriteAsync(MakeRecord(extra: new { note = "customer read card 4111111111111111" }), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("4111111111111111", written!.Metadata);
        Assert.Contains(SensitiveDataMask.MaskToken, written.Metadata);
    }

    [Theory]
    [InlineData("4111 1111 1111 1111", true)]   // Luhn-valid, spaced → masked
    [InlineData("4111-1111-1111-1111", true)]   // Luhn-valid, dashed → masked
    [InlineData("378282246310005", true)]       // 15-digit Amex → masked
    [InlineData("4111111111111112", false)]     // non-Luhn 16 digits → kept
    [InlineData("0412345678", false)]           // 10-digit phone → kept
    public void Sanitizer_MasksOnlyLuhnValidPans(string value, bool masked)
    {
        var result = SensitiveDataMask.Sanitize($"{{\"v\":\"{value}\"}}");
        Assert.Equal(masked, result.Contains(SensitiveDataMask.MaskToken));
        Assert.Equal(!masked, result.Contains(value));
    }
}
