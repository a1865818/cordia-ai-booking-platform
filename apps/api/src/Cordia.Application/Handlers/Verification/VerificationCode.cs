using System.Security.Cryptography;
using System.Text;

namespace Cordia.Application.Handlers.Verification;

internal static class VerificationCode
{
    public const string DevelopmentCode = "000000";
    private const string PepperEnvironmentVariable = "CORDIA_VERIFICATION_CODE_PEPPER";
    private const string LocalDevelopmentPepper = "cordia-local-verification-code-pepper";

    public static string Hash(string verificationId, string code)
    {
        var pepper = Environment.GetEnvironmentVariable(PepperEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pepper) && IsProduction())
            throw new InvalidOperationException($"{PepperEnvironmentVariable} must be set in production.");

        var key = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(pepper) ? LocalDevelopmentPepper : pepper);
        var bytes = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{verificationId}:{code}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsProduction()
        => string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase);
}
