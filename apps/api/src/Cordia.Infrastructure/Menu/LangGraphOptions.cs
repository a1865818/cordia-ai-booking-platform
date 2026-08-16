namespace Cordia.Infrastructure.Menu;

/// <summary>
/// Options for the API → LangGraph menu-extraction RPC (spec 11 §3, issue #87 D-E). Bound from
/// the "LangGraph" configuration section.
///
/// <para><see cref="Host"/> was dead configuration until this seam landed: nothing read it, so
/// nothing noticed that the committed default named <c>https</c> while <c>langgraph dev</c> serves
/// plain HTTP on <c>:2024</c>. It is authoritative now.</para>
/// </summary>
public sealed class LangGraphOptions
{
    public const string SectionName = "LangGraph";

    /// <summary>
    /// Scheme + authority of the LangGraph process, e.g. <c>http://localhost:2024</c>. Must be an
    /// absolute <c>http</c>/<c>https</c> URI whenever the real client is selected — validated at
    /// startup rather than on the first ingestion, because a malformed host would otherwise
    /// surface as an <c>ExtractFailed</c> catalog hours after the deploy.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Path of the extraction route on that process. Mirrors <c>EXTRACT_PATH</c> in
    /// <c>apps/langgraph/cordia_agent/menu/http_app.py</c> — change both or neither.
    /// </summary>
    public string ExtractPath { get; set; } = "/internal/menu/extract";

    /// <summary>
    /// Budget for one extraction call, owned here rather than inherited from
    /// <see cref="System.Net.Http.HttpClient"/>'s 100s default.
    ///
    /// <para><b>This is also a transaction-hold budget.</b> The call runs inside the ingestion
    /// handler's open inbox transaction, so every second here is a second a Postgres transaction
    /// stays open. 120s is a deliberate compromise: an LLM reading a large menu genuinely takes
    /// tens of seconds, and anything shorter would turn slow-but-successful extractions into
    /// <c>ExtractFailed</c> catalogs the owner has to re-upload.</para>
    /// </summary>
    public int ExtractTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Selects <c>StubIsolatedMenuExtractor</c> instead of the real client (D-E). Default
    /// <c>false</c> — the real client runs unless a deployment opts out explicitly, so a missing
    /// configuration key can never silently downgrade production to fabricated sample items.
    /// Set to <c>true</c> in Development and Test only; <c>Program.cs</c> logs loudly if it is on
    /// anywhere else.
    /// </summary>
    public bool UseStubExtractor { get; set; }

    /// <summary>
    /// Whether <see cref="Host"/> is usable as an <see cref="System.Net.Http.HttpClient"/> base
    /// address. Only meaningful when the real client is selected; the stub never dials out.
    /// </summary>
    internal bool HasUsableHost
        => Uri.TryCreate(Host, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
