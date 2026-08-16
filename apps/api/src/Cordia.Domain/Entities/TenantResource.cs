using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// A bookable thing a customer can actually name: a service ("dinner") or a
/// location ("fitzroy").
///
/// This exists because nothing else in the schema knows what a venue offers.
/// `capacity_slots` carries a bare `resource_id` and no name, and
/// `vendor_catalog_items` is a MENU — dishes, not sittings or rooms. So the
/// agent had no way to turn something a caller said into an identifier, and the
/// slot extractor is (correctly) forbidden from inventing one. Without a source
/// of truth the only workable input was a caller reciting "svc_001" aloud, which
/// no real caller does and no recogniser reliably transcribes.
///
/// The rows are the closed set the resolver matches against. A spoken name that
/// matches nothing here resolves to nothing and the agent asks — which is the
/// property that makes resolution safe: it can only ever emit an identifier the
/// tenant has declared.
/// </summary>
public class TenantResource : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Discriminator: <c>service</c> or <c>location</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The identifier the booking payload carries, e.g. <c>dinner</c>.</summary>
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>Human name, e.g. "Dinner sitting". What a read-back should say.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Other things a customer might call this, lower-cased. "the fitzroy room",
    /// "main dining room". Matched exactly after normalisation — this is a
    /// synonym list, not a fuzzy search.
    /// </summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>Inactive resources are never resolved and never offered.</summary>
    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
