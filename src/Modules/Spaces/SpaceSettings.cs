using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

/// <summary>The tenant's space rules (FR-017). An empty unit list lets everyone with <c>spaces.create</c> create spaces.</summary>
public sealed record SpaceRules(IReadOnlyList<Guid> CreationUnits, bool GuestsAllowed, int GuestDays, int Version);

public sealed record SpaceRulesRequest(IReadOnlyList<Guid>? CreationUnits, bool? GuestsAllowed, int? GuestDays, int? Version);

/// <summary>A template a creator can pick: a built-in one by key, or the tenant's by ID.</summary>
public sealed record TemplateOption(string Key, string Name, string Kind, bool BuiltIn);

public sealed record TemplateRequest(string? Name, string? Kind, TemplateDefinition? Definition, int? Version);

public sealed record TemplateView(Guid Id, string Name, string Kind, TemplateDefinition Definition, string Status, int Version, DateTimeOffset UpdatedAt);

/// <summary>The tenant's space rules and templates (spec 029 FR-012, FR-017), managed with <c>spaces.manage</c>.</summary>
public sealed class SpaceSettings(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    ILocalizer text,
    IAuditWriter audit,
    IClock clock)
{
    public const int DefaultGuestDays = 90;
    public const int MaxCreationUnits = 50;
    public const int MaxTemplates = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<SpaceRules> CurrentAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select creation_units, guests_allowed, guest_days, version from spaces.setting", null,
            r => new SpaceRules(r.GetFieldValue<Guid[]>(0), r.GetBoolean(1), r.GetInt32(2), r.GetInt32(3)), ct)
        // Defaults: everyone with spaces.create may create, guests off until the tenant turns them on (spec 026), 90 days.
        ?? new SpaceRules([], false, DefaultGuestDays, 0);

    private async Task ManageAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SpacesProjects, ct);
        await authz.DemandAsync(Permissions.SpacesManage, AuthzTarget.Tenant, ct);
    }

    public async Task<SpaceRules> GetRulesAsync(CancellationToken ct)
    {
        await ManageAsync(ct);
        return await CurrentAsync(ct);
    }

    public async Task<SpaceRules> SetRulesAsync(SpaceRulesRequest request, CancellationToken ct)
    {
        await ManageAsync(ct);
        var current = await CurrentAsync(ct);
        if (request.Version != current.Version)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.spaces.settings_changed", null, new Dictionary<string, object?> { ["version"] = current.Version });
        }
        var errors = new ValidationErrors();
        var units = (request.CreationUnits ?? []).Distinct().ToArray();
        if (units.Length > MaxCreationUnits)
        {
            errors.Add("creationUnits", "validation.spaces.creation_units");
        }
        foreach (var unit in units)
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                errors.Add("creationUnits", "validation.spaces.unit");
                break;
            }
        }
        var guests = request.GuestsAllowed ?? false;
        if (guests && !await entitlements.IsEnabledAsync(Capabilities.SpacesGuests, ct))
        {
            errors.Add("guestsAllowed", "validation.spaces.guests_package");
        }
        var days = request.GuestDays ?? DefaultGuestDays;
        if (days is < 7 or > 365)
        {
            errors.Add("guestDays", "validation.spaces.guest_days");
        }
        errors.ThrowIfAny();
        var version = current.Version + 1;
        await session.ExecAsync(
            """
            insert into spaces.setting (tenant_id, creation_units, guests_allowed, guest_days, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @units, @guests, @days, @version, @user, @now)
            on conflict (tenant_id) do update set creation_units = excluded.creation_units, guests_allowed = excluded.guests_allowed,
              guest_days = excluded.guest_days, version = excluded.version, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { units, guests, days, version, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesSettingsChanged, "spaces_setting", null,
            Changes: new { version, creationUnits = units.Length, guestsAllowed = guests, guestDays = days }), ct);
        return new SpaceRules(units, guests, days, version);
    }

    // ---- Templates ----

    /// <summary>The kind and definition of a template by built-in key or tenant template ID; null when unknown or retired.</summary>
    internal async Task<(string? Kind, TemplateDefinition? Definition)> TemplateAsync(string key, string locale, CancellationToken ct)
    {
        if (SpaceTemplates.BuiltIn(key, text, locale) is { } builtIn)
        {
            return (key, builtIn);
        }
        if (!Guid.TryParse(key, out var id))
        {
            return (null, null);
        }
        var row = await session.QuerySingleOrDefaultAsync("select kind, definition::text from spaces.template where id = @id and status = 'active'", new { id },
            r => (Kind: r.GetString(0), Definition: JsonSerializer.Deserialize<TemplateDefinition>(r.GetString(1), Json)), ct);
        return row == default ? (null, null) : (row.Kind, row.Definition);
    }

    /// <summary>The templates a creator can pick from: the built-in project and team templates, then the tenant's active ones.</summary>
    public async Task<IReadOnlyList<TemplateOption>> OptionsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SpacesProjects, ct);
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var options = SpaceTemplates.BuiltInKeys.Select(k => new TemplateOption(k, text.Get($"spaces.template.{k}.name", locale), k, true)).ToList();
        options.AddRange(await session.QueryAsync("select id, name, kind from spaces.template where status = 'active' order by name, id", null,
            r => new TemplateOption(r.GetGuid(0).ToString(), r.GetString(1), r.GetString(2), false), ct));
        return options;
    }

    private static TemplateView ReadTemplate(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2),
        JsonSerializer.Deserialize<TemplateDefinition>(r.GetString(3), Json)!, r.GetString(4), r.GetInt32(5), r.GetFieldValue<DateTimeOffset>(6));

    private const string TemplateColumns = "id, name, kind, definition::text, status, version, updated_at";

    public async Task<IReadOnlyList<TemplateView>> TemplatesAsync(CancellationToken ct)
    {
        await ManageAsync(ct);
        return await session.QueryAsync($"select {TemplateColumns} from spaces.template order by status, name, id", null, ReadTemplate, ct);
    }

    private static (string Name, string Kind, TemplateDefinition Definition) Validate(TemplateRequest request)
    {
        var errors = new ValidationErrors();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > SpaceTemplates.MaxNameLength)
        {
            errors.Add("name", "validation.spaces.name");
        }
        if (request.Kind is not { } kind || !SpaceKinds.All.Contains(kind))
        {
            errors.Add("kind", "validation.spaces.kind");
            kind = SpaceKinds.Project;
        }
        var definition = SpaceTemplates.Validate(request.Definition, errors);
        errors.ThrowIfAny();
        return (name, kind, definition);
    }

    public async Task<TemplateView> CreateTemplateAsync(TemplateRequest request, CancellationToken ct)
    {
        await ManageAsync(ct);
        var (name, kind, definition) = Validate(request);
        if (await session.ScalarAsync<int>("select count(*)::int from spaces.template where status = 'active'", null, ct) >= MaxTemplates)
        {
            throw AppException.Validation("templates", "validation.spaces.template_count");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into spaces.template (tenant_id, id, name, kind, definition, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @name, @kind, @definition, @user, @now, @now)
            """, new { id, name, kind, definition = new Jsonb(JsonSerializer.Serialize(definition, Json)), user = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesTemplateChanged, "space_template", id.ToString(), Changes: new { name, kind, change = "created" }), ct);
        return (await session.QuerySingleOrDefaultAsync($"select {TemplateColumns} from spaces.template where id = @id", new { id }, ReadTemplate, ct))!;
    }

    /// <summary>Changes a template. Spaces made from it keep what they were given (FR-012).</summary>
    public async Task<TemplateView> UpdateTemplateAsync(Guid id, TemplateRequest request, bool retire, CancellationToken ct)
    {
        await ManageAsync(ct);
        var current = await session.QuerySingleOrDefaultAsync($"select {TemplateColumns} from spaces.template where id = @id", new { id }, ReadTemplate, ct)
            ?? throw AppException.NotFound();
        if (retire)
        {
            await session.ExecAsync("update spaces.template set status = 'retired', version = version + 1, updated_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.SpacesTemplateChanged, "space_template", id.ToString(), Changes: new { change = "retired" }), ct);
        }
        else
        {
            if (request.Version != current.Version)
            {
                throw new AppException(ProblemCodes.Conflict, 409, "conflict.spaces.template_changed", null, new Dictionary<string, object?> { ["version"] = current.Version });
            }
            var (name, kind, definition) = Validate(request);
            await session.ExecAsync(
                "update spaces.template set name = @name, kind = @kind, definition = @definition, version = version + 1, updated_at = @now where id = @id",
                new { id, name, kind, definition = new Jsonb(JsonSerializer.Serialize(definition, Json)), now = clock.UtcNow }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.SpacesTemplateChanged, "space_template", id.ToString(), Changes: new { name, kind, change = "updated" }), ct);
        }
        return (await session.QuerySingleOrDefaultAsync($"select {TemplateColumns} from spaces.template where id = @id", new { id }, ReadTemplate, ct))!;
    }
}
