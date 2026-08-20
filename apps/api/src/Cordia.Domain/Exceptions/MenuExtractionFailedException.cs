using Cordia.Domain.Menu;

namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a menu extraction reaches a definite failure — transport, model, or a response
/// the strict wire contract refuses (spec 11 §5.2, §27.6). Fail-CLOSED: the extraction seam must
/// never turn a failure into a successful-looking empty result, which would settle the catalog to
/// <c>DraftReady</c> with no items and read to the owner as "your menu is empty".
///
/// <para><c>IngestVendorMenuCommandHandler</c> catches it inside the inbox transaction and settles
/// the catalog to <c>ExtractFailed</c>, so it never escapes as an HTTP status.</para>
///
/// <para><b>The message must stay free of extracted content.</b> A response body from the
/// extraction agent is derived from the uploaded document and is untrusted; only a coarse
/// category or status code belongs here (Inv-12/Inv-16).</para>
///
/// <para><b>The failure CATEGORY rides on the exception</b> (<see cref="Category"/>), so the
/// ingestion handler can record which kind of failure it settled without matching on the message
/// text — the one place a reworded string could silently reclassify a misconfiguration as a model
/// problem. Values come from <see cref="MenuExtractionFailureCategory"/> and nowhere else.</para>
/// </summary>
public sealed class MenuExtractionFailedException : DomainException
{
    public MenuExtractionFailedException(string message, string category) : base(message)
        => Category = category;

    public MenuExtractionFailedException(string message, string category, Exception inner)
        : base(message, inner) => Category = category;

    /// <summary>
    /// One of the <see cref="MenuExtractionFailureCategory"/> tokens. A fixed literal chosen by the
    /// throw site — never a captured exception message, status text or response body.
    /// </summary>
    public string Category { get; }
}
