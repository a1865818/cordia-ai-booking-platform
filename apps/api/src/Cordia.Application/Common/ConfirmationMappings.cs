using Cordia.Domain.Enums;
using Cordia.Domain.Generated;

namespace Cordia.Application.Common;

/// <summary>
/// Shared mappers from the generated wire vocabulary to the Domain enums stored on
/// intent confirmations. The booking-operation and manage-booking submit handlers all
/// persist confirmations, so the channel / verification-level mapping lives in one place
/// rather than being copied per handler.
/// </summary>
public static class ConfirmationMappings
{
    /// <summary>
    /// Maps <see cref="ChannelEnum"/> to the lowercase wire vocabulary used by the agent
    /// (<c>email | sms | voice | web</c>) for storage in <c>intent_confirmations.confirmation_channel</c>.
    /// Using an explicit switch rather than <c>.ToString()</c> ensures the stored value is always
    /// the correct wire label regardless of how the C# enum member name is capitalised.
    /// </summary>
    public static string MapConfirmationChannel(ChannelEnum channel) => channel switch
    {
        ChannelEnum.Email => "email",
        ChannelEnum.Sms => "sms",
        ChannelEnum.Voice => "voice",
        ChannelEnum.Web => "web",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unrecognised ConfirmationChannel value."),
    };

    /// <summary>
    /// Maps the string <c>verification_level</c> from the generated <c>Confirmation</c> type
    /// to the Domain <see cref="VerificationLevel"/> enum by PgName / JSON member name.
    /// Defaults to <see cref="VerificationLevel.Unverified"/> for null or unrecognized values.
    /// </summary>
    public static VerificationLevel MapVerificationLevel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return VerificationLevel.Unverified;

        return raw.ToLowerInvariant() switch
        {
            "channel_verified" => VerificationLevel.ChannelVerified,
            "identity_verified" => VerificationLevel.IdentityVerified,
            "high_assurance" => VerificationLevel.HighAssurance,
            "unverified" => VerificationLevel.Unverified,
            _ => VerificationLevel.Unverified,
        };
    }

    /// <summary>
    /// True when <paramref name="raw"/> is a member of the <c>verification_level</c> wire vocabulary.
    /// Edge validators use this so a captured assurance level is rejected up front (400) instead of
    /// being silently coerced to <see cref="VerificationLevel.Unverified"/> by
    /// <see cref="MapVerificationLevel"/> long after the customer enrolled.
    /// </summary>
    public static bool IsKnownVerificationLevel(string raw) => raw.ToLowerInvariant() switch
    {
        "unverified" or "channel_verified" or "identity_verified" or "high_assurance" => true,
        _ => false,
    };
}
