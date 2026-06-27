using System.Text.Json;

namespace Cordia.Domain.Messaging;

/// <summary>
/// Shared helpers for reading a stored message payload (a JSON string) into a detached
/// <see cref="JsonElement"/> and safely extracting envelope fields from it (§4.1).
///
/// Centralises the "parse-or-fall-back-to-empty-object" defence and the per-field
/// type-checked accessors that the outbox publisher, scheduled-job poller, and dead-letter
/// replay handler would otherwise each hand-roll.
/// </summary>
public static class JobPayload
{
    // Detached once: Clone() makes the element independent of the (undisposed) parse document,
    // so it is safe to share across all callers that hit a malformed payload.
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// Parses <paramref name="json"/> into a detached <see cref="JsonElement"/>.  A malformed
    /// payload is a producer defect; it falls back to an empty object so the caller can still
    /// build a routable envelope (and mark the row) instead of looping forever on unparse-able
    /// input — the consumer rejects/DLQs it if it cannot deserialise.
    /// </summary>
    public static JsonElement ParseOrEmpty(string json)
    {
        try
        {
            // Clone() detaches the element from the document, so the document can be disposed
            // immediately — avoids leaking the parsed JsonDocument on every payload parse.
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>Returns the string property <paramref name="key"/>, or null if absent / not a string.</summary>
    public static string? GetStringOrNull(this JsonElement payload, string key)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(key, out var prop)
           && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>Returns the int32 property <paramref name="key"/>, or null if absent / not a JSON number.</summary>
    public static int? GetInt32OrNull(this JsonElement payload, string key)
        // Guard ValueKind == Number BEFORE TryGetInt32: TryGetInt32 throws (not returns false) when
        // the token is not a number (e.g. a string "3"), which would otherwise break envelope-building
        // on a single malformed optional field instead of treating it as absent.
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(key, out var prop)
           && prop.ValueKind == JsonValueKind.Number
           && prop.TryGetInt32(out var value)
            ? value
            : null;
}
