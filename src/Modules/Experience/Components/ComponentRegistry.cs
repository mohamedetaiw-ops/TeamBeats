using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Experience.Components;

/// <summary>
/// The platform component registry (spec 032 FR-024, FR-030, FR-031). Built once at start from every
/// <see cref="IComponentRegistration"/>. A manifest that fails validation is not registered and the reason is logged.
/// </summary>
public sealed partial class ComponentRegistry
{
    public const int PlatformMajor = 1;

    public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.Ordinal)
    {
        "content", "media", "navigation", "data", "people", "services", "personal", "utility",
    };

    private readonly Dictionary<string, ComponentManifest> _byReference = new(StringComparer.Ordinal);

    public ComponentRegistry(IEnumerable<IComponentRegistration> registrations, ILocalizer localizer, ILogger<ComponentRegistry> logger)
    {
        var rejected = new List<(string, string)>();
        foreach (var manifest in registrations.SelectMany(r => r.Manifests()))
        {
            var problem = Validate(manifest, localizer);
            if (problem is null && _byReference.TryGetValue(manifest.Reference, out var existing)
                && !(System.Version.Parse(manifest.Version) > System.Version.Parse(existing.Version)))
            {
                problem = "duplicate_version";
            }
            if (problem is not null)
            {
                rejected.Add(($"{manifest.Id}@{manifest.Version}", problem));
                logger.LogWarning("Component manifest {Component} was not registered: {Reason}", $"{manifest.Id}@{manifest.Version}", problem);
                continue;
            }
            _byReference[manifest.Reference] = manifest;
        }
        foreach (var migration in registrations.SelectMany(r => r.Migrations()))
        {
            // An upgrade must go to the next major of a registered component; anything else is out of range (FR-030).
            var problem = migration.ToMajor != migration.FromMajor + 1 || !_byReference.ContainsKey(migration.From)
                || !_byReference.ContainsKey(migration.To) || _migrations.ContainsKey(migration.From)
                ? "migration_range"
                : null;
            if (problem is not null)
            {
                rejected.Add(($"{migration.From}->{migration.To}", problem));
                logger.LogWarning("Component migration {Migration} was not registered: {Reason}", $"{migration.From}->{migration.To}", problem);
                continue;
            }
            _migrations[migration.From] = migration;
        }
        Rejected = rejected;
    }

    private readonly Dictionary<string, ComponentMigration> _migrations = new(StringComparer.Ordinal);

    /// <summary>The migration hook from <c>id@major</c> to the next major, or null when there is none.</summary>
    public ComponentMigration? MigrationFrom(string reference) => _migrations.GetValueOrDefault(reference);

    /// <summary>Manifests that failed validation, with the reason code.</summary>
    public IReadOnlyList<(string Component, string Reason)> Rejected { get; }

    public IEnumerable<ComponentManifest> All => _byReference.Values.OrderBy(m => m.Id, StringComparer.Ordinal);

    /// <summary>Resolves <c>id@major</c> to the newest registered version of that major.</summary>
    public ComponentManifest? Find(string reference) => _byReference.GetValueOrDefault(reference);

    /// <summary>Validation at registration (FR-024, FR-028, FR-030, FR-031). Returns a reason code, or null when valid.</summary>
    public static string? Validate(ComponentManifest m, ILocalizer localizer)
    {
        if (!ComponentId().IsMatch(m.Id) || !SemVer().IsMatch(m.Version) || string.IsNullOrWhiteSpace(m.Publisher))
        {
            return "identity";
        }
        var range = CompatibilityRange().Match(m.Compatibility);
        if (!range.Success || int.Parse(range.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) != PlatformMajor)
        {
            return "compatibility";
        }
        if (m.Trust != TrustTiers.Platform)
        {
            return "trust_tier";
        }
        if (!Categories.Contains(m.Display.Category))
        {
            return "category";
        }
        foreach (var key in new[] { m.Display.NameKey, m.Display.DescriptionKey })
        {
            if (!localizer.Has(key, "en") || !localizer.Has(key, "ar"))
            {
                return "resource_key";
            }
        }
        if (m.Properties.Any(p => !PropertyName().IsMatch(p.Key) || !ValidSchema(p.Value)))
        {
            return "property_schema";
        }
        if (m.Placement.MinSpan < 1 || m.Placement.MaxSpan > 12 || m.Placement.MinSpan > m.Placement.MaxSpan)
        {
            return "placement";
        }
        if (m.Context.Any(c => !ComponentContext.Approved.Contains(c)))
        {
            return "context";
        }
        if (m.Requires.Capabilities.Any(c => !KnownCapabilities.Contains(c)) || m.Requires.Permissions.Any(p => !Permissions.All.Contains(p))
            || m.Requires.ConnectionFeature is { } feature && !ConnectionFeature().IsMatch(feature))
        {
            return "requirements";
        }
        var events = m.Events.Publishes.Concat(m.Events.Subscribes).ToList();
        if (events.Any(e => !EventName().IsMatch(e.Name) || e.Payload.Values.Any(p => !ValidSchema(p))))
        {
            return "events";
        }
        if (m.Data.Any(d => !d.StartsWith("/api/v1/", StringComparison.Ordinal)))
        {
            return "data_contract";
        }
        if (m.State is not (ComponentStates.Active or ComponentStates.Deprecated or ComponentStates.Retired))
        {
            return "state";
        }
        return null;
    }

    private static bool ValidSchema(PropertySchema s) =>
        PropertyValidator.Types.Contains(s.Type)
        && (s.Type != "enum" || s.Enum is { Count: > 0 })
        && (s.Type != "list" || (s.Items is { } items && items.Type != "list" && ValidSchema(items)));

    private static readonly IReadOnlySet<string> KnownCapabilities = typeof(Capabilities)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetValue(null)!).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex ComponentId();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemVer();

    [GeneratedRegex(@"^\^(\d+)$")]
    private static partial Regex CompatibilityRange();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,39}$")]
    private static partial Regex PropertyName();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,39}$")]
    private static partial Regex EventName();

    /// <summary>A connection feature as <c>kind:feature</c>, for example <c>microsoft365:mail</c>.</summary>
    [GeneratedRegex("^[a-z][a-z0-9]*:[a-z][a-z0-9_]*$")]
    private static partial Regex ConnectionFeature();
}
