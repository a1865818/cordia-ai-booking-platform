namespace Cordia.Domain.Interfaces;

/// <summary>
/// Resolves the opaque artifact reference carried on a menu-ingestion command into the menu
/// text handed across the extraction boundary (<see cref="IIsolatedMenuExtractor"/>).
///
/// <para>Separate from the extractor on purpose: <b>where the bytes come from</b> and
/// <b>what reads them</b> change independently. The current implementation covers only the
/// inline/data slice the console can send today; object-storage references (<c>s3://</c>,
/// <c>blob://</c>, …) land with <b>#84</b> behind this same port, so nothing above it moves
/// when they do.</para>
///
/// <para>A reference this resolver cannot turn into text is a
/// <see cref="Cordia.Domain.Exceptions.MenuArtifactUnresolvableException"/> — never an empty
/// string. An empty menu would look like a successful extraction of nothing and settle the
/// catalog to <c>DraftReady</c> with no items (fail-open); the exception settles it to
/// <c>ExtractFailed</c>, which is the honest outcome.</para>
/// </summary>
public interface IMenuArtifactTextResolver
{
    /// <summary>Resolves <paramref name="artifactRef"/> to the menu text it points at.</summary>
    /// <exception cref="Cordia.Domain.Exceptions.MenuArtifactUnresolvableException">
    /// The reference is of an unsupported form, or its content cannot be read within the
    /// implementation's size bound.
    /// </exception>
    Task<string> ResolveTextAsync(string artifactRef, CancellationToken ct = default);
}
