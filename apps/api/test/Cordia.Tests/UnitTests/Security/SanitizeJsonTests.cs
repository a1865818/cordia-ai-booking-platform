using Cordia.Domain.Security;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// <see cref="SensitiveDataMask.SanitizeJson"/> — the queue/outbox payload lane (Inv-12),
/// mirroring Python <c>redact_payload</c>: string values are masked via the shared detector,
/// control-plane identifier keys round-trip byte-for-byte, structure is untouched, invalid
/// JSON is masked wholesale, and payloads with nothing to mask return byte-identical (the
/// no-op proof for every existing outbox/scheduled-job fixture shape).
/// </summary>
[Trait("Category", "Unit")]
public class SanitizeJsonTests
{
    // Luhn-valid test PAN (standard Visa test number).
    private const string Pan = "4111111111111111";

    [Fact]
    public void PanInFreeTextField_IsMasked_StructureIntact()
    {
        var json = $$"""{"note":"card {{Pan}} expires soon","operation_id":"op-1"}""";

        var result = SensitiveDataMask.SanitizeJson(json);

        Assert.Contains(SensitiveDataMask.MaskToken, result);
        Assert.DoesNotContain(Pan, result);
        Assert.Contains("\"operation_id\":\"op-1\"", result);
    }

    [Fact]
    public void ControlPlaneKeys_PreserveDigitRunValues()
    {
        // A digit-run under an id/hash key that WOULD mask as free text must round-trip.
        var json = $$"""{"operation_id":"{{Pan}}","payload_hash":"{{Pan}}","booking_ref":"{{Pan}}","schema_version":"{{Pan}}","idempotency_key":"{{Pan}}","id":"{{Pan}}","seq":"{{Pan}}","ts":"{{Pan}}"}""";

        var result = SensitiveDataMask.SanitizeJson(json);

        Assert.Equal(json, result);
    }

    [Fact]
    public void NonControlPlaneKey_WithPanValue_IsMasked()
    {
        var json = $$"""{"customer_note":"{{Pan}}"}""";

        var result = SensitiveDataMask.SanitizeJson(json);

        Assert.DoesNotContain(Pan, result);
        Assert.Contains(SensitiveDataMask.MaskToken, result);
    }

    [Fact]
    public void NumericPanScalar_IsMasked_BenignNumberUntouched()
    {
        // B6: an UNQUOTED numeric PAN must not slip through — the JsonValue branch now sanitizes
        // non-string scalars too. A benign small number stays an untouched number.
        var result = SensitiveDataMask.SanitizeJson("""{"card":4111111111111111,"party":4}""");

        Assert.DoesNotContain(Pan, result);
        Assert.Contains(SensitiveDataMask.MaskToken, result);
        Assert.Contains("\"party\":4", result);
    }

    [Fact]
    public void PanInsideArrayAndNestedObject_IsMasked()
    {
        var json = $$$"""{"items":[{"comment":"{{{Pan}}}"},"plain {{{Pan}}} text"],"nested":{"free":"{{{Pan}}}"}}""";

        var result = SensitiveDataMask.SanitizeJson(json);

        Assert.DoesNotContain(Pan, result);
    }

    [Fact]
    public void NestedStructureUnderIdSuffixedKey_IsStillWalked()
    {
        // The exemption applies only to a string DIRECTLY under the key (redact_payload parity);
        // an object under an *_id key gets no blanket pass.
        var json = $$$"""{"customer_id":{"note":"{{{Pan}}}"}}""";

        var result = SensitiveDataMask.SanitizeJson(json);

        Assert.DoesNotContain(Pan, result);
    }

    [Fact]
    public void InvalidJson_IsMaskedWholesale()
    {
        var raw = $"not json at all {Pan} trailing";

        var result = SensitiveDataMask.SanitizeJson(raw);

        Assert.DoesNotContain(Pan, result);
        Assert.Contains(SensitiveDataMask.MaskToken, result);
    }

    // No-op proof: representative real payload shapes produced by the handlers today
    // (workflow continuations, saga outcomes, notifications, scheduled jobs) pass through
    // BYTE-IDENTICAL — including formatting — so consumer id-extraction cannot break.
    [Theory]
    [InlineData("""{"operation_id":"op-8f3e2c1a4b5d6e7f","tenant_id":"t-cafe-01"}""")]
    [InlineData("""{"saga_id":"pos_1a2b3c4d","operation_id":"op_9z8y7x","preorder":true,"standalone":true}""")]
    [InlineData("""{"notification_id":"ntf_0123456789abcdef","provider_message_id":"pmsg_555000111222","status":"delivered"}""")]
    [InlineData("""{"job_id":"job_42","attempt":3,"run_after":"2026-07-10T01:02:03.456789+00:00"}""")]
    [InlineData("""{ "operation_id": "op-1", "spaced": "formatting preserved" }""")]
    [InlineData("{}")]
    [InlineData("")]
    public void RepresentativeQueuePayloads_PassThroughByteIdentical(string payload)
    {
        Assert.Equal(payload, SensitiveDataMask.SanitizeJson(payload));
    }
}
