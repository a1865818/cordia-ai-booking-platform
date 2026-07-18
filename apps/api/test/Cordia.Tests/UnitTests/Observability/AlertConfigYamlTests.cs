using System.Reflection;
using Cordia.Domain.Observability;

namespace Cordia.Tests.UnitTests.Observability;

/// <summary>
/// Guards the spec 13 §10 config-as-code: tools/config/slos.yaml and tools/config/alerts.yaml.
///
/// No YAML dependency is added — a minimal line parser is sufficient to (a) confirm the files
/// parse into the expected list shapes with the required keys, and (b) extract every alert
/// `metric:` value so it can be cross-checked against the frozen MetricNames registry. This
/// guards against metric-name drift between the alert contract and the code that emits it.
/// </summary>
[Trait("Category", "Unit")]
public class AlertConfigYamlTests
{
    private static string ConfigDir()
    {
        // Walk up from the test assembly location until tools/config is found.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "config");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate tools/config from the test base directory.");
    }

    /// <summary>All string constant values declared on <see cref="MetricNames"/> (the §4.2 registry).</summary>
    private static HashSet<string> KnownMetricNames()
    {
        var names = typeof(MetricNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(MetricNames.FulfillmentAmbiguousOutcomeTotal, names);
        return names;
    }

    /// <summary>Extracts the trimmed value of every <c>metric:</c> key from a YAML text.</summary>
    private static List<string> MetricValues(string yaml) =>
        yaml.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("metric:", StringComparison.Ordinal))
            .Select(l => l["metric:".Length..].Trim())
            .Where(v => v.Length > 0)
            .ToList();

    private static int CountKeyOccurrences(string yaml, string key) =>
        yaml.Split('\n')
            .Count(l => l.TrimStart().StartsWith(key, StringComparison.Ordinal));

    [Fact]
    public void SlosYaml_Exists_AndHasExpectedEntries()
    {
        var path = Path.Combine(ConfigDir(), "slos.yaml");
        Assert.True(File.Exists(path), $"Missing {path}");

        var yaml = File.ReadAllText(path);

        // Top-level list marker + a baseline-pending note per OD-1.
        Assert.Contains("slos:", yaml);
        Assert.Contains("baseline pending measurement", yaml);

        // One id: per SLO entry — the OD-1 table has 9 rows.
        var idCount = CountKeyOccurrences(yaml, "- id:");
        Assert.Equal(9, idCount);
    }

    [Fact]
    public void AlertsYaml_Exists_AndHasExpectedEntries()
    {
        var path = Path.Combine(ConfigDir(), "alerts.yaml");
        Assert.True(File.Exists(path), $"Missing {path}");

        var yaml = File.ReadAllText(path);

        Assert.Contains("alerts:", yaml);

        // One id: per alert — the §25.4 set encoded here has 12 entries.
        var idCount = CountKeyOccurrences(yaml, "- id:");
        Assert.Equal(12, idCount);

        // Each alert carries a metric, condition, invariant, and runbook.
        Assert.Equal(12, CountKeyOccurrences(yaml, "metric:"));
        Assert.Equal(12, CountKeyOccurrences(yaml, "condition:"));
        Assert.Equal(12, CountKeyOccurrences(yaml, "invariant:"));
        Assert.Equal(12, CountKeyOccurrences(yaml, "runbook:"));
    }

    [Fact]
    public void AlertsYaml_EveryMetricMapsToAKnownMetricName()
    {
        var path = Path.Combine(ConfigDir(), "alerts.yaml");
        var yaml = File.ReadAllText(path);

        var known = KnownMetricNames();
        var metrics = MetricValues(yaml);

        Assert.NotEmpty(metrics);
        foreach (var metric in metrics)
        {
            Assert.True(
                known.Contains(metric),
                $"alerts.yaml references metric '{metric}' which is not a constant value in MetricNames (name drift).");
        }
    }

    [Fact]
    public void AlertsYaml_CoversTheFourNewAlertSignalMetrics()
    {
        var path = Path.Combine(ConfigDir(), "alerts.yaml");
        var metrics = MetricValues(File.ReadAllText(path));

        Assert.Contains(MetricNames.AcceptanceIdempotencyConflictTotal, metrics);
        Assert.Contains(MetricNames.FulfillmentCapacityInvariantViolationTotal, metrics);
        Assert.Contains(MetricNames.SecurityTenantCrossScopeAttemptTotal, metrics);
        Assert.Contains(MetricNames.NotificationMissingTerminalTotal, metrics);
    }
}
