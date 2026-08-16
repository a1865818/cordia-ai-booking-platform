using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Who created a booking or operation. Null on historical rows means unknown.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttributedSource
{
    [PgName("agent")]
    [JsonStringEnumMemberName("agent")]
    Agent,

    [PgName("operator")]
    [JsonStringEnumMemberName("operator")]
    Operator,
}
