using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Calendar;

/// <summary>
/// Calendar settings (FR-021): versioned tenant configuration, checked against its schema in code before it is written.
/// Only <c>hr.manage</c> at tenant scope changes them; everyone reads the version in force.
/// </summary>
public sealed class CalendarSettings(IDbSession session, ITenantContext context, IClock clock, IAuthorizationService authz, IAuditWriter audit)
{
    public const int DefaultThreshold = 70;
    public static readonly SettingsView Defaults = new(0, false, DefaultThreshold, CalendarLayers.All, null);

    private SettingsView? _current;

    public async Task<SettingsView> CurrentAsync(CancellationToken ct)
    {
        _current ??= await session.QuerySingleOrDefaultAsync(
            "select version, indirect_reports, threshold, default_layers, updated_at from calendar.settings order by version desc limit 1",
            null, r => new SettingsView(r.GetInt32(0), r.GetBoolean(1), r.GetInt32(2), r.GetFieldValue<string[]>(3), r.GetFieldValue<DateTimeOffset>(4)), ct)
            ?? Defaults;
        return _current;
    }

    public async Task<SettingsView> GetAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.HrManage, AuthzTarget.Tenant, ct);
        return await CurrentAsync(ct);
    }

    public async Task<SettingsView> PutAsync(SettingsRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.HrManage, AuthzTarget.Tenant, ct);
        var current = await CurrentAsync(ct);
        var errors = new ValidationErrors();
        var threshold = request.Threshold ?? current.Threshold;
        if (threshold is < 10 or > 100)
        {
            errors.Add("threshold", "validation.calendar_settings.threshold");
        }
        var layers = (request.DefaultLayers ?? current.DefaultLayers).Distinct(StringComparer.Ordinal).ToArray();
        if (layers.Length == 0 || layers.Any(l => !CalendarLayers.All.Contains(l)))
        {
            errors.Add("defaultLayers", "validation.calendar_settings.layers");
        }
        errors.ThrowIfAny();
        var version = current.Version + 1;
        var indirect = request.IndirectReports ?? current.IndirectReports;
        await session.ExecAsync(
            """
            insert into calendar.settings (tenant_id, version, indirect_reports, threshold, default_layers, updated_by, updated_at)
            values (platform.current_tenant_id(), @version, @indirect, @threshold, @layers, @actor, @now)
            """, new { version, indirect, threshold, layers, actor = context.ActorId, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.CalendarSettingsChanged, "calendar_settings", version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Changes: new { indirectReports = indirect, threshold, defaultLayers = layers }), ct);
        _current = null;
        return await CurrentAsync(ct);
    }
}
