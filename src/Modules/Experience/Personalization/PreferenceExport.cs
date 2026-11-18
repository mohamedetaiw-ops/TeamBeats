using System.Text.Json;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Personalization;

public sealed record ExportedProfile(int SchemaVersion, IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Devices,
    DateTimeOffset? UpdatedAt);

public sealed record ExportedLayout(Guid PageId, string DeviceClass, int SchemaVersion, int Revision, JsonElement Operations, DateTimeOffset UpdatedAt);

public sealed record ExportedFavorite(string Kind, string Label, string? Path, string? Source, Guid? ItemId, DateTimeOffset CreatedAt);

public sealed record ExportedRecent(string Path, string Label, DateTimeOffset VisitedAt);

/// <summary>
/// The person's own preferences as a documented, versioned file (spec 008 FR-034): format <c>teambeats.preferences</c>,
/// version 1. It holds only rows of the caller in the current tenant: the preference profile with its device values,
/// personal layouts as operations over pages (by page id, never page content), favorites and recent visits.
/// </summary>
public sealed record PreferencesFile(string Format, int Version, DateTimeOffset ExportedAt, ExportedProfile? Profile, IReadOnlyList<ExportedLayout> Layouts,
    IReadOnlyList<ExportedFavorite> Favorites, IReadOnlyList<ExportedRecent> Recents);

/// <summary>Export and reset of the person's own preferences (spec 008 FR-034, T022). Nothing here reads another person's rows.</summary>
public sealed class PreferenceExport(IDbSession session, ITenantContext context, IClock clock)
{
    public const string Format = "teambeats.preferences";
    public const int Version = 1;

    public async Task<PreferencesFile> ExportAsync(CancellationToken ct)
    {
        var user = context.RequireUser();
        var profile = await session.QuerySingleOrDefaultAsync(
            "select schema_version, values::text, device_values::text, updated_at from experience.preference_profile where user_id = @user",
            new { user }, r => new ExportedProfile(r.GetInt32(0),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1), JsonDefaults.Options)!,
                JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(r.GetString(2), JsonDefaults.Options)!
                    .ToDictionary(d => d.Key, d => (IReadOnlyDictionary<string, string>)d.Value),
                r.GetFieldValue<DateTimeOffset>(3)), ct);
        var layouts = await session.QueryAsync(
            "select page_id, device_class, schema_version, revision, operations::text, updated_at from experience.personal_layout where user_id = @user order by page_id, device_class",
            new { user }, r => new ExportedLayout(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                JsonDocument.Parse(r.GetString(4)).RootElement.Clone(), r.GetFieldValue<DateTimeOffset>(5)), ct);
        var favorites = await session.QueryAsync(
            "select kind, label, path, source, item_id, created_at from experience.favorite where user_id = @user order by position, created_at",
            new { user }, r => new ExportedFavorite(r.GetString(0), r.GetString(1), r.Get<string?>("path"), r.Get<string?>("source"), r.Get<Guid?>("item_id"),
                r.GetFieldValue<DateTimeOffset>(5)), ct);
        var recents = await session.QueryAsync(
            "select path, label, visited_at from experience.recent_visit where user_id = @user order by visited_at desc",
            new { user }, r => new ExportedRecent(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)), ct);
        return new PreferencesFile(Format, Version, clock.UtcNow, profile, layouts, favorites, recents);
    }

    /// <summary>Resets the preference profile on every device and every personal layout; favorites and recents stay (FR-034).</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        var user = context.RequireUser();
        await session.ExecAsync(
            "delete from experience.preference_profile where user_id = @user; delete from experience.personal_layout where user_id = @user", new { user }, ct);
    }
}
