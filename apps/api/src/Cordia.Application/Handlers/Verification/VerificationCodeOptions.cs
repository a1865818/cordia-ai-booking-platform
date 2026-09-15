namespace Cordia.Application.Handlers.Verification;

/// <summary>
/// The HMAC pepper verification codes are hashed under (#71).
///
/// <para><b>Why this is not an environment-name check any more.</b> The pepper used to be refused
/// only when <c>ASPNETCORE_ENVIRONMENT</c> was literally <c>"Production"</c>, so Staging, a typo, or
/// an environment named anything else silently hashed every code under the pepper committed to this
/// repository. It now fails closed: outside Development a real pepper is required, and the committed
/// one is used only where a host explicitly opts in (the test host does, the same way it opts into
/// the tenant header shim).</para>
/// </summary>
public sealed class VerificationCodeOptions
{
    public const string SectionName = "Verification";

    /// <summary>The legacy variable deployments already set; still honoured.</summary>
    public const string PepperEnvironmentVariable = "CORDIA_VERIFICATION_CODE_PEPPER";

    /// <summary>
    /// The deployment's pepper. Bound from <c>Verification:CodePepper</c>, falling back to
    /// <see cref="PepperEnvironmentVariable"/>.
    /// </summary>
    public string? CodePepper { get; set; }

    /// <summary>
    /// Whether the committed local pepper may stand in when no pepper is configured. ON in
    /// Development, OFF elsewhere unless <c>Verification:AllowLocalPepper</c> is set explicitly.
    /// </summary>
    public bool AllowLocalPepper { get; set; }

    /// <summary>True when hashing can proceed: a pepper is configured, or the local one is allowed.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(CodePepper) || AllowLocalPepper;
}
