using System.Text.Json;
using Cordia.Infrastructure.Menu;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Pins the menu-extraction wire contract (issue #87 D-C) across the Python↔C# boundary.
///
/// <para>There is no generated contract for this internal RPC on purpose: nothing in
/// <c>apps/web</c> consumes it, and quicktype cannot emit the strictness
/// (<c>JsonUnmappedMemberHandling.Disallow</c>) that is the actual security property. Drift is
/// pinned the way the repo already pins canon.v1 — <see cref="GoldenExtractionResponse"/> below is
/// the same document as <c>GOLDEN_EXTRACTION_RESPONSE</c> in
/// <c>apps/langgraph/tests/unit_tests/menu/test_http_app.py</c>, where it is round-tripped through
/// the Pydantic <c>ExtractionResult</c>. Change one side and exactly one of the two suites goes
/// red. (Same convention as
/// <c>UnitTests/Idempotency/CanonicalHashCrossLanguageParityTests.cs</c> ↔
/// <c>tests/unit_tests/test_canonical_hash.py</c>.)</para>
/// </summary>
[Trait("Category", "Unit")]
public class MenuExtractionWireContractParityTests : IDisposable
{
    private readonly MenuExtractorHarness _harness = new();

    public void Dispose()
    {
        _harness.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The same JSON document as <c>GOLDEN_EXTRACTION_RESPONSE</c> in
    /// <c>apps/langgraph/tests/unit_tests/menu/test_http_app.py</c>: identical keys, identical
    /// values, identical nesting. Only the surface syntax differs — Python's <c>None</c> is JSON
    /// <c>null</c>, and the literals are indented to each language's house style. Do not change
    /// any KEY OR VALUE here without changing it there; that correspondence is the whole
    /// mechanism.
    /// </summary>
    private const string GoldenExtractionResponse =
        """
        {
          "items": [
            {
              "item_id": "item_1",
              "name": "Grilled Salmon",
              "description": "Served with seasonal greens.",
              "price": {"amount_minor": 3200, "currency": "AUD"},
              "duration_minutes": null,
              "category": "Mains",
              "modifiers": null,
              "allergens": ["fish"],
              "dietary_labels": ["gluten-free"],
              "availability_window": {
                "days_of_week": ["mon", "tue"],
                "start_time": "17:00",
                "end_time": "21:00"
              },
              "image_ref": null,
              "extraction_confidence": 0.42,
              "source_location": "page 2, Mains"
            }
          ],
          "flags": [{"item_id": "item_1", "reason": "low_confidence"}]
        }
        """;

    [Fact]
    public void GoldenResponse_DeserializesCleanly_UnderTheStrictContract()
    {
        var payload = JsonSerializer.Deserialize<WireExtractionResponse>(
            GoldenExtractionResponse, MenuExtractionWire.Options);

        Assert.NotNull(payload);

        var item = Assert.Single(payload.Items!);
        Assert.Equal("item_1", item.ItemId);
        Assert.Equal("Grilled Salmon", item.Name);
        Assert.Equal("Served with seasonal greens.", item.Description);
        Assert.Equal(3200, item.Price!.AmountMinor);
        Assert.Equal("AUD", item.Price.Currency);
        Assert.Null(item.DurationMinutes);
        Assert.Equal("Mains", item.Category);
        Assert.Null(item.Modifiers);
        Assert.Equal(["fish"], item.Allergens!);
        Assert.Equal(["gluten-free"], item.DietaryLabels!);
        Assert.Equal(["mon", "tue"], item.AvailabilityWindow!.DaysOfWeek!);
        Assert.Equal("17:00", item.AvailabilityWindow.StartTime);
        Assert.Equal("21:00", item.AvailabilityWindow.EndTime);
        Assert.Null(item.ImageRef);
        Assert.Equal(0.42, item.ExtractionConfidence);
        Assert.Equal("page 2, Mains", item.SourceLocation);

        var flag = Assert.Single(payload.Flags!);
        Assert.Equal("item_1", flag.ItemId);
        Assert.Equal("low_confidence", flag.Reason);
    }

    [Fact]
    public async Task GoldenResponse_MapsToTheExpectedDraftItemsAndFlags()
    {
        var handler = FakeHttpMessageHandler.RespondingJson(GoldenExtractionResponse);

        var result = await _harness
            .Create(handler)
            .ExtractAsync("inline:Grilled Salmon - $32.00", TestContext.Current.CancellationToken);

        // MenuExtractionResult's record equality is REFERENCE-based over its lists, so two
        // structurally identical results are never Assert.Equal. Compare element-wise.
        var item = Assert.Single(result.Items);
        Assert.Equal("item_1", item.ItemId);
        Assert.Equal("Grilled Salmon", item.Name);
        Assert.Equal("Served with seasonal greens.", item.Description);
        Assert.Equal(3200, item.PriceMinor);
        Assert.Equal("AUD", item.Currency);
        Assert.Equal("Mains", item.Category);
        Assert.Null(item.Modifiers);
        Assert.Equal(
            """{"days_of_week":["mon","tue"],"start_time":"17:00","end_time":"21:00"}""",
            item.AvailabilityWindow);
        Assert.Null(item.ImageRef);
        Assert.Equal(0.42, item.ExtractionConfidence);
        Assert.Equal("page 2, Mains", item.SourceLocation);

        // 0.42 is below the agent's 0.7 threshold, so the Python side raises this flag itself —
        // the fixture exercises the real flag path, not a hand-written one.
        var flag = Assert.Single(result.Flags);
        Assert.Equal("item_1", flag.ItemId);
        Assert.Equal("low_confidence", flag.Reason);
    }

    [Fact]
    public void GoldenResponse_PlusOneUnsanctionedKey_IsRejected()
    {
        // The same fixture with a single privileged-looking key added must not deserialize. This
        // is the property the whole D-C decision exists for: the envelope is structurally
        // incapable of carrying an action, and a drifted agent cannot smuggle one past the client
        // by adding a field (Inv-12 / §27.1).
        var tampered = GoldenExtractionResponse.Replace(
            "\"flags\": [", "\"tool_calls\": [\"publish_vendor_catalog\"],\n  \"flags\": [",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<WireExtractionResponse>(tampered, MenuExtractionWire.Options));
    }
}
