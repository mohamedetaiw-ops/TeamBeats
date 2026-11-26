using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// One configured KPI tile (spec 021 US6 scenario 2): the metric name a KPI feed publishes (spec 017 FR-022), its name
/// per language, and the permission a viewer must also hold for the whole tenant to see it. Without a permission, the
/// location dashboard's own <c>analytics.view</c> for the whole tenant is enough.
/// </summary>
public sealed record KpiTileDefinition(Guid Id, string Metric, IReadOnlyDictionary<string, string> Names, string? Permission);

public sealed record KpiTileSet(IReadOnlyList<KpiTileDefinition> Tiles);

public sealed record KpiTileSetView(IReadOnlyList<KpiTileDefinition> Tiles, int Version, DateTimeOffset? ChangedAt, bool CanChange);

public sealed record KpiTileInput(Guid? Id, string? Metric, IReadOnlyDictionary<string, string>? Names, string? Permission);

public sealed record KpiTileSetRequest(IReadOnlyList<KpiTileInput>? Tiles, int? Version);

/// <summary>A KPI tile on one location's dashboard: the latest reading for the location, with its source and when it was measured and received.</summary>
public sealed record KpiTileView(Guid Id, string Metric, IReadOnlyDictionary<string, string> Names, decimal? Value, string? Unit, DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd, string? Source, DateTimeOffset? SourceTime, DateTimeOffset? ReceivedAt);

/// <summary>
/// KPI tiles on location dashboards (spec 021 R3, US6 scenario 2, FR-010), under <c>analytics.kpi_tiles</c>. The tile set
/// is versioned tenant configuration (constitution XXIX), changed by people who manage tenant settings and audited as
/// <c>analytics.kpi_tile_configured</c>. Values come only through the KPI feed contract of spec 017; a tile shows the
/// latest reading for the location up to the end of the dashboard's range, with the connection it came from and the
/// source and receipt times, and is left out for a viewer without its permission. KPI values are business figures, not
/// person figures, so the minimum group size does not apply, and no group average is shown.
/// </summary>
public sealed partial class KpiTiles(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context, IKpiFeed feed,
    IAuditWriter audit, IClock clock)
{
    public const int MaxTiles = 12;
    public const string AuditTarget = "analytics_kpi_tiles";
    private static readonly string[] Languages = ["en", "ar"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._:/-]{0,99}$")]
    private static partial Regex MetricName();

    private async Task<(KpiTileSet Set, int Version, DateTimeOffset? At)> CurrentAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync("select version, document::text, changed_at from analytics.kpi_tile_set order by version desc limit 1", null,
            r => (Version: r.GetInt32(0), Document: r.GetString(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        return rows.Count == 0 ? (new KpiTileSet([]), 0, null) : (JsonSerializer.Deserialize<KpiTileSet>(rows[0].Document, Json)!, rows[0].Version, rows[0].At);
    }

    private async Task<bool> MayChangeAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct)).Allowed;

    public async Task<KpiTileSetView> ViewAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsKpiTiles, ct);
        context.RequireUser();
        var change = await MayChangeAsync(ct);
        if (!change && !(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var (set, version, at) = await CurrentAsync(ct);
        return new KpiTileSetView(set.Tiles, version, at, change);
    }

    public async Task<KpiTileSetView> ChangeAsync(KpiTileSetRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsKpiTiles, ct);
        var user = context.RequireUser();
        if (!await MayChangeAsync(ct))
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        var inputs = request.Tiles ?? [];
        if (inputs.Count > MaxTiles)
        {
            errors.Add("tiles", "validation.analytics.kpi_tiles_count");
        }
        var tiles = new List<KpiTileDefinition>();
        var metrics = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(inputs.Count, MaxTiles); i++)
        {
            var input = inputs[i];
            var metric = input.Metric?.Trim();
            if (metric is null || !MetricName().IsMatch(metric) || !metrics.Add(metric))
            {
                errors.Add($"tiles[{i}].metric", "validation.analytics.kpi_metric");
            }
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var language in Languages)
            {
                var name = input.Names?.GetValueOrDefault(language)?.Trim();
                if (name is { Length: > 0 and <= 80 })
                {
                    names[language] = name;
                }
                else
                {
                    errors.Add($"tiles[{i}].names.{language}", "validation.analytics.kpi_name");
                }
            }
            var permission = string.IsNullOrWhiteSpace(input.Permission) ? null : input.Permission.Trim();
            if (permission is not null && !Permissions.All.Contains(permission))
            {
                errors.Add($"tiles[{i}].permission", "validation.analytics.kpi_permission");
            }
            tiles.Add(new KpiTileDefinition(input.Id ?? Ids.New(), metric ?? "", names, permission));
        }
        if (request.Version is null)
        {
            errors.Add("version", "validation.analytics.version");
        }
        errors.ThrowIfAny();
        var (before, version, _) = await CurrentAsync(ct);
        if (request.Version != version)
        {
            throw AppException.Conflict("conflict.analytics.kpi_tiles_version");
        }
        var after = new KpiTileSet(tiles);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into analytics.kpi_tile_set (tenant_id, version, schema_version, document, changed_at, changed_by)
            values (platform.current_tenant_id(), @version, 1, @document::jsonb, @now, @user)
            """, new { version = version + 1, document = JsonSerializer.Serialize(after, Json), now, user }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AnalyticsKpiTileConfigured, AuditTarget, null,
            Changes: new Dictionary<string, object>
            {
                ["tiles"] = new { before = before.Tiles.Select(t => new { t.Metric, t.Permission }), after = tiles.Select(t => new { t.Metric, t.Permission }) },
                ["version"] = new { before = version, after = version + 1 },
            }), ct);
        return new KpiTileSetView(tiles, version + 1, now, true);
    }

    /// <summary>
    /// The tiles one location's dashboard shows to the viewer, or null without <c>analytics.kpi_tiles</c>. The caller has
    /// already checked the dashboard itself (<c>analytics.view</c> for the whole tenant).
    /// </summary>
    public async Task<IReadOnlyList<KpiTileView>?> ForLocationAsync(string location, DateTimeOffset until, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AnalyticsKpiTiles, ct))
        {
            return null;
        }
        var (set, _, _) = await CurrentAsync(ct);
        var result = new List<KpiTileView>();
        foreach (var tile in set.Tiles)
        {
            if (tile.Permission is { } permission && !(await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed)
            {
                continue;
            }
            var reading = (await feed.ReadAsync(new KpiQuery(tile.Metric, location, null, until, 1), ct)).FirstOrDefault();
            result.Add(reading is null
                ? new KpiTileView(tile.Id, tile.Metric, tile.Names, null, null, null, null, null, null, null)
                : new KpiTileView(tile.Id, tile.Metric, tile.Names, reading.Value, reading.Unit, reading.PeriodStart, reading.PeriodEnd, reading.Source, reading.SourceTime,
                    reading.ReceivedAt));
        }
        return result;
    }
}
