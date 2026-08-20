namespace Cordia.Domain.Menu;

/// <summary>
/// Coarse, closed set of reasons a menu extraction failed, recorded in the
/// <c>catalog.extraction_failed</c> audit metadata so an operator can tell a MISCONFIGURATION
/// apart from a MODEL problem without reading the application log (issue #94).
///
/// <para><b>Why this exists.</b> The audit row carried only <c>{ catalog_id }</c> and the owner
/// alert a fixed <c>reason = "extraction_failed"</c>, so "LangGraph is unreachable because
/// <c>LangGraph__Host</c> points at localhost" and "the model returned a response the wire
/// contract refuses" produced byte-identical persisted evidence. The durable record is the only
/// evidence a background lane leaves behind, and it could not distinguish the two failures whose
/// remedies are furthest apart.</para>
///
/// <para><b>Every value is a fixed literal.</b> Nothing here is ever derived from an exception
/// message, a response body, or the uploaded document — those are untrusted, and the whole
/// extraction seam refuses to echo them (Inv-12/16). The category is carried ON the exception
/// (<c>MenuExtractionFailedException.Category</c>) rather than recovered by matching strings in the
/// handler, so a reworded message cannot silently reclassify a failure.</para>
/// </summary>
public static class MenuExtractionFailureCategory
{
    /// <summary>The agent could not be dialled at all — DNS, connection refused, TLS. Almost always configuration.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The call exceeded <c>LangGraph:ExtractTimeoutSeconds</c>.</summary>
    public const string Timeout = "timeout";

    /// <summary>A non-2xx status other than the route's documented model-failure status: 401, 404, 413, 415, 422, 503.</summary>
    public const string HttpError = "http_error";

    /// <summary>The route's own 502 — it reached the model and the model failed on this document.</summary>
    public const string ModelError = "model_error";

    /// <summary>A 2xx whose body the strict wire contract refuses, or whose content this client will not persist.</summary>
    public const string MalformedResponse = "malformed_response";

    /// <summary>The artifact reference could not be turned into menu text; the agent was never called.</summary>
    public const string ArtifactUnresolvable = "artifact_unresolvable";

    /// <summary>
    /// Anything that reached the ingestion handler's fail-secure catch without carrying a category —
    /// i.e. not one of this seam's typed exceptions. A bug rather than a configuration problem, and
    /// deliberately not silent: an audit row with no category at all would read as "not recorded".
    /// </summary>
    public const string Unknown = "unknown";
}
