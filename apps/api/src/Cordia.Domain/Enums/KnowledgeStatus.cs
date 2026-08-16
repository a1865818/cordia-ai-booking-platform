using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Whether a knowledge-base entry has an answer, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeStatus
{
    [PgName("needs-answer")]
    [JsonStringEnumMemberName("needs-answer")]
    NeedsAnswer,

    [PgName("answered")]
    [JsonStringEnumMemberName("answered")]
    Answered,
}
