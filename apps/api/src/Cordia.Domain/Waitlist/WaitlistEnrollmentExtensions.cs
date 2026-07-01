using System.Text.Json;
using Cordia.Domain.Entities;

namespace Cordia.Domain.Waitlist;

/// <summary>
/// Typed access to the <see cref="WaitlistEnrollment"/> jsonb columns (<c>deposit_terms</c> /
/// <c>auto_book</c>, spec 11 §4.4). One shared snake_case <see cref="JsonSerializerOptions"/> is
/// used for both directions so a value serialized here round-trips back to the same record.
/// </summary>
public static class WaitlistEnrollmentExtensions
{
    /// <summary>
    /// Shared options: snake_case wire shape, matching the §4.4 contract field names. Private so the
    /// wire contract can't be mutated from outside — <see cref="JsonSerializerOptions"/> is mutable
    /// until first use, and a shared public instance would let unrelated code change parsing globally.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Parses the enrollment's <c>auto_book</c> jsonb into a typed grant, or <c>null</c> when absent.</summary>
    public static AutoBookGrant? GetAutoBookGrant(this WaitlistEnrollment enrollment)
        => string.IsNullOrWhiteSpace(enrollment.AutoBook)
            ? null
            : JsonSerializer.Deserialize<AutoBookGrant>(enrollment.AutoBook, JsonOptions);

    /// <summary>Parses the enrollment's <c>deposit_terms</c> jsonb into typed terms, or <c>null</c> when absent.</summary>
    public static DepositTerms? GetDepositTerms(this WaitlistEnrollment enrollment)
        => string.IsNullOrWhiteSpace(enrollment.DepositTerms)
            ? null
            : JsonSerializer.Deserialize<DepositTerms>(enrollment.DepositTerms, JsonOptions);

    /// <summary>Serializes an <see cref="AutoBookGrant"/> to its jsonb storage form.</summary>
    public static string ToJson(this AutoBookGrant grant) => JsonSerializer.Serialize(grant, JsonOptions);

    /// <summary>Serializes <see cref="DepositTerms"/> to its jsonb storage form.</summary>
    public static string ToJson(this DepositTerms terms) => JsonSerializer.Serialize(terms, JsonOptions);
}
