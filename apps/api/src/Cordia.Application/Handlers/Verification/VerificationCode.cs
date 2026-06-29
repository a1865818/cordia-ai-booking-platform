using System.Security.Cryptography;
using System.Text;

namespace Cordia.Application.Handlers.Verification;

internal static class VerificationCode
{
    public const string DevelopmentCode = "000000";

    public static string Hash(string verificationId, string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{verificationId}:{code}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
