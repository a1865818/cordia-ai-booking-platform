using Cordia.Domain.Enums;

namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a caller attempts a catalog state-machine edge that is not in the §5.2
/// allowed-edge set (spec 11 §5.2). Maps to HTTP 409 Conflict.
/// </summary>
public sealed class InvalidCatalogStateTransitionException : DomainException
{
    /// <summary>The status the catalog was in when the illegal transition was attempted.</summary>
    public CatalogStatus From { get; }

    /// <summary>The target status that is not a defined edge from <see cref="From"/>.</summary>
    public CatalogStatus To { get; }

    public InvalidCatalogStateTransitionException(CatalogStatus from, CatalogStatus to)
        : base($"Transition {from} -> {to} is not a defined catalog state-machine edge.")
    {
        From = from;
        To = to;
    }
}
