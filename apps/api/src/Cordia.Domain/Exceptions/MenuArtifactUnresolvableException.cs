namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when an artifact reference cannot be turned into menu text by
/// <c>IMenuArtifactTextResolver</c> — an unsupported reference form (object storage arrives with
/// <b>#84</b>), or content past the resolver's size bound. Settles the catalog to
/// <c>ExtractFailed</c> through the ingestion handler's existing fail-secure catch.
///
/// <para><b>Deliberately does not carry the artifact reference.</b> <c>source_artifact_ref</c> is
/// on the <c>SensitiveDataMask</c> control-plane list — it is written verbatim wherever it appears,
/// unscreened — and its inline form today <i>is the whole menu document</i>. Putting it in an
/// exception message would push untrusted document text straight into the logs (Inv-12). The
/// reference is already on the catalog row for anyone diagnosing a specific failure.</para>
/// </summary>
public sealed class MenuArtifactUnresolvableException : DomainException
{
    public MenuArtifactUnresolvableException(string message) : base(message) { }

    public MenuArtifactUnresolvableException(string message, Exception inner) : base(message, inner) { }
}
