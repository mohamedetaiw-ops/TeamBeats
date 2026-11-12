using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

/// <summary>One resolved rule (FR-003).</summary>
internal sealed record Rule(string Source, Guid[] UnitIds, int AdvanceDays, int MinMinutes, int MaxMinutes, bool WorkingHours, int BufferMinutes, int MaxActive, bool Overnight,
    int SeriesDays, int Version)
{
    public RuleView View => new(Source, UnitIds, AdvanceDays, MinMinutes, MaxMinutes, WorkingHours, BufferMinutes, MaxActive, Overnight, SeriesDays, Version);
}

internal sealed record Settings(int SlotMinutes, int CheckInBeforeMinutes, int CheckInGraceMinutes, int ApprovalHoldHours, int NoShowLimit, int NoShowWindowDays,
    bool NoShowRestrict, int NoShowRestrictDays, int NoShowAdvanceDays, bool ShowNames, int Version)
{
    public static readonly Settings Defaults = new(15, 15, 15, 24, 3, 30, false, 14, 1, false, 0);

    public SettingsView View => new(SlotMinutes, CheckInBeforeMinutes, CheckInGraceMinutes, ApprovalHoldHours, NoShowLimit, NoShowWindowDays, NoShowRestrict, NoShowRestrictDays,
        NoShowAdvanceDays, ShowNames, Version);
}

/// <summary>
/// Booking rules and the tenant's booking settings (spec 031 FR-003, FR-010, section 6). A resource follows its own
/// rule, else the rule for its kind at its site, else the rule for its kind in the tenant, else the defaults. The
/// rules are read on the server for every booking path.
/// </summary>
public sealed class BookingRules(IDbSession session, ITenantContext context, IAuthorizationService authz, Facilities facilities, IAuditWriter audit, IClock clock)
{
    private sealed record RuleRow(Guid? ResourceId, Guid? SiteId, string? Kind, Rule Rule);

    /// <summary>
    /// Defaults when nobody set a rule (section 6): 14 days ahead, three active desk bookings, within opening hours on
    /// working days. Equipment is lent for days at a time, so it may run overnight and outside opening hours.
    /// </summary>
    internal static Rule Default(string kind) => kind switch
    {
        ResourceKinds.Room => new("default", [], 14, 15, 480, true, 0, 10, false, 90, 0),
        ResourceKinds.Desk => new("default", [], 14, 15, 720, true, 0, 3, false, 90, 0),
        ResourceKinds.Parking => new("default", [], 14, 15, 720, true, 0, 3, false, 90, 0),
        _ => new("default", [], 14, 15, 10080, false, 0, 3, true, 90, 0),
    };

    private const string Columns = "resource_id, site_id, kind, unit_ids, advance_days, min_minutes, max_minutes, working_hours, buffer_minutes, max_active, overnight, series_days, version";

    private static RuleRow Read(Npgsql.NpgsqlDataReader r)
    {
        var resource = r.Get<Guid?>("resource_id");
        var site = r.Get<Guid?>("site_id");
        var source = resource is not null ? "resource" : site is not null ? "site" : "tenant";
        return new RuleRow(resource, site, r.Get<string>("kind"), new Rule(source, r.GetFieldValue<Guid[]>(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetBoolean(7),
            r.GetInt32(8), r.GetInt32(9), r.GetBoolean(10), r.GetInt32(11), r.GetInt32(12)));
    }

    /// <summary>All rules of the tenant in one read; the table holds a few rows per site at most.</summary>
    internal async Task<Func<ResourceRow, Rule>> ResolverAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync($"select {Columns} from booking.rule", null, Read, ct);
        var byResource = rows.Where(r => r.ResourceId is not null).ToDictionary(r => r.ResourceId!.Value, r => r.Rule);
        var bySite = rows.Where(r => r.ResourceId is null && r.SiteId is not null).ToDictionary(r => (r.SiteId!.Value, r.Kind!), r => r.Rule);
        var byKind = rows.Where(r => r.ResourceId is null && r.SiteId is null).ToDictionary(r => r.Kind!, r => r.Rule);
        return resource => byResource.TryGetValue(resource.Id, out var own) ? own
            : bySite.TryGetValue((resource.SiteId, resource.Kind), out var site) ? site
            : byKind.TryGetValue(resource.Kind, out var kind) ? kind
            : Default(resource.Kind);
    }

    internal async Task<Rule> ForAsync(ResourceRow resource, CancellationToken ct) => (await ResolverAsync(ct))(resource);

    internal async Task<Settings> SettingsAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            """
            select slot_minutes, check_in_before_minutes, check_in_grace_minutes, approval_hold_hours, no_show_limit, no_show_window_days, no_show_restrict,
                   no_show_restrict_days, no_show_advance_days, show_names, version
            from booking.setting
            """, null,
            r => new Settings(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetBoolean(6), r.GetInt32(7), r.GetInt32(8),
                r.GetBoolean(9), r.GetInt32(10)), ct) ?? Settings.Defaults;

    // ---- Facilities administrators ----

    public async Task<SettingsView> GetSettingsAsync(CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        await authz.DemandAsync(Permissions.BookingManage, AuthzTarget.Tenant, ct);
        return (await SettingsAsync(ct)).View;
    }

    public async Task<SettingsView> SetSettingsAsync(SettingsRequest request, CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        await authz.DemandAsync(Permissions.BookingManage, AuthzTarget.Tenant, ct);
        var current = await SettingsAsync(ct);
        if (request.Version is { } v && v != current.Version)
        {
            throw AppException.Conflict("conflict.booking.changed");
        }
        var next = new Settings(request.SlotMinutes ?? current.SlotMinutes, request.CheckInBeforeMinutes ?? current.CheckInBeforeMinutes,
            request.CheckInGraceMinutes ?? current.CheckInGraceMinutes, request.ApprovalHoldHours ?? current.ApprovalHoldHours, request.NoShowLimit ?? current.NoShowLimit,
            request.NoShowWindowDays ?? current.NoShowWindowDays, request.NoShowRestrict ?? current.NoShowRestrict, request.NoShowRestrictDays ?? current.NoShowRestrictDays,
            request.NoShowAdvanceDays ?? current.NoShowAdvanceDays, request.ShowNames ?? current.ShowNames, current.Version + 1);
        var errors = new ValidationErrors();
        if (next.SlotMinutes is not (5 or 10 or 15 or 30 or 60)) errors.Add("slotMinutes", "validation.booking.slot_minutes");
        if (next.CheckInBeforeMinutes is < 0 or > 120) errors.Add("checkInBeforeMinutes", "validation.booking.range");
        if (next.CheckInGraceMinutes is < 5 or > 120) errors.Add("checkInGraceMinutes", "validation.booking.range");
        if (next.ApprovalHoldHours is < 1 or > 72) errors.Add("approvalHoldHours", "validation.booking.range");
        if (next.NoShowLimit is < 1 or > 20) errors.Add("noShowLimit", "validation.booking.range");
        if (next.NoShowWindowDays is < 7 or > 90) errors.Add("noShowWindowDays", "validation.booking.range");
        if (next.NoShowRestrictDays is < 1 or > 90) errors.Add("noShowRestrictDays", "validation.booking.range");
        if (next.NoShowAdvanceDays is < 0 or > 14) errors.Add("noShowAdvanceDays", "validation.booking.range");
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            insert into booking.setting (tenant_id, slot_minutes, check_in_before_minutes, check_in_grace_minutes, approval_hold_hours, no_show_limit, no_show_window_days,
              no_show_restrict, no_show_restrict_days, no_show_advance_days, show_names, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @SlotMinutes, @CheckInBeforeMinutes, @CheckInGraceMinutes, @ApprovalHoldHours, @NoShowLimit, @NoShowWindowDays,
              @NoShowRestrict, @NoShowRestrictDays, @NoShowAdvanceDays, @ShowNames, @Version, @actor, @now)
            on conflict (tenant_id) do update set slot_minutes = excluded.slot_minutes, check_in_before_minutes = excluded.check_in_before_minutes,
              check_in_grace_minutes = excluded.check_in_grace_minutes, approval_hold_hours = excluded.approval_hold_hours, no_show_limit = excluded.no_show_limit,
              no_show_window_days = excluded.no_show_window_days, no_show_restrict = excluded.no_show_restrict, no_show_restrict_days = excluded.no_show_restrict_days,
              no_show_advance_days = excluded.no_show_advance_days, show_names = excluded.show_names, version = excluded.version, updated_by = excluded.updated_by,
              updated_at = excluded.updated_at
            """, new
            {
                next.SlotMinutes, next.CheckInBeforeMinutes, next.CheckInGraceMinutes, next.ApprovalHoldHours, next.NoShowLimit, next.NoShowWindowDays, next.NoShowRestrict,
                next.NoShowRestrictDays, next.NoShowAdvanceDays, next.ShowNames, next.Version, actor = context.RequireUser(), now = clock.UtcNow,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingSettingsChanged, "booking_settings", null, Changes: next.View), ct);
        return next.View;
    }

    /// <summary>The rule a resource follows now.</summary>
    public async Task<RuleView> ResourceRuleAsync(Guid resourceId, CancellationToken ct)
    {
        var resource = await facilities.ResourceRowAsync(resourceId, ct) ?? throw AppException.NotFound();
        await facilities.ManagedSiteAsync(resource.SiteId, ct);
        return (await ForAsync(resource, ct)).View;
    }

    /// <summary>The rule for a kind at a site, or in the tenant when <paramref name="siteId"/> is null.</summary>
    public async Task<RuleView> KindRuleAsync(string kind, Guid? siteId, CancellationToken ct)
    {
        await KindTargetAsync(kind, siteId, ct);
        var rows = await session.QueryAsync($"select {Columns} from booking.rule where resource_id is null and kind = @kind and (site_id = @siteId or site_id is null)",
            new { kind, siteId }, Read, ct);
        return (rows.FirstOrDefault(r => r.SiteId is not null)?.Rule ?? rows.FirstOrDefault(r => r.SiteId is null)?.Rule ?? Default(kind)).View;
    }

    private async Task<Guid?> KindTargetAsync(string kind, Guid? siteId, CancellationToken ct)
    {
        if (!ResourceKinds.All.Contains(kind))
        {
            throw AppException.NotFound();
        }
        if (siteId is { } site)
        {
            return (await facilities.ManagedSiteAsync(site, ct)).OrgUnitId;
        }
        await facilities.RequireAnyAsync(ct);
        await authz.DemandAsync(Permissions.BookingManage, AuthzTarget.Tenant, ct);
        return null;
    }

    public async Task<RuleView> SetResourceRuleAsync(Guid resourceId, RuleRequest request, CancellationToken ct)
    {
        var resource = await facilities.ResourceRowAsync(resourceId, ct) ?? throw AppException.NotFound();
        var site = await facilities.ManagedSiteAsync(resource.SiteId, ct);
        var current = await ForAsync(resource, ct);
        var rule = Validate(request, current);
        await UpsertAsync("resource_id = @resourceId::uuid", resourceId, null, null, rule, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingRulesChanged, Facilities.ResourceType, resourceId.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { target = "resource", version = rule.Version, rule = rule.View }), ct);
        return (await ForAsync(resource, ct)).View;
    }

    public async Task<RuleView> SetKindRuleAsync(string kind, Guid? siteId, RuleRequest request, CancellationToken ct)
    {
        var unit = await KindTargetAsync(kind, siteId, ct);
        var current = await KindRuleAsync(kind, siteId, ct);
        var rule = Validate(request, new Rule(current.Source, current.UnitIds.ToArray(), current.AdvanceDays, current.MinMinutes, current.MaxMinutes, current.WorkingHours,
            current.BufferMinutes, current.MaxActive, current.Overnight, current.SeriesDays, current.Version));
        await UpsertAsync("resource_id is null and kind = @kind::text and site_id is not distinct from @siteId::uuid", null, siteId, kind, rule, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingRulesChanged, siteId is null ? "booking_kind" : Facilities.SiteType, siteId?.ToString() ?? kind,
            TargetOrgUnitId: unit, Changes: new { target = siteId is null ? "tenant" : "site", kind, version = rule.Version, rule = rule.View }), ct);
        return await KindRuleAsync(kind, siteId, ct);
    }

    private static Rule Validate(RuleRequest request, Rule current)
    {
        var rule = new Rule(current.Source, (request.UnitIds ?? current.UnitIds).Distinct().ToArray(), request.AdvanceDays ?? current.AdvanceDays,
            request.MinMinutes ?? current.MinMinutes, request.MaxMinutes ?? current.MaxMinutes, request.WorkingHours ?? current.WorkingHours,
            request.BufferMinutes ?? current.BufferMinutes, request.MaxActive ?? current.MaxActive, request.Overnight ?? current.Overnight,
            request.SeriesDays ?? current.SeriesDays, current.Version + 1);
        var errors = new ValidationErrors();
        if (rule.UnitIds.Length > 50) errors.Add("unitIds", "validation.booking.units");
        if (rule.AdvanceDays is < 0 or > 365) errors.Add("advanceDays", "validation.booking.range");
        if (rule.MinMinutes is < 5 or > 1440) errors.Add("minMinutes", "validation.booking.range");
        if (rule.MaxMinutes is < 5 or > 20160 || rule.MaxMinutes < rule.MinMinutes) errors.Add("maxMinutes", "validation.booking.range");
        if (rule.BufferMinutes is < 0 or > 240) errors.Add("bufferMinutes", "validation.booking.range");
        if (rule.MaxActive is < 1 or > 100) errors.Add("maxActive", "validation.booking.range");
        if (rule.SeriesDays is < 1 or > 365) errors.Add("seriesDays", "validation.booking.range");
        errors.ThrowIfAny();
        return rule;
    }

    private async Task UpsertAsync(string where, Guid? resourceId, Guid? siteId, string? kind, Rule rule, CancellationToken ct)
    {
        var args = new
        {
            id = Ids.New(), resourceId, siteId, kind, units = rule.UnitIds, rule.AdvanceDays, rule.MinMinutes, rule.MaxMinutes, rule.WorkingHours, rule.BufferMinutes,
            rule.MaxActive, rule.Overnight, rule.SeriesDays, rule.Version, actor = context.RequireUser(), now = clock.UtcNow,
        };
        var updated = await session.ExecAsync(
            $"""
            update booking.rule set unit_ids = @units, advance_days = @AdvanceDays, min_minutes = @MinMinutes, max_minutes = @MaxMinutes, working_hours = @WorkingHours,
              buffer_minutes = @BufferMinutes, max_active = @MaxActive, overnight = @Overnight, series_days = @SeriesDays, version = version + 1, updated_by = @actor,
              updated_at = @now
            where {where}
            """, args, ct);
        if (updated == 0)
        {
            await session.ExecAsync(
                """
                insert into booking.rule (tenant_id, id, resource_id, site_id, kind, unit_ids, advance_days, min_minutes, max_minutes, working_hours, buffer_minutes,
                  max_active, overnight, series_days, version, updated_by, updated_at)
                values (platform.current_tenant_id(), @id, @resourceId, @siteId, @kind, @units, @AdvanceDays, @MinMinutes, @MaxMinutes, @WorkingHours, @BufferMinutes,
                  @MaxActive, @Overnight, @SeriesDays, 1, @actor, @now)
                """, args, ct);
        }
    }
}
