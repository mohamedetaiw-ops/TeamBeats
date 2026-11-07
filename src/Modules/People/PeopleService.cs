using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.People;

public sealed record UnitRef(Guid Id, IReadOnlyDictionary<string, string> Names);

public sealed record LocationRef(string Key, string Label, IReadOnlyDictionary<string, string> Names);

public sealed record FieldView(string Key, string Value, IReadOnlyDictionary<string, string>? Names);

public sealed record PersonCard(Guid Id, string DisplayName, IReadOnlyDictionary<string, string> Names, string? JobTitle, UnitRef? Unit, LocationRef? Location,
    IReadOnlyList<FieldView>? Fields = null, int? Level = null);

public sealed record PersonView(Guid Id, string DisplayName, IReadOnlyDictionary<string, string> Names, UnitRef? Unit, IReadOnlyList<UnitRef> Units,
    LocationRef? Location, IReadOnlyList<FieldView> Fields, PersonCard? Manager, int DirectReports, bool IsSelf, bool CanSetManager, bool CanEditAdminFields);

public sealed record SearchPage(IReadOnlyList<PersonCard> Items, int? Next);

public sealed record OrgChart(PersonCard Person, IReadOnlyList<PersonCard> Chain, IReadOnlyList<PersonCard> Peers, int PeerCount,
    IReadOnlyList<PersonCard> Reports, int ReportCount);

public sealed record TeamPage(IReadOnlyList<PersonCard> Items, int Total, int? Next);

public sealed record OwnField(string Key, string? Value, string Source, string Visibility, bool Editable, IReadOnlyDictionary<string, string>? Names);

public sealed record OwnProfile(PersonView Profile, IReadOnlyList<OwnField> Fields, bool NewcomerOptOut, bool AllowNewcomerOptOut);

public sealed record OwnProfileRequest(IReadOnlyDictionary<string, string?>? Values, bool? NewcomerOptOut);

public sealed record AdminFieldsRequest(IReadOnlyDictionary<string, string?>? Values, IReadOnlyDictionary<string, string>? Names);

public sealed record ManagerRequest(Guid? ManagerId);

public sealed record PolicyFieldView(string Key, string Source, string Visibility, IReadOnlyList<string> Sources, bool Custom, IReadOnlyDictionary<string, string> Names);

public sealed record PolicyView(IReadOnlyList<PolicyFieldView> Fields, int NewcomerDays, bool AllowNewcomerOptOut, string LocationAttribute,
    IReadOnlyList<string> LocationAttributes, bool CustomFieldsAvailable, DateTimeOffset? DirectorySyncedAt, int Version)
{
    public string ETag => PeopleService.ETag(Version);
}

public sealed record PolicyFieldInput(string Key, string? Source, string? Visibility, IReadOnlyDictionary<string, string>? Names);

public sealed record PolicyRequest(IReadOnlyList<PolicyFieldInput>? Fields, int? NewcomerDays, bool? AllowNewcomerOptOut, string? LocationAttribute);

public sealed record LocationView(string Key, string Label, IReadOnlyDictionary<string, string> Names, int People);

public sealed record LocationRequest(IReadOnlyDictionary<string, string>? Names);

/// <summary>
/// People directory, profiles, org chart and My team (spec 010 R1). Every answer goes through the viewer's visibility
/// (FR-003): a field the viewer may not see is left out of the response and is never used to match, filter or sort.
/// </summary>
public sealed class PeopleService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IOrganizationHierarchy hierarchy,
    ProfileStore store,
    ReportingLine lines,
    VisibilityResolver visibility)
{
    public const int MaxPageSize = 50;
    public const int MaxChain = 15;
    public static readonly IReadOnlyList<string> NameLocales = ["ar", "en"];

    public static string ETag(int version) => $"\"v{version}\"";

    private Dictionary<string, LocationRef>? _locations;
    private readonly Dictionary<Guid, UnitRef> _units = [];

    private async Task<(Viewer Viewer, IReadOnlyList<FieldPolicy> Policies)> BeginAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.PeopleView, ct))
        {
            throw AppException.Denied();
        }
        await store.EnsureFreshAsync(ct);
        return (await visibility.ViewerAsync(context.RequireUser(), ct), await store.PoliciesAsync(ct));
    }

    // Search (US1, FR-006, FR-017)

    public async Task<SearchPage> SearchAsync(string? q, Guid? unitId, string? location, int? offset, int? limit, CancellationToken ct)
    {
        var (viewer, policies) = await BeginAsync(ct);
        var tokens = SearchText.Normalize(q).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5).ToArray();
        var everyone = policies.Where(p => p.Visibility == FieldVisibility.Everyone).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        Guid[]? units = null;
        if (unitId is { } u)
        {
            units = (await hierarchy.GetSubtreesAsync([u], ct)).ToArray();
        }
        var size = Math.Clamp(limit ?? 20, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);
        static string Like(string s) => "%" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var rows = await store.QueryAsync(
            """
            active and membership_type <> 'guest'
            and (@units::uuid[] is null or unit_ids && @units)
            and (@location::text is null or location_key = @location)
            and not exists (
              select 1 from unnest(@contains::text[], @skeletons::text[], @digits::text[]) as t(c, s, d)
              where not (
                search_name like t.c or search_local like t.c
                or (@email and lower(email) like t.c)
                or (@title and coalesce(search_fields->>'jobTitle', '') like t.c)
                or (@department and coalesce(search_fields->>'department', '') like t.c)
                or (t.s <> '' and (search_skeleton like t.s or search_local like t.s))
                or (t.d <> '' and ((@work and coalesce(search_fields->>'workPhone', '') like t.d) or (@mobile and coalesce(search_fields->>'mobilePhone', '') like t.d)))
              )
            )
            order by case when search_name like @first then 0 when search_name like @word then 1 else 2 end, display_name, user_id
            offset @skip limit @take
            """,
            new
            {
                units,
                location = SearchText.LocationKey(location),
                contains = tokens.Select(Like).ToArray(),
                skeletons = tokens.Select(t => t.Length >= 3 && SearchText.Skeleton(t) is { Length: >= 2 } s ? "% " + s + "%" : string.Empty).ToArray(),
                digits = tokens.Select(t => SearchText.Digits(t) is { Length: >= 2 } d ? Like(d) : string.Empty).ToArray(),
                email = everyone.Contains(ProfileFields.Email),
                title = everyone.Contains(ProfileFields.JobTitle),
                department = everyone.Contains(ProfileFields.Department),
                work = everyone.Contains(ProfileFields.WorkPhone),
                mobile = everyone.Contains(ProfileFields.MobilePhone),
                first = tokens.Length > 0 ? tokens[0] + "%" : "%",
                word = tokens.Length > 0 ? "% " + tokens[0] + "%" : "%",
                skip,
                take = size + 1,
            }, ct);
        var cards = await CardsAsync(viewer, policies, rows.Take(size), ct);
        return new SearchPage(cards, rows.Count > size ? skip + size : null);
    }

    // Profile (US1 scenario 4, US3)

    public async Task<PersonView> GetAsync(Guid userId, CancellationToken ct)
    {
        var (viewer, policies) = await BeginAsync(ct);
        var row = await store.GetAsync(userId, ct) ?? throw AppException.NotFound();
        if (!row.Active)
        {
            throw new AppException(ProblemCodes.Gone, 410, "people.no_longer_available");
        }
        return await ViewAsync(viewer, policies, row, ct);
    }

    private async Task<PersonView> ViewAsync(Viewer viewer, IReadOnlyList<FieldPolicy> policies, ProfileRow row, CancellationToken ct)
    {
        var fields = await FieldsAsync(viewer, policies, row, ct);
        var managerId = await lines.ManagerOfAsync(row.UserId, ct);
        PersonCard? manager = null;
        if (managerId is { } m && await store.GetAsync(m, ct) is { Active: true } managerRow)
        {
            manager = (await CardsAsync(viewer, policies, [managerRow], ct))[0];
        }
        var reports = (await store.GetManyAsync(await lines.DirectReportsAsync(row.UserId, ct), ct)).Count(r => r.Active);
        var unitRefs = await UnitsAsync(row.UnitIds, ct);
        var canSetManager = (await authz.CheckAsync(Permissions.PeopleManage, AuthzTarget.Tenant, ct)).Allowed;
        return new PersonView(row.UserId, row.DisplayName, row.Names, row.PrimaryUnitId is { } p ? unitRefs.FirstOrDefault(u => u.Id == p) : null, unitRefs,
            await LocationAsync(row.LocationKey, ct), fields, manager, reports, row.UserId == viewer.Id, canSetManager,
            CanCorrect(viewer, row));
    }

    private static bool CanCorrect(Viewer viewer, ProfileRow row) => viewer.Manage.Covers(row.PrimaryUnitId) || viewer.Hr.Covers(row.PrimaryUnitId);

    private async Task<IReadOnlyList<FieldView>> FieldsAsync(Viewer viewer, IReadOnlyList<FieldPolicy> policies, ProfileRow row, CancellationToken ct)
    {
        var byKey = policies.ToDictionary(p => p.Key, StringComparer.Ordinal);
        return (await visibility.VisibleFieldsAsync(viewer, row, policies, ct))
            .Select(f => new FieldView(f.Key, f.Value, byKey[f.Key].Custom ? byKey[f.Key].Names : null)).ToList();
    }

    private async Task<IReadOnlyList<PersonCard>> CardsAsync(Viewer viewer, IReadOnlyList<FieldPolicy> policies, IEnumerable<ProfileRow> rows,
        CancellationToken ct, bool withFields = false, IReadOnlyDictionary<Guid, int>? levels = null)
    {
        var list = rows.ToList();
        await UnitsAsync(list.Where(r => r.PrimaryUnitId is not null).Select(r => r.PrimaryUnitId!.Value), ct);
        var title = policies.First(p => p.Key == ProfileFields.JobTitle);
        var cards = new List<PersonCard>();
        foreach (var row in list)
        {
            var fields = withFields ? await FieldsAsync(viewer, policies, row, ct) : null;
            var root = title.Visibility == FieldVisibility.Subtree ? await visibility.BranchRootAsync(row.PrimaryUnitId, ct) : null;
            var jobTitle = viewer.Sees(row, title, root) ? row.Value(title) : null;
            cards.Add(new PersonCard(row.UserId, row.DisplayName, row.Names, jobTitle,
                row.PrimaryUnitId is { } u ? _units.GetValueOrDefault(u) : null, await LocationAsync(row.LocationKey, ct), fields,
                levels is not null && levels.TryGetValue(row.UserId, out var level) ? level : null));
        }
        return cards;
    }

    private async Task<IReadOnlyList<UnitRef>> UnitsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var missing = wanted.Where(id => !_units.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            foreach (var (id, names) in await hierarchy.GetNamesAsync(missing, ct))
            {
                _units[id] = new UnitRef(id, names);
            }
        }
        return wanted.Where(_units.ContainsKey).Select(id => _units[id]).ToList();
    }

    private async Task<LocationRef?> LocationAsync(string? key, CancellationToken ct)
    {
        if (key is null)
        {
            return null;
        }
        _locations ??= (await session.QueryAsync("select key, label, names::text from people.location", null,
            r => new LocationRef(r.GetString(0), r.GetString(1), System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2))!), ct))
            .ToDictionary(l => l.Key, StringComparer.Ordinal);
        return _locations.GetValueOrDefault(key);
    }

    // Org chart and My team (US2 scenarios 3 and 4, FR-007, FR-008)

    public async Task<OrgChart> OrgChartAsync(Guid userId, CancellationToken ct)
    {
        var (viewer, policies) = await BeginAsync(ct);
        var row = await store.GetAsync(userId, ct) ?? throw AppException.NotFound();
        if (!row.Active)
        {
            throw new AppException(ProblemCodes.Gone, 410, "people.no_longer_available");
        }
        var chainIds = await lines.ChainAsync(userId, MaxChain, ct);
        var chainRows = (await store.GetManyAsync(chainIds, ct)).Where(r => r.Active).ToDictionary(r => r.UserId);
        var chain = await CardsAsync(viewer, policies, chainIds.Where(chainRows.ContainsKey).Select(id => chainRows[id]), ct);

        IReadOnlyList<ProfileRow> peers = [];
        if (chainIds.Count > 0)
        {
            peers = (await store.GetManyAsync(await lines.DirectReportsAsync(chainIds[0], ct), ct))
                .Where(r => r.Active && r.UserId != userId).OrderBy(r => r.DisplayName, StringComparer.CurrentCulture).ToList();
        }
        var reports = (await store.GetManyAsync(await lines.DirectReportsAsync(userId, ct), ct))
            .Where(r => r.Active).OrderBy(r => r.DisplayName, StringComparer.CurrentCulture).ToList();
        return new OrgChart((await CardsAsync(viewer, policies, [row], ct))[0], chain,
            await CardsAsync(viewer, policies, peers.Take(MaxPageSize), ct), peers.Count,
            await CardsAsync(viewer, policies, reports.Take(MaxPageSize), ct), reports.Count);
    }

    public async Task<TeamPage> TeamAsync(bool indirect, int? offset, int? limit, CancellationToken ct)
    {
        var (viewer, policies) = await BeginAsync(ct);
        var levels = await lines.ReportsWithLevelAsync(viewer.Id, ct);
        var wanted = levels.Where(l => indirect || l.Value == 1).ToDictionary(l => l.Key, l => l.Value);
        var rows = (await store.GetManyAsync(wanted.Keys, ct)).Where(r => r.Active)
            .OrderBy(r => wanted[r.UserId]).ThenBy(r => r.DisplayName, StringComparer.CurrentCulture).ThenBy(r => r.UserId).ToList();
        var size = Math.Clamp(limit ?? 50, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);
        var page = rows.Skip(skip).Take(size);
        return new TeamPage(await CardsAsync(viewer, policies, page, ct, withFields: true, levels: wanted), rows.Count,
            skip + size < rows.Count ? skip + size : null);
    }

    // Newcomers (US4 scenarios 1 and 3, FR-009)

    public async Task<IReadOnlyList<PersonCard>> NewcomersAsync(int? limit, CancellationToken ct)
    {
        var (viewer, policies) = await BeginAsync(ct);
        var settings = await store.SettingsAsync(ct);
        var rows = await store.QueryAsync(
            """
            active and membership_type <> 'guest' and not (newcomer_opt_out and @optOut)
            and coalesce(hire_date, first_seen) >= current_date - @days and coalesce(hire_date, first_seen) <= current_date
            order by coalesce(hire_date, first_seen) desc, display_name, user_id limit @n
            """, new { optOut = settings.AllowNewcomerOptOut, days = settings.NewcomerDays, n = Math.Clamp(limit ?? 12, 1, MaxPageSize) }, ct);
        return await CardsAsync(viewer, policies, rows, ct);
    }

    // Own profile (US3 scenarios 1 and 2)

    public async Task<OwnProfile> OwnAsync(CancellationToken ct)
    {
        var (viewer, policies) = await BeginAsync(ct);
        var row = await store.GetAsync(viewer.Id, ct) ?? throw AppException.NotFound();
        var settings = await store.SettingsAsync(ct);
        return new OwnProfile(await ViewAsync(viewer, policies, row, ct),
            policies.Select(p => new OwnField(p.Key, row.Value(p), p.Source, p.Visibility, p.EmployeeEditable, p.Custom ? p.Names : null)).ToList(),
            row.NewcomerOptOut, settings.AllowNewcomerOptOut);
    }

    public async Task<OwnProfile> UpdateOwnAsync(OwnProfileRequest request, CancellationToken ct)
    {
        var (viewer, policies) = await BeginAsync(ct);
        var row = await store.GetAsync(viewer.Id, ct) ?? throw AppException.NotFound();
        var settings = await store.SettingsAsync(ct);
        var values = ApplyValues(request.Values, row.OwnValues, policies, FieldSources.Person);
        var optOut = request.NewcomerOptOut is { } o && settings.AllowNewcomerOptOut ? o : row.NewcomerOptOut;
        var changed = Changed(row.OwnValues, values);
        if (optOut != row.NewcomerOptOut)
        {
            changed.Add("newcomerOptOut");
        }
        if (changed.Count > 0)
        {
            await SaveValuesAsync(row with { OwnValues = values, NewcomerOptOut = optOut }, policies, "own_values", values, optOut, changed, ct);
        }
        return await OwnAsync(ct);
    }

    /// <summary>Corrections to administrator-managed fields and localized names (section 3: HR officers and organization administrators).</summary>
    public async Task<PersonView> UpdateAdminFieldsAsync(Guid userId, AdminFieldsRequest request, CancellationToken ct)
    {
        var (viewer, policies) = await BeginAsync(ct);
        var row = await store.GetAsync(userId, ct) ?? throw AppException.NotFound();
        if (!CanCorrect(viewer, row))
        {
            throw AppException.Denied();
        }
        var values = ApplyValues(request.Values, row.AdminValues, policies, FieldSources.Admin);
        var changed = Changed(row.AdminValues, values);
        var names = row.Names;
        if (request.Names is { } requested)
        {
            var errors = new ValidationErrors();
            var cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (locale, name) in requested)
            {
                if (!NameLocales.Contains(locale) || name.Length > ProfileFields.MaxValueLength)
                {
                    errors.Add($"names.{locale}", "validation.people.name");
                }
                else if (!string.IsNullOrWhiteSpace(name))
                {
                    cleaned[locale] = name.Trim();
                }
            }
            errors.ThrowIfAny();
            if (!cleaned.OrderBy(k => k.Key).SequenceEqual(names.OrderBy(k => k.Key)))
            {
                names = cleaned;
                changed.Add("names");
            }
        }
        if (changed.Count > 0)
        {
            var updated = row with { AdminValues = values, Names = names };
            await session.ExecAsync(
                "update people.profile set names = @names, search_local = @local where user_id = @u",
                new { names = Jsonb.From(names), local = SearchLocal(names), u = userId }, ct);
            await SaveValuesAsync(updated, policies, "admin_values", values, row.NewcomerOptOut, changed, ct);
        }
        return await ViewAsync(viewer, policies, (await store.GetAsync(userId, ct))!, ct);
    }

    private static string SearchLocal(IReadOnlyDictionary<string, string> names) =>
        string.Join(' ', names.Values.Select(SearchText.Normalize)) + " " + string.Join(' ', names.Values.Select(SearchText.Skeletons));

    private static Dictionary<string, string> ApplyValues(IReadOnlyDictionary<string, string?>? requested, IReadOnlyDictionary<string, string> current,
        IReadOnlyList<FieldPolicy> policies, string source)
    {
        var values = new Dictionary<string, string>(current, StringComparer.Ordinal);
        if (requested is null)
        {
            return values;
        }
        var byKey = policies.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var errors = new ValidationErrors();
        foreach (var (key, raw) in requested)
        {
            if (!byKey.TryGetValue(key, out var policy))
            {
                errors.Add($"values.{key}", "validation.people.field_unknown");
                continue;
            }
            if (policy.Source != source)
            {
                // Directory-owned values are changed at the source (US3 scenario 2); the manager is never editable here.
                errors.Add($"values.{key}", "validation.people.read_only");
                continue;
            }
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                values.Remove(key);
                continue;
            }
            if (value.Length > ProfileFields.MaxValueLength)
            {
                errors.Add($"values.{key}", "validation.people.too_long");
            }
            else if (key == ProfileFields.TimeZone && !TimeZoneInfo.TryFindSystemTimeZoneById(value, out _))
            {
                errors.Add($"values.{key}", "validation.people.time_zone");
            }
            else if (ProfileFields.ByKey.TryGetValue(key, out var def) && def.IsDate && !DateOnly.TryParseExact(value, "yyyy-MM-dd", out _))
            {
                errors.Add($"values.{key}", "validation.people.date");
            }
            else
            {
                values[key] = value;
            }
        }
        errors.ThrowIfAny();
        return values;
    }

    private static List<string> Changed(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        before.Keys.Union(after.Keys).Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k)).Order(StringComparer.Ordinal).ToList();

    private async Task SaveValuesAsync(ProfileRow updated, IReadOnlyList<FieldPolicy> policies, string column, IReadOnlyDictionary<string, string> values,
        bool optOut, IReadOnlyList<string> changed, CancellationToken ct)
    {
        await session.ExecAsync(
            $"update people.profile set {column} = @values, newcomer_opt_out = @optOut, search_fields = @search, version = version + 1, updated_at = now() where user_id = @u",
            new { values = Jsonb.From(values), optOut, search = Jsonb.From(ProfileStore.SearchFields(updated, policies)), u = updated.UserId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PeopleProfileUpdated, "person", updated.UserId.ToString(), TargetOrgUnitId: updated.PrimaryUnitId,
            Changes: new { fields = changed }), ct);
        await outbox.EnqueueAsync(new ProfileUpdated(updated.UserId, changed), ct);
    }

    // Manual manager (US2 scenario 6)

    public async Task<PersonView> SetManagerAsync(Guid userId, ManagerRequest request, CancellationToken ct)
    {
        var (viewer, policies) = await BeginAsync(ct);
        var row = await store.GetAsync(userId, ct) ?? throw AppException.NotFound();
        await authz.DemandAsync(Permissions.PeopleManage, AuthzTarget.Tenant, ct);
        if ((await store.SettingsAsync(ct)).DirectorySyncedAt is not null)
        {
            throw AppException.Conflict("conflict.people.manager_from_directory");
        }
        if (request.ManagerId is { } m)
        {
            if (m == userId || (await lines.AllReportsAsync(userId, ct)).Contains(m))
            {
                throw AppException.Validation("managerId", "validation.people.manager_cycle");
            }
            if (await store.GetAsync(m, ct) is not { Active: true })
            {
                throw AppException.Validation("managerId", "validation.people.manager_unknown");
            }
        }
        var previous = await lines.ManagerOfAsync(userId, ct);
        if (await lines.SetAsync(userId, request.ManagerId, "manual", ct))
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.PeopleManagerSetManually, "person", userId.ToString(), TargetOrgUnitId: row.PrimaryUnitId,
                Changes: new { previousManager = previous, manager = request.ManagerId }), ct);
        }
        return await ViewAsync(viewer, policies, row, ct);
    }

    // Policy (US3 scenarios 1, 4 and 5, FR-002, FR-003, FR-016)

    public async Task<PolicyView> PolicyAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.PeopleManage, AuthzTarget.Tenant, ct);
        var settings = await store.SettingsAsync(ct);
        var policies = await store.PoliciesAsync(ct);
        return new PolicyView(
            policies.Select(p => new PolicyFieldView(p.Key, p.Source, p.Visibility,
                ProfileFields.ByKey.TryGetValue(p.Key, out var def) ? def.Sources : ProfileFields.CustomSources, p.Custom, p.Names)).ToList(),
            settings.NewcomerDays, settings.AllowNewcomerOptOut, settings.LocationAttribute, DirectoryProfiles.LocationAttributes,
            await entitlements.IsEnabledAsync(Capabilities.PeopleCustomFields, ct), settings.DirectorySyncedAt, settings.Version);
    }

    public async Task<PolicyView> UpdatePolicyAsync(string? ifMatch, PolicyRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.PeopleManage, AuthzTarget.Tenant, ct);
        var settings = await store.SettingsAsync(ct);
        if (ifMatch is not null && ifMatch != ETag(settings.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.people.policy_changed", null,
                new Dictionary<string, object?> { ["currentVersion"] = settings.Version });
        }
        var current = await store.PoliciesAsync(ct);
        var errors = new ValidationErrors();
        var next = new List<FieldPolicy>();
        var inputs = request.Fields ?? current.Select(p => new PolicyFieldInput(p.Key, p.Source, p.Visibility, p.Names)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var customCount = 0;
        foreach (var (input, index) in inputs.Select((f, i) => (f, i)))
        {
            var at = $"fields[{index}]";
            if (!seen.Add(input.Key))
            {
                errors.Add($"{at}.key", "validation.people.field_unknown");
                continue;
            }
            var builtIn = ProfileFields.ByKey.TryGetValue(input.Key, out var def);
            var custom = !builtIn && ProfileFields.IsCustom(input.Key) && input.Key.Length is > 7 and <= 47
                && input.Key[ProfileFields.CustomPrefix.Length..].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
            if (!builtIn && !custom)
            {
                errors.Add($"{at}.key", "validation.people.field_unknown");
                continue;
            }
            var sources = builtIn ? def!.Sources : ProfileFields.CustomSources;
            var source = input.Source ?? (builtIn ? def!.DefaultSource : FieldSources.Person);
            if (!sources.Contains(source))
            {
                errors.Add($"{at}.source", "validation.people.source");
            }
            var visible = input.Visibility ?? (builtIn ? def!.DefaultVisibility : FieldVisibility.Everyone);
            if (!FieldVisibility.All.Contains(visible))
            {
                errors.Add($"{at}.visibility", "validation.people.visibility");
            }
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (custom)
            {
                customCount++;
                foreach (var (locale, name) in input.Names ?? new Dictionary<string, string>())
                {
                    if (NameLocales.Contains(locale) && !string.IsNullOrWhiteSpace(name) && name.Length <= 80)
                    {
                        names[locale] = name.Trim();
                    }
                }
                if (names.Count == 0)
                {
                    errors.Add($"{at}.names", "validation.people.field_name");
                }
            }
            next.Add(new FieldPolicy(input.Key, source, visible, custom, names, custom ? 100 + index : ProfileFields.PositionOf(input.Key)));
        }
        foreach (var missing in ProfileFields.BuiltIn.Where(d => !seen.Contains(d.Key)))
        {
            next.Add(current.First(p => p.Key == missing.Key));
        }
        if (customCount > ProfileFields.MaxCustomFields)
        {
            errors.Add("fields", "validation.people.too_many_fields");
        }
        var days = request.NewcomerDays ?? settings.NewcomerDays;
        if (days is < 7 or > 90)
        {
            errors.Add("newcomerDays", "validation.people.newcomer_days");
        }
        var attribute = request.LocationAttribute ?? settings.LocationAttribute;
        if (!DirectoryProfiles.LocationAttributes.Contains(attribute))
        {
            errors.Add("locationAttribute", "validation.people.location_attribute");
        }
        errors.ThrowIfAny();
        if (next.Count(p => p.Custom) > current.Count(p => p.Custom))
        {
            await entitlements.DemandAsync(Capabilities.PeopleCustomFields, ct);
        }

        var before = current.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var sourceChanged = false;
        foreach (var policy in next)
        {
            var old = before.GetValueOrDefault(policy.Key);
            if (old?.Visibility != policy.Visibility)
            {
                await audit.WriteAsync(new AuditEntry(AuditActions.PeopleVisibilityChanged, "profile_field", policy.Key,
                    Changes: new { field = policy.Key, from = old?.Visibility, to = policy.Visibility }), ct);
            }
            if (old?.Source != policy.Source)
            {
                sourceChanged = true;
                await audit.WriteAsync(new AuditEntry(AuditActions.PeopleFieldSourceChanged, "profile_field", policy.Key,
                    Changes: new { field = policy.Key, from = old?.Source, to = policy.Source }), ct);
            }
        }
        foreach (var removed in current.Where(p => p.Custom && next.All(n => n.Key != p.Key)))
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.PeopleFieldSourceChanged, "profile_field", removed.Key,
                Changes: new { field = removed.Key, from = removed.Source, to = (string?)null }), ct);
        }

        await session.ExecAsync("delete from people.field_policy", null, ct);
        await session.ExecAsync(
            """
            insert into people.field_policy (tenant_id, field_key, source, visibility, is_custom, names, position)
            select platform.current_tenant_id(), r.key, r.source, r.visibility, r.custom, coalesce(r.names, '{}'::jsonb), r.position
            from jsonb_to_recordset(@rows) as r(key text, source text, visibility text, custom boolean, names jsonb, position int)
            """, new { rows = Jsonb.From(next.Select(p => new { key = p.Key, source = p.Source, visibility = p.Visibility, custom = p.Custom, names = p.Names, position = p.Position })) }, ct);
        if (days != settings.NewcomerDays || request.AllowNewcomerOptOut is { } a && a != settings.AllowNewcomerOptOut || attribute != settings.LocationAttribute)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.PeopleSettingsChanged, "people_settings", null,
                Changes: new { newcomerDays = days, allowNewcomerOptOut = request.AllowNewcomerOptOut ?? settings.AllowNewcomerOptOut, locationAttribute = attribute }), ct);
        }
        await session.ExecAsync(
            "update people.settings set newcomer_days = @days, allow_newcomer_opt_out = @optOut, location_attribute = @attribute, version = version + 1, updated_at = now()",
            new { days, optOut = request.AllowNewcomerOptOut ?? settings.AllowNewcomerOptOut, attribute }, ct);
        store.ForgetPolicies();
        if (sourceChanged)
        {
            await store.RebuildSearchFieldsAsync(ct);
        }
        return await PolicyAsync(ct);
    }

    // Locations (US5, FR-011)

    public async Task<IReadOnlyList<LocationView>> LocationsAsync(CancellationToken ct)
    {
        await BeginAsync(ct);
        return await session.QueryAsync(
            """
            select l.key, l.label, l.names::text, (select count(*)::int from people.profile p where p.location_key = l.key and p.active)
            from people.location l order by l.label, l.key
            """, null, r => new LocationView(r.GetString(0), r.GetString(1), System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2))!, r.GetInt32(3)), ct);
    }

    public async Task<LocationView> RenameLocationAsync(string key, LocationRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.PeopleManage, AuthzTarget.Tenant, ct);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new ValidationErrors();
        foreach (var (locale, name) in request.Names ?? new Dictionary<string, string>())
        {
            if (!NameLocales.Contains(locale) || name.Length > 80)
            {
                errors.Add($"names.{locale}", "validation.people.location_name");
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                names[locale] = name.Trim();
            }
        }
        errors.ThrowIfAny();
        var updated = await session.ExecAsync("update people.location set names = @names where key = @key", new { names = Jsonb.From(names), key }, ct);
        if (updated == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PeopleSettingsChanged, "location", key, Changes: new { location = key, locales = names.Keys.Order().ToArray() }), ct);
        return (await LocationsAsync(ct)).First(l => l.Key == key);
    }
}
