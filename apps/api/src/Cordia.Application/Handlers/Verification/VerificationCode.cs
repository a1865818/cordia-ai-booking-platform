using System.Security.Cryptography;
using System.Text;

namespace Cordia.Application.Handlers.Verification;

internal static class VerificationCode
{
    private const string LocalDevelopmentPepper = "cordia-local-verification-code-pepper";

    /// <summary>
    /// Wrong codes a challenge tolerates before it locks (#71). A six-digit code guessed at five
    /// tries per challenge is a 1-in-200,000 chance, and each new challenge is itself a new code.
    /// </summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>
    /// A uniformly random six-digit code from the OS CSPRNG. Replaces a fixed <c>"000000"</c> that
    /// every challenge for every tenant was hashed under — anyone who knew a verification id could
    /// verify it (#71).
    /// </summary>
    public static string Generate() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public static string Hash(VerificationCodeOptions options, string verificationId, string code)
    {
        // Startup validation refuses a host with neither, so reaching this means the options were
        // built outside the host (a bare container). Still fail closed rather than fall back.
        if (!options.IsUsable)
            throw new InvalidOperationException(
                $"No verification code pepper is configured. Set {VerificationCodeOptions.SectionName}:CodePepper "
                + $"or {VerificationCodeOptions.PepperEnvironmentVariable}.");

        var pepper = string.IsNullOrWhiteSpace(options.CodePepper) ? LocalDevelopmentPepper : options.CodePepper;
        var bytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(pepper), Encoding.UTF8.GetBytes($"{verificationId}:{code}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
