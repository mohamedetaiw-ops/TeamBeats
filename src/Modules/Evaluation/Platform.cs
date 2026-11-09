using System.Text.Json;

namespace TeamBeats.Modules.Evaluation;

/// <summary>A case of a platform suite. Platform cases run as an employee of the agent's unit unless a run names a persona.</summary>
public sealed record PlatformCase(string Input, string Locale, CaseExpectations Expect, List<string> Tags, string Severity);

/// <summary>
/// A platform suite (US1 scenario 1, US10): the mandatory safety suites of spec 027 and one suite per launch template,
/// shipped with the product as read-only data. Tenants inherit them and cannot change or remove them.
/// </summary>
public sealed record PlatformSuite(string Key, bool Mandatory, string? Template, int Version, Dictionary<string, string> Names, List<SuiteMetric> Metrics, List<PlatformCase> Cases);

public static class PlatformSuites
{
    private sealed record File(List<PlatformSuite> Suites);

    private static readonly Lazy<IReadOnlyList<PlatformSuite>> Library = new(Load);

    public static IReadOnlyList<PlatformSuite> All => Library.Value;

    public static PlatformSuite? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    /// <summary>What an agent inherits: every mandatory suite, then the suite of its template if there is one.</summary>
    public static IReadOnlyList<PlatformSuite> For(string? templateId) =>
        All.Where(s => s.Mandatory).Concat(All.Where(s => templateId is not null && s.Template == templateId)).ToList();

    private static IReadOnlyList<PlatformSuite> Load()
    {
        var assembly = typeof(PlatformSuites).Assembly;
        var list = new List<PlatformSuite>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Platform.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            list.AddRange(JsonSerializer.Deserialize<File>(stream, EvalJson.Options)?.Suites ?? []);
        }
        if (list.Select(s => s.Key).Distinct().Count() != list.Count)
        {
            throw new InvalidOperationException("Platform suite keys must be unique.");
        }
        return list;
    }
}
