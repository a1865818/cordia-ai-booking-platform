using System.Security.Cryptography;
using System.Text;

namespace Cordia.Application.Handlers.Verification;

internal static class VerificationCode
{
    public const string DevelopmentCode = "000000";
    private const string LocalDevelopmentPepper = "cordia-local-verification-code-pepper";

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
