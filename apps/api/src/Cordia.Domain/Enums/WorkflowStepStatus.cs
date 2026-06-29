using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Life-cycle status of a workflow step, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowStepStatus
{
    [PgName("pending")]
    [JsonStringEnumMemberName("pending")]
    Pending,

    [PgName("in_progress")]
    [JsonStringEnumMemberName("in_progress")]
    InProgress,

    [PgName("succeeded")]
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [PgName("failed")]
    [JsonStringEnumMemberName("failed")]
    Failed,

    [PgName("terminal")]
    [JsonStringEnumMemberName("terminal")]
    Terminal,
}
