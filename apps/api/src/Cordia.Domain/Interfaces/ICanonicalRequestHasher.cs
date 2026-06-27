using System.Text.Json.Nodes;
namespace Cordia.Domain.Interfaces;

/// <summary>Versioned canonical serialization + cryptographic hash of a request body (spec 04 §5).</summary>
public interface ICanonicalRequestHasher
{
    /// <summary>Canonicalize <paramref name="payload"/> per canon.v1 and hash it.</summary>
    CanonicalHash Hash(JsonNode? payload);
}

/// <summary>A version-stamped canonical hash (Inv-3: the version is stored so the binding stays interpretable).</summary>
public readonly record struct CanonicalHash(string Version, string Value);
