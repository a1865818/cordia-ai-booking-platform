using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>
/// Identity verification ladder for confirmation evidence, stored as a PostgreSQL enum.
/// NOTE: member values are inferred from the minimal→full assurance ladder; pending design
/// ratification (spec 12 §13 open decisions).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerificationLevel
{
    [PgName("unverified")]
    [JsonStringEnumMemberName("unverified")]
    Unverified,

    [PgName("channel_verified")]
    [JsonStringEnumMemberName("channel_verified")]
    ChannelVerified,

    [PgName("identity_verified")]
    [JsonStringEnumMemberName("identity_verified")]
    IdentityVerified,

    [PgName("high_assurance")]
    [JsonStringEnumMemberName("high_assurance")]
    HighAssurance,
}
