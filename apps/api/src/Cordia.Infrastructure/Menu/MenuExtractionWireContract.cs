using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordia.Infrastructure.Menu;

/// <summary>
/// The wire shape of <c>POST /internal/menu/extract</c> (issue #87 D-C), hand-authored to mirror
/// <c>cordia_agent.menu.extraction_agent.ExtractionResult</c> and
/// <c>cordia_agent.contracts.catalog.CatalogItem</c> one-for-one.
///
/// <para><b>Why hand-authored and not generated.</b> This is an internal service-to-service RPC
/// that nothing in <c>apps/web</c> consumes, so putting it through the shared-schema pipeline would
/// be speculative — and quicktype cannot emit the one thing that matters here, which is
/// strictness. Drift is pinned instead the way the repo already pins canon.v1: the same fixture
/// JSON is duplicated verbatim in both suites (<c>MenuExtractionWireContractParityTests</c> ↔
/// <c>apps/langgraph/tests/unit_tests/menu/test_http_app.py</c>), so one of the two goes red.</para>
///
/// <para><b><see cref="JsonUnmappedMemberHandling.Disallow"/> is the security property, not tidy
/// housekeeping.</b> The response is derived from an untrusted document read by a model. Default
/// System.Text.Json behaviour would silently DROP a field the model invented — including a
/// tool-shaped one such as <c>tool_calls</c> — which reads as "we are safe because we ignore it",
/// when what actually happened is that nobody found out. Disallow makes it a
/// <see cref="JsonException"/>, the client turns that into a
/// <c>MenuExtractionFailedException</c>, and the catalog settles to <c>ExtractFailed</c>: loud,
/// fail-closed, and visible (Inv-12 / §27.1).</para>
///
/// <para><b>Every record in the graph carries the attribute</b>, nested ones included. It is not
/// inherited: without it on <c>WireCatalogItem</c>, a <c>tool_calls</c> key nested INSIDE an item
/// would be dropped while the envelope-level one was rejected — the attacker-controlled half being
/// the permissive one. <c>NestedUnexpectedField_IsRejected</c> pins that.</para>
/// </summary>
internal static class MenuExtractionWire
{
    /// <summary>
    /// Serializer settings for both directions of this RPC. Property names are declared
    /// explicitly on every member, so nothing here depends on a naming policy being configured at
    /// the call site — a mismatch would turn every field into an unmapped-member throw.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        // Case-SENSITIVE on purpose: the agent emits snake_case and only snake_case. Accepting
        // "ToolCalls" as a match for something is exactly the fuzziness Disallow exists to remove.
        PropertyNameCaseInsensitive = false,
    };
}

/// <summary>The request body: the resolved menu text plus the ref, for log correlation only.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WireExtractRequest(
    [property: JsonPropertyName("menu_text")] string MenuText,
    [property: JsonPropertyName("artifact_ref")] string? ArtifactRef);

/// <summary>The whole sanctioned response envelope: <c>items</c> + <c>flags</c>, nothing else.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WireExtractionResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<WireCatalogItem>? Items,
    [property: JsonPropertyName("flags")] IReadOnlyList<WireExtractionFlag>? Flags);

/// <summary>
/// One extracted row. Every member is nullable because the wire is not a promise: absence is
/// detected and reported by the client's mapping step, never papered over with a default.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WireCatalogItem(
    [property: JsonPropertyName("item_id")] string? ItemId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("price")] WireMoney? Price,
    [property: JsonPropertyName("duration_minutes")] int? DurationMinutes,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("modifiers")] IReadOnlyList<WireCatalogModifier>? Modifiers,
    [property: JsonPropertyName("allergens")] IReadOnlyList<string>? Allergens,
    [property: JsonPropertyName("dietary_labels")] IReadOnlyList<string>? DietaryLabels,
    [property: JsonPropertyName("availability_window")] WireAvailabilityWindow? AvailabilityWindow,
    [property: JsonPropertyName("image_ref")] string? ImageRef,
    [property: JsonPropertyName("extraction_confidence")] double? ExtractionConfidence,
    [property: JsonPropertyName("source_location")] string? SourceLocation);

/// <summary>An amount in ISO-4217 minor units.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WireMoney(
    [property: JsonPropertyName("amount_minor")] long? AmountMinor,
    [property: JsonPropertyName("currency")] string? Currency);

/// <summary>A selectable item modifier.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WireCatalogModifier(
    [property: JsonPropertyName("modifier_id")] string? ModifierId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("price_delta")] WireMoney? PriceDelta);

/// <summary>When an item is orderable; all members optional (null everywhere ⇒ always).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WireAvailabilityWindow(
    [property: JsonPropertyName("days_of_week")] IReadOnlyList<string>? DaysOfWeek,
    [property: JsonPropertyName("start_time")] string? StartTime,
    [property: JsonPropertyName("end_time")] string? EndTime);

/// <summary>An advisory review flag. <c>item_id</c> is null for a document-level flag.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WireExtractionFlag(
    [property: JsonPropertyName("item_id")] string? ItemId,
    [property: JsonPropertyName("reason")] string? Reason);
