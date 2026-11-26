using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.People;

public static class Proficiencies
{
    public const string Basic = "basic";
    public const string Good = "good";
    public const string Expert = "expert";

    public static readonly IReadOnlyList<string> All = [Basic, Good, Expert];

    public static int Rank(string value) => value switch { Expert => 3, Good => 2, _ => 1 };
}

public sealed record SkillView(Guid Id, IReadOnlyDictionary<string, string> Names, string Status, int People);

/// <summary>
/// The tenant's skill list and settings. <see cref="Available"/> is whether the package has <c>people.skills</c>;
/// <see cref="Enabled"/> whether the tenant has skills on. Suggested skills are listed only for administrators.
/// </summary>
public sealed record SkillListView(bool Available, bool Enabled, bool AllowFreeText, DateTimeOffset? TurnedOffAt, IReadOnlyList<SkillView> Skills, bool CanManage, int Version)
{
    public string ETag => Skills_.ETag(Version);
}

public sealed record SkillInput(Guid? Id, IReadOnlyDictionary<string, string>? Names, bool? Approve);

public sealed record SkillListRequest(bool? Enabled, bool? AllowFreeText, IReadOnlyList<SkillInput>? Skills);

public sealed record MergeSkillRequest(Guid? IntoId);

/// <summary>A skill on a profile; <see cref="Endorsements"/> counts the endorsements the owner did not hide.</summary>
public sealed record PersonSkillView(Guid SkillId, IReadOnlyDictionary<string, string> Names, string Proficiency, bool Suggested, int Endorsements,
    bool EndorsedByMe, bool CanEndorse);

public sealed record PersonSkillsView(bool Enabled, IReadOnlyList<PersonSkillView> Items, bool IsSelf);

public sealed record Endorser(Guid Id, string DisplayName, bool Hidden);

public sealed record OwnSkillView(Guid SkillId, IReadOnlyDictionary<string, string> Names, string Proficiency, bool Suggested, IReadOnlyList<Endorser> Endorsers);

public sealed record OwnSkillsView(bool Enabled, bool AllowFreeText, IReadOnlyList<OwnSkillView> Items, IReadOnlyList<SkillView> Choices);

public sealed record OwnSkillInput(Guid? SkillId, string? Name, string? Proficiency);

public sealed record OwnSkillsRequest(IReadOnlyList<OwnSkillInput>? Skills);

public sealed record ExpertView(PersonCard Person, Guid SkillId, IReadOnlyDictionary<string, string> SkillNames, string Proficiency, int Endorsements);

public sealed record ExpertsView(bool Enabled, IReadOnlyList<SkillView> Matches, IReadOnlyList<ExpertView> Items);

/// <summary>ETag helper; the record above cannot reference the service's static method by a name it shadows.</summary>
internal static class Skills_
{
    public static string ETag(int version) => $"\"s{version}\"";
}

/// <summary>
/// Skills and Find an expert (spec 010 R2 US6, FR-018, FR-019, FR-031). The tenant keeps a list of skills; employees
/// declare up to <see cref="MaxPerPerson"/> of them with a proficiency, and colleagues endorse them once each. Skills show
/// to everyone who may open the person's profile. Find an expert ranks by proficiency, then visible endorsements, then
/// name. While the package has no <c>people.skills</c> or the tenant turned skills off, nothing shows and nothing is
/// accepted; 90 days after skills were turned off they are deleted by the People maintenance job.
/// </summary>
public sealed class Skills(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    PeopleService people,
    ProfileStore store,
    INotificationSender notifications,
    IAuditWriter audit,
    TeamBeats.Localization.RequestLocale locale,
    IClock clock)
{
    public const int MaxSkills = 300;
    public const int MaxPerPerson = 30;
    public const int MaxNameLength = 80;
    public const int MaxExperts = 50;
    public static readonly TimeSpan KeepAfterOff = TimeSpan.FromDays(90);
    public const string EndorsedTemplate = "people.skill.endorsed";

    private sealed record Setting(bool Enabled, bool AllowFreeText, DateTimeOffset? TurnedOffAt, int Version);

    private sealed record SkillRow(Guid Id, Dictionary<string, string> Names, string Status, Guid? SuggestedBy);

    private async Task<Setting> SettingAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select enabled, allow_free_text, turned_off_at, version from people.skill_setting", null,
            r => new Setting(r.GetBoolean(0), r.GetBoolean(1), r.Get<DateTimeOffset?>("turned_off_at"), r.GetInt32(3)), ct)
        ?? new Setting(true, false, null, 0);

    /// <summary>Whether skills show at all: the package has them and the tenant has them on.</summary>
    internal async Task<bool> OnAsync(CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.PeopleSkills, ct) && (await SettingAsync(ct)).Enabled;

    private async Task<bool> ManagesAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.PeopleManage, AuthzTarget.Tenant, ct)).Allowed;

    private async Task RequireOnAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PeopleSkills, ct);
        if (!(await SettingAsync(ct)).Enabled)
        {
            throw AppException.Conflict("conflict.people.skills_off");
        }
    }

    private async Task<List<SkillRow>> SkillRowsAsync(string where, object? args, CancellationToken ct) =>
        (await session.QueryAsync($"select id, names::text, status, suggested_by from people.skill where {where} order by search, id", args,
            r => new SkillRow(r.GetGuid(0), NamesOf(r.GetString(1)), r.GetString(2), r.Get<Guid?>("suggested_by")), ct)).ToList();

    internal static string SearchText(IReadOnlyDictionary<string, string> names) =>
        string.Join(' ', names.Values.Select(SearchText_).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal));

    private static string SearchText_(string value) => TeamBeats.Modules.People.SearchText.Normalize(value);

    private static Dictionary<string, string> CleanNames(IReadOnlyDictionary<string, string>? names, string field)
    {
        var clean = (names ?? new Dictionary<string, string>())
            .Where(n => PeopleService.NameLocales.Contains(n.Key) && !string.IsNullOrWhiteSpace(n.Value))
            .ToDictionary(n => n.Key, n => n.Value.Trim(), StringComparer.Ordinal);
        if (clean.Count == 0)
        {
            throw AppException.Validation(field, "validation.people.skill_name");
        }
        if (clean.Values.Any(v => v.Length > MaxNameLength))
        {
            throw AppException.Validation(field, "validation.people.skill_name_length");
        }
        return clean;
    }

    // ---- The tenant's list ----

    public async Task<SkillListView> ListAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.PeopleView, ct))
        {
            throw AppException.Denied();
        }
        var available = await entitlements.IsEnabledAsync(Capabilities.PeopleSkills, ct);
        var setting = await SettingAsync(ct);
        var manages = await ManagesAsync(ct);
        if (!available || (!setting.Enabled && !manages))
        {
            return new SkillListView(available, setting.Enabled, setting.AllowFreeText, setting.TurnedOffAt, [], manages, setting.Version);
        }
        var rows = await SkillRowsAsync(manages ? "true" : "status = 'active'", null, ct);
        var counts = (await session.QueryAsync("select skill_id, count(*)::int from people.person_skill group by skill_id", null,
            r => (Id: r.GetGuid(0), Count: r.GetInt32(1)), ct)).ToDictionary(c => c.Id, c => c.Count);
        return new SkillListView(available, setting.Enabled, setting.AllowFreeText, setting.TurnedOffAt,
            rows.Select(r => new SkillView(r.Id, r.Names, r.Status, counts.GetValueOrDefault(r.Id))).ToList(), manages, setting.Version);
    }

    /// <summary>
    /// Replaces the settings and, when given, the list: skills with an id are renamed (and approved when asked), skills
    /// without one are added, and skills left out are removed with everyone's declarations of them. Audited.
    /// </summary>
    public async Task<SkillListView> SetListAsync(SkillListRequest request, string? ifMatch, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PeopleSkills, ct);
        if (!await ManagesAsync(ct))
        {
            throw AppException.Denied();
        }
        var setting = await SettingAsync(ct);
        if (ifMatch != Skills_.ETag(setting.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.people.skills_changed", null,
                new Dictionary<string, object?> { ["etag"] = Skills_.ETag(setting.Version) });
        }
        var now = clock.UtcNow;
        var user = context.RequireUser();
        var enabled = request.Enabled ?? setting.Enabled;
        var turnedOffAt = enabled ? null : setting.Enabled ? now : setting.TurnedOffAt;
        await session.ExecAsync(
            """
            insert into people.skill_setting (tenant_id, enabled, allow_free_text, turned_off_at, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @enabled, @free, @off, 1, @user, @now)
            on conflict (tenant_id) do update set enabled = excluded.enabled, allow_free_text = excluded.allow_free_text, turned_off_at = excluded.turned_off_at,
              version = people.skill_setting.version + 1, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { enabled, free = request.AllowFreeText ?? setting.AllowFreeText, off = turnedOffAt, user, now }, ct);

        int added = 0, renamed = 0, removed = 0;
        if (request.Skills is { } skills)
        {
            if (skills.Count > MaxSkills)
            {
                throw AppException.Validation("skills", "validation.people.skill_count");
            }
            var current = (await SkillRowsAsync("true", null, ct)).ToDictionary(r => r.Id);
            var keep = new HashSet<Guid>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < skills.Count; i++)
            {
                var names = CleanNames(skills[i].Names, $"skills[{i}].names");
                var search = SearchText(names);
                if (!seen.Add(search))
                {
                    throw AppException.Validation($"skills[{i}].names", "validation.people.skill_duplicate");
                }
                if (skills[i].Id is { } id && current.TryGetValue(id, out var row))
                {
                    keep.Add(id);
                    var status = skills[i].Approve == true ? "active" : row.Status;
                    if (!row.Names.OrderBy(n => n.Key).SequenceEqual(names.OrderBy(n => n.Key)) || status != row.Status)
                    {
                        await session.ExecAsync("update people.skill set names = @names::jsonb, search = @search, status = @status where id = @id",
                            new { id, names = Json(names), search, status }, ct);
                        renamed++;
                    }
                }
                else
                {
                    await session.ExecAsync(
                        "insert into people.skill (tenant_id, id, names, status, search, created_at) values (platform.current_tenant_id(), @id, @names::jsonb, 'active', @search, @now)",
                        new { id = Ids.New(), names = Json(names), search, now }, ct);
                    added++;
                }
            }
            var gone = current.Keys.Where(id => !keep.Contains(id)).ToArray();
            if (gone.Length > 0)
            {
                removed = await session.ExecAsync("delete from people.skill where id = any(@gone)", new { gone }, ct);
            }
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PeopleSkillListChanged, "people_skills", "tenant",
            Changes: new { enabled, allowFreeText = request.AllowFreeText ?? setting.AllowFreeText, added, renamed, removed }), ct);
        return await ListAsync(ct);
    }

    /// <summary>
    /// Merges one skill into another: every declaration moves to the target (keeping the higher proficiency), with its
    /// endorsements, and the merged skill goes. Audited.
    /// </summary>
    public async Task<SkillListView> MergeAsync(Guid skillId, MergeSkillRequest request, CancellationToken ct)
    {
        await RequireOnAsync(ct);
        if (!await ManagesAsync(ct))
        {
            throw AppException.Denied();
        }
        if (request.IntoId is not { } into || into == skillId)
        {
            throw AppException.Validation("intoId", "validation.people.skill_merge");
        }
        var found = await SkillRowsAsync("id = any(@ids)", new { ids = new[] { skillId, into } }, ct);
        if (found.Count != 2)
        {
            throw AppException.NotFound();
        }
        await session.ExecAsync(
            """
            insert into people.person_skill (tenant_id, user_id, skill_id, proficiency, added_at)
            select tenant_id, user_id, @into, proficiency, added_at from people.person_skill where skill_id = @from
            on conflict (tenant_id, user_id, skill_id) do update set proficiency =
              case when array_position(array['basic','good','expert'], excluded.proficiency) > array_position(array['basic','good','expert'], people.person_skill.proficiency)
                then excluded.proficiency else people.person_skill.proficiency end
            """, new { into, from = skillId }, ct);
        await session.ExecAsync(
            """
            insert into people.endorsement (tenant_id, user_id, skill_id, endorser_id, hidden, created_at)
            select tenant_id, user_id, @into, endorser_id, hidden, created_at from people.endorsement where skill_id = @from
            on conflict do nothing
            """, new { into, from = skillId }, ct);
        await session.ExecAsync("update people.skill set status = 'active' where id = @into", new { into }, ct);
        await session.ExecAsync("delete from people.skill where id = @from", new { from = skillId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PeopleSkillListChanged, "people_skills", "tenant",
            Changes: new { merged = skillId, into }), ct);
        return await ListAsync(ct);
    }

    // ---- A person's skills ----

    public async Task<PersonSkillsView> PersonAsync(Guid userId, CancellationToken ct)
    {
        var viewer = await people.RequireVisibleAsync(userId, ct);
        if (!await OnAsync(ct))
        {
            return new PersonSkillsView(false, [], viewer == userId);
        }
        var manages = await ManagesAsync(ct);
        var rows = await session.QueryAsync(
            """
            select s.id, s.names::text, ps.proficiency, s.status,
              (select count(*)::int from people.endorsement e where e.user_id = ps.user_id and e.skill_id = ps.skill_id and not e.hidden),
              exists (select 1 from people.endorsement e where e.user_id = ps.user_id and e.skill_id = ps.skill_id and e.endorser_id = @viewer)
            from people.person_skill ps join people.skill s on s.id = ps.skill_id
            where ps.user_id = @userId and (s.status = 'active' or @mine or @manages)
            """, new { userId, viewer, mine = viewer == userId, manages },
            r => new PersonSkillView(r.GetGuid(0), NamesOf(r.GetString(1)), r.GetString(2), r.GetString(3) == "suggested", r.GetInt32(4), r.GetBoolean(5),
                CanEndorse: viewer != userId && r.GetString(3) == "active"), ct);
        return new PersonSkillsView(true, Order(rows), viewer == userId);
    }

    private static List<PersonSkillView> Order(IEnumerable<PersonSkillView> rows) =>
        rows.OrderByDescending(r => Proficiencies.Rank(r.Proficiency)).ThenByDescending(r => r.Endorsements)
            .ThenBy(r => r.Names.GetValueOrDefault("en") ?? r.Names.Values.First(), StringComparer.CurrentCultureIgnoreCase).ToList();

    public async Task<OwnSkillsView> MineAsync(CancellationToken ct)
    {
        var me = await people.RequireVisibleAsync(context.RequireUser(), ct);
        var setting = await SettingAsync(ct);
        if (!await OnAsync(ct))
        {
            return new OwnSkillsView(false, false, [], []);
        }
        var mine = await session.QueryAsync(
            "select s.id, s.names::text, ps.proficiency, s.status from people.person_skill ps join people.skill s on s.id = ps.skill_id where ps.user_id = @me",
            new { me }, r => (Id: r.GetGuid(0), Names: NamesOf(r.GetString(1)), Proficiency: r.GetString(2), Suggested: r.GetString(3) == "suggested"), ct);
        var endorsements = (await session.QueryAsync("select skill_id, endorser_id, hidden from people.endorsement where user_id = @me order by created_at", new { me },
            r => (Skill: r.GetGuid(0), Endorser: r.GetGuid(1), Hidden: r.GetBoolean(2)), ct)).ToList();
        var names = (await store.GetManyAsync(endorsements.Select(e => e.Endorser).Distinct(), ct)).ToDictionary(r => r.UserId, r => r.DisplayName);
        var items = mine.Select(m => new OwnSkillView(m.Id, m.Names, m.Proficiency, m.Suggested,
                endorsements.Where(e => e.Skill == m.Id).Select(e => new Endorser(e.Endorser, names.GetValueOrDefault(e.Endorser, ""), e.Hidden)).ToList()))
            .OrderByDescending(m => Proficiencies.Rank(m.Proficiency)).ToList();
        var choices = (await SkillRowsAsync("status = 'active'", null, ct)).Select(r => new SkillView(r.Id, r.Names, r.Status, 0)).ToList();
        return new OwnSkillsView(true, setting.AllowFreeText, items, choices);
    }

    /// <summary>
    /// Replaces the signed-in person's skills. A skill kept keeps its endorsements; one dropped loses them. A name
    /// instead of an id, when the tenant allows free text, reuses a skill of the same name or adds a suggested one.
    /// </summary>
    public async Task<OwnSkillsView> SetMineAsync(OwnSkillsRequest request, CancellationToken ct)
    {
        var me = await people.RequireVisibleAsync(context.RequireUser(), ct);
        await RequireOnAsync(ct);
        var setting = await SettingAsync(ct);
        var input = request.Skills ?? [];
        if (input.Count > MaxPerPerson)
        {
            throw AppException.Validation("skills", "validation.people.skills_per_person");
        }
        var active = (await SkillRowsAsync("status = 'active' or suggested_by = @me", new { me }, ct)).ToDictionary(r => r.Id);
        var all = await SkillRowsAsync("true", null, ct);
        var chosen = new Dictionary<Guid, string>();
        var now = clock.UtcNow;
        for (var i = 0; i < input.Count; i++)
        {
            var proficiency = input[i].Proficiency is { } p && Proficiencies.All.Contains(p) ? p
                : throw AppException.Validation($"skills[{i}].proficiency", "validation.people.proficiency");
            Guid id;
            if (input[i].SkillId is { } given)
            {
                id = active.ContainsKey(given) ? given : throw AppException.Validation($"skills[{i}].skillId", "validation.people.skill_unknown");
            }
            else if (!string.IsNullOrWhiteSpace(input[i].Name))
            {
                if (!setting.AllowFreeText)
                {
                    throw AppException.Validation($"skills[{i}].name", "validation.people.skill_free_text");
                }
                var name = input[i].Name!.Trim();
                if (name.Length > MaxNameLength)
                {
                    throw AppException.Validation($"skills[{i}].name", "validation.people.skill_name_length");
                }
                var search = SearchText_(name);
                var existing = all.FirstOrDefault(s => s.Names.Values.Any(v => SearchText_(v) == search));
                if (existing is not null && (existing.Status == "active" || existing.SuggestedBy == me))
                {
                    id = existing.Id;
                }
                else
                {
                    if (all.Count >= MaxSkills)
                    {
                        throw AppException.Validation($"skills[{i}].name", "validation.people.skill_count");
                    }
                    id = Ids.New();
                    var names = new Dictionary<string, string> { [PeopleService.NameLocales.Contains(locale.Locale) ? locale.Locale : "en"] = name };
                    await session.ExecAsync(
                        "insert into people.skill (tenant_id, id, names, status, search, suggested_by, created_at) values (platform.current_tenant_id(), @id, @names::jsonb, 'suggested', @search, @me, @now)",
                        new { id, names = Json(names), search, me, now }, ct);
                    all.Add(new SkillRow(id, names, "suggested", me));
                }
            }
            else
            {
                throw AppException.Validation($"skills[{i}].skillId", "validation.people.skill_unknown");
            }
            chosen[id] = proficiency;
        }
        await session.ExecAsync("delete from people.person_skill where user_id = @me and not (skill_id = any(@keep))", new { me, keep = chosen.Keys.ToArray() }, ct);
        foreach (var (id, proficiency) in chosen)
        {
            await session.ExecAsync(
                """
                insert into people.person_skill (tenant_id, user_id, skill_id, proficiency, added_at) values (platform.current_tenant_id(), @me, @id, @proficiency, @now)
                on conflict (tenant_id, user_id, skill_id) do update set proficiency = excluded.proficiency
                """, new { me, id, proficiency, now }, ct);
        }
        // A suggestion nobody declares any more goes.
        await session.ExecAsync("delete from people.skill s where s.status = 'suggested' and not exists (select 1 from people.person_skill ps where ps.skill_id = s.id)", null, ct);
        return await MineAsync(ct);
    }

    // ---- Endorsements ----

    public async Task<PersonSkillsView> EndorseAsync(Guid userId, Guid skillId, bool on, CancellationToken ct)
    {
        var viewer = await people.RequireVisibleAsync(userId, ct);
        await RequireOnAsync(ct);
        if (viewer == userId)
        {
            throw AppException.Conflict("conflict.people.endorse_self");
        }
        var declared = await session.QuerySingleOrDefaultAsync(
            "select 1 from people.person_skill ps join people.skill s on s.id = ps.skill_id where ps.user_id = @userId and ps.skill_id = @skillId and s.status = 'active'",
            new { userId, skillId }, r => r.GetInt32(0), ct);
        if (declared == 0)
        {
            throw AppException.NotFound();
        }
        if (on)
        {
            var added = await session.ExecAsync(
                "insert into people.endorsement (tenant_id, user_id, skill_id, endorser_id, created_at) values (platform.current_tenant_id(), @userId, @skillId, @viewer, @now) on conflict do nothing",
                new { userId, skillId, viewer, now = clock.UtcNow }, ct);
            if (added > 0)
            {
                await notifications.SendAsync(new NotificationRequest([userId], NotificationCategories.Recognition, EndorsedTemplate, "/people/me",
                    DedupeKey: $"people.endorsed:{userId:N}:{skillId:N}:{viewer:N}"), ct);
            }
        }
        else
        {
            await session.ExecAsync("delete from people.endorsement where user_id = @userId and skill_id = @skillId and endorser_id = @viewer", new { userId, skillId, viewer }, ct);
        }
        return await PersonAsync(userId, ct);
    }

    /// <summary>The owner hides or shows an endorsement; a hidden one is not counted or ranked.</summary>
    public async Task<OwnSkillsView> HideAsync(Guid skillId, Guid endorserId, bool hidden, CancellationToken ct)
    {
        var me = context.RequireUser();
        await RequireOnAsync(ct);
        if (await session.ExecAsync("update people.endorsement set hidden = @hidden where user_id = @me and skill_id = @skillId and endorser_id = @endorserId",
                new { hidden, me, skillId, endorserId }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        return await MineAsync(ct);
    }

    // ---- Find an expert ----

    public async Task<ExpertsView> ExpertsAsync(Guid? skillId, string? q, CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.PeopleView, ct))
        {
            throw AppException.Denied();
        }
        if (!await OnAsync(ct))
        {
            return new ExpertsView(false, [], []);
        }
        var active = await SkillRowsAsync("status = 'active'", null, ct);
        var text = SearchText_(q ?? "");
        var matches = skillId is { } id ? active.Where(s => s.Id == id).ToList()
            : text.Length >= 2 ? active.Where(s => s.Names.Values.Any(v => SearchText_(v).Contains(text, StringComparison.Ordinal))).Take(20).ToList()
            : [];
        var skillViews = matches.Select(s => new SkillView(s.Id, s.Names, s.Status, 0)).ToList();
        if (matches.Count == 0)
        {
            return new ExpertsView(true, skillViews, []);
        }
        var rows = await session.QueryAsync(
            """
            select ps.user_id, ps.skill_id, ps.proficiency,
              (select count(*)::int from people.endorsement e where e.user_id = ps.user_id and e.skill_id = ps.skill_id and not e.hidden)
            from people.person_skill ps where ps.skill_id = any(@ids)
            """, new { ids = matches.Select(m => m.Id).ToArray() },
            r => (User: r.GetGuid(0), Skill: r.GetGuid(1), Proficiency: r.GetString(2), Endorsements: r.GetInt32(3)), ct);
        // One entry per person: their strongest matching skill.
        var best = rows.GroupBy(r => r.User).Select(g => g.OrderByDescending(r => Proficiencies.Rank(r.Proficiency)).ThenByDescending(r => r.Endorsements).First()).ToList();
        var cards = (await people.VisibleCardsAsync(best.Select(b => b.User).ToList(), ct)).ToDictionary(c => c.Id);
        var byId = matches.ToDictionary(m => m.Id);
        var experts = best.Where(b => cards.ContainsKey(b.User))
            .OrderByDescending(b => Proficiencies.Rank(b.Proficiency)).ThenByDescending(b => b.Endorsements)
            .ThenBy(b => cards[b.User].DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxExperts)
            .Select(b => new ExpertView(cards[b.User], b.Skill, byId[b.Skill].Names, b.Proficiency, b.Endorsements)).ToList();
        return new ExpertsView(true, skillViews, experts);
    }

    // ---- Purge ----

    /// <summary>Deletes every skill, declaration and endorsement 90 days after the tenant turned skills off (FR-031).</summary>
    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var setting = await SettingAsync(ct);
        if (setting.Enabled || setting.TurnedOffAt is not { } off || now - off < KeepAfterOff)
        {
            return 0;
        }
        return await session.ExecAsync("delete from people.skill", null, ct);
    }

    private static Dictionary<string, string> NamesOf(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);
}
