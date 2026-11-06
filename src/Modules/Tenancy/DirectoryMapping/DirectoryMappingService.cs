using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy.DirectoryMapping;

/// <summary>How units are derived from the directory (spec 002 US5 scenario 1). <c>ou_path</c> needs on-premises AD (R2).</summary>
public static class MappingStrategies
{
    public const string OuPath = "ou_path";
    public const string Attributes = "attributes";
    public const string AdminUnits = "admin_units";
    public const string Groups = "groups";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { OuPath, Attributes, AdminUnits, Groups };
    public static readonly IReadOnlySet<string> AvailableForEntra = new HashSet<string> { Attributes, AdminUnits, Groups };
}

/// <summary>Directory attributes that can form levels, top first.</summary>
public static class MappingAttributes
{
    public const string Company = "company";
    public const string Division = "division";
    public const string Department = "department";

    public static readonly IReadOnlyDictionary<string, string> UnitType = new Dictionary<string, string>
    {
        [Company] = "subsidiary",
        [Division] = "division",
        [Department] = "department",
    };
}

public sealed record AliasRule(string From, string To);

/// <summary>Maps a directory value path (a unit key from the preview) to an existing product unit (scenario 7).</summary>
public sealed record ValueMapping(string Key, Guid UnitId);

public sealed record OrgMappingSettings(
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Groups,
    IReadOnlyList<AliasRule> Aliases,
    IReadOnlyList<ValueMapping> ValueMap,
    Guid? ParentUnitId);

public sealed record OrgMappingRequest(
    string? Strategy,
    IReadOnlyList<string>? Levels,
    IReadOnlyList<string>? Groups,
    IReadOnlyList<AliasRule>? Aliases,
    IReadOnlyList<ValueMapping>? ValueMap,
    Guid? ParentUnitId,
    int? DeletionThresholdPct);

public sealed record OrgMappingView(Guid ConnectionId, string Strategy, OrgMappingSettings Settings, int DeletionThresholdPct,
    DateTimeOffset? PreviewedAt, DateTimeOffset? ApprovedAt);

public sealed record PlannedUnit(string Key, string? ParentKey, Guid? ParentUnitId, string Name, string TypeKey);

public sealed record PlannedRename(Guid UnitId, string Key, string From, string To);

public sealed record PlannedMove(Guid UnitId, string Key, Guid? FromParentId, string? ToParentKey, Guid? ToParentUnitId);

public sealed record PlannedMembership(Guid UserId, string Key);

public sealed record EndedMembership(Guid MembershipId, Guid UserId, Guid UnitId, bool Replaced);

public sealed record FlaggedUnit(Guid UnitId, string Key, string Reason);

public sealed record UnmappedUser(Guid UserId, string Reason);

public sealed record MappingDiff(
    IReadOnlyList<PlannedUnit> Create,
    IReadOnlyList<Guid> Reactivate,
    IReadOnlyList<PlannedRename> Rename,
    IReadOnlyList<PlannedMove> Move,
    IReadOnlyList<Guid> Deactivate,
    IReadOnlyList<FlaggedUnit> Flagged,
    IReadOnlyList<PlannedMembership> AddMemberships,
    IReadOnlyList<EndedMembership> EndMemberships,
    IReadOnlyList<UnmappedUser> Unmapped,
    bool ThresholdExceeded);

public sealed record MappingRun(Guid Id, string Mode, DateTimeOffset StartedAt, string Outcome, IReadOnlyDictionary<string, int> Counts);

public sealed record MappingRunResult(MappingRun Run, MappingDiff Diff);

/// <summary>Raised when the deletion threshold stops an apply (scenario 8); the admin is alerted from it.</summary>
public sealed record OrgMappingNeedsAttention(Guid ConnectionId, string Reason) : IIntegrationEvent;

/// <summary>
/// Maps the organization hierarchy from the directory (spec 002 User Story 5, R1 for Entra ID). Directory data comes
/// from Identity through <see cref="IDirectoryReader"/>; Tenancy never calls the directory. Each run diffs the whole
/// directory snapshot against the units and memberships this connection owns, so runs are idempotent. Product-owned
/// units are never changed. A preview runs the same diff without applying it. Access is checked by the endpoints.
/// </summary>
public sealed partial class DirectoryMappingService(
    IDbSession session,
    TenantContextAccessor context,
    IDirectoryReader directory,
    OrgHierarchyService hierarchy,
    OrgMembershipService memberships,
    ITenantSettingsReader tenantSettings,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    private const string Select =
        "select connection_id, strategy, settings::text, deletion_threshold_pct, previewed_at, approved_at from tenancy.directory_mapping";

    private static OrgMappingView MapView(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1),
        JsonSerializer.Deserialize<OrgMappingSettings>(r.GetString(2), JsonDefaults.Options) ?? new OrgMappingSettings([], [], [], [], null),
        r.GetInt32(3), r.Get<DateTimeOffset?>("previewed_at"), r.Get<DateTimeOffset?>("approved_at"));

    public static string ExternalSource(Guid connectionId) => "entra:" + connectionId.ToString("D");

    public Task<OrgMappingView?> GetAsync(CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(Select + " order by created_at limit 1", null, MapView, ct);

    public async Task<IReadOnlyList<MappingRun>> RecentRunsAsync(Guid connectionId, int limit, CancellationToken ct) =>
        await session.QueryAsync(
            "select id, mode, started_at, outcome, counts::text from tenancy.directory_mapping_run where connection_id = @c order by started_at desc limit @n",
            new { c = connectionId, n = Math.Clamp(limit, 1, 50) },
            r => new MappingRun(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3),
                JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(4))!), ct);

    /// <summary>Saves the strategy and settings. Any change needs a new preview and approval before it is applied.</summary>
    public async Task<OrgMappingView> SaveAsync(OrgMappingRequest request, CancellationToken ct)
    {
        var snapshot = await directory.ReadAsync(ct) ?? throw AppException.Conflict("conflict.org_mapping.no_directory");
        var current = await GetAsync(ct);
        var strategy = request.Strategy?.Trim().ToLowerInvariant() ?? current?.Strategy;
        if (strategy is null || !MappingStrategies.All.Contains(strategy))
        {
            throw AppException.Validation("strategy", "validation.org_mapping.strategy");
        }
        if (!MappingStrategies.AvailableForEntra.Contains(strategy))
        {
            throw AppException.Validation("strategy", "validation.org_mapping.strategy_unavailable");
        }

        var levels = (request.Levels ?? current?.Settings.Levels ?? [MappingAttributes.Company, MappingAttributes.Department])
            .Select(l => l.Trim().ToLowerInvariant()).ToList();
        if (strategy == MappingStrategies.Attributes
            && (levels.Count == 0 || levels.Distinct().Count() != levels.Count || levels.Any(l => !MappingAttributes.UnitType.ContainsKey(l))))
        {
            throw AppException.Validation("levels", "validation.org_mapping.levels");
        }

        var groups = new List<string>();
        foreach (var g in request.Groups ?? current?.Settings.Groups ?? [])
        {
            groups.Add(Guid.TryParse(g, out var id) ? id.ToString("D") : throw AppException.Validation("groups", "validation.org_mapping.group_id"));
        }
        if (strategy == MappingStrategies.Groups && groups.Count == 0)
        {
            throw AppException.Validation("groups", "validation.org_mapping.groups_required");
        }

        var aliases = (request.Aliases ?? current?.Settings.Aliases ?? []).ToList();
        if (aliases.Any(a => string.IsNullOrWhiteSpace(a.From) || string.IsNullOrWhiteSpace(a.To) || a.From.Length > 200 || a.To.Length > 200))
        {
            throw AppException.Validation("aliases", "validation.org_mapping.alias");
        }

        var valueMap = (request.ValueMap ?? current?.Settings.ValueMap ?? []).ToList();
        foreach (var v in valueMap)
        {
            var unit = await hierarchy.LoadAsync(v.UnitId, ct);
            if (string.IsNullOrWhiteSpace(v.Key) || unit is null || unit.Owner != OrgOwners.Product || unit.Status != "active")
            {
                throw AppException.Validation("valueMap", "validation.org_mapping.value_map_unit");
            }
        }

        var parent = request.ParentUnitId ?? current?.Settings.ParentUnitId;
        if (parent is { } p && await hierarchy.LoadAsync(p, ct) is not { Status: "active" })
        {
            throw AppException.Validation("parentUnitId", "validation.org_mapping.parent_unit");
        }

        var threshold = request.DeletionThresholdPct ?? current?.DeletionThresholdPct ?? 10;
        if (threshold is < 1 or > 100)
        {
            throw AppException.Validation("deletionThresholdPct", "validation.org_mapping.threshold_range");
        }

        var settings = new OrgMappingSettings(strategy == MappingStrategies.Attributes ? levels : [], groups, aliases, valueMap, parent);
        await session.ExecAsync(
            """
            insert into tenancy.directory_mapping (tenant_id, id, connection_id, strategy, settings, deletion_threshold_pct)
            values (@t, @id, @c, @s, @settings, @th)
            on conflict (tenant_id, connection_id) do update set strategy = excluded.strategy, settings = excluded.settings,
              deletion_threshold_pct = excluded.deletion_threshold_pct, previewed_at = null, approved_at = null, updated_at = now()
            """, new { t = context.RequireTenant(), id = Ids.New(), c = snapshot.ConnectionId, s = strategy, settings = Jsonb.From(settings), th = threshold }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.OrgDirectoryMappingChanged, "connection", snapshot.ConnectionId.ToString(),
            Changes: new { strategy, levels = settings.Levels, groups = groups.Count, aliases = aliases.Count, valueMap = valueMap.Count, parentUnitId = parent, deletionThresholdPct = threshold }), ct);
        return (await GetAsync(ct))!;
    }

    /// <summary>Runs the diff without changing any unit or membership (scenario 2).</summary>
    public async Task<MappingRunResult> PreviewAsync(CancellationToken ct)
    {
        var (mapping, snapshot) = await LoadAsync(ct);
        var diff = await PlanAsync(mapping, snapshot, ct);
        await session.ExecAsync("update tenancy.directory_mapping set previewed_at = now() where connection_id = @c", new { c = mapping.ConnectionId }, ct);
        return new MappingRunResult(await RecordAsync(mapping.ConnectionId, "preview", "succeeded", diff, ct), diff);
    }

    /// <summary>Approves the latest preview and applies it. Later directory syncs then apply automatically (scenario 3).</summary>
    public async Task<MappingRunResult> ApproveAsync(CancellationToken ct)
    {
        var (mapping, _) = await LoadAsync(ct);
        if (mapping.PreviewedAt is null)
        {
            throw AppException.Conflict("conflict.org_mapping.preview_required");
        }
        await session.ExecAsync("update tenancy.directory_mapping set approved_at = now() where connection_id = @c", new { c = mapping.ConnectionId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.OrgDirectoryMappingApproved, "connection", mapping.ConnectionId.ToString()), ct);
        return (await ApplyAsync(ct))!;
    }

    /// <summary>Applies the mapping if it is approved. Called after each directory sync.</summary>
    public async Task<MappingRunResult?> ApplyAsync(CancellationToken ct)
    {
        var mapping = await GetAsync(ct);
        if (mapping?.ApprovedAt is null)
        {
            return null;
        }
        var snapshot = await directory.ReadAsync(ct);
        if (snapshot is null || snapshot.ConnectionId != mapping.ConnectionId)
        {
            return null;
        }

        context.SetActor(ActorType.System, null);
        var diff = await PlanAsync(mapping, snapshot, ct);
        if (diff.ThresholdExceeded)
        {
            // Keep the last good state and alert the tenant admin.
            await outbox.EnqueueAsync(new OrgMappingNeedsAttention(mapping.ConnectionId, "threshold"), ct);
            return new MappingRunResult(await RecordAsync(mapping.ConnectionId, "apply", "stopped_threshold", diff, ct), diff);
        }

        var source = ExternalSource(mapping.ConnectionId);
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var unitIds = await DirectoryUnitIdsAsync(source, ct);
        foreach (var v in mapping.Settings.ValueMap)
        {
            unitIds[v.Key] = v.UnitId;
        }

        // Units first, parents before children.
        foreach (var unit in diff.Create)
        {
            var parentId = unit.ParentKey is { } pk ? unitIds[pk] : unit.ParentUnitId;
            var id = await hierarchy.InsertUnitAsync(parentId, unit.TypeKey, Code(mapping.ConnectionId, unit.Key), new Dictionary<string, string> { [locale] = unit.Name },
                new OrgHierarchyService.DirectoryOwnership(mapping.ConnectionId, source, unit.Key), ct);
            unitIds[unit.Key] = id;
        }
        foreach (var id in diff.Reactivate)
        {
            await hierarchy.ReactivateFromDirectoryAsync(id, ct);
        }
        foreach (var rename in diff.Rename)
        {
            await hierarchy.RenameFromDirectoryAsync(rename.UnitId, locale, rename.From, rename.To, ct);
        }
        foreach (var move in diff.Move)
        {
            var unit = (await hierarchy.LoadAsync(move.UnitId, ct))!;
            var parentId = move.ToParentKey is { } pk ? unitIds[pk] : move.ToParentUnitId;
            var parent = parentId is { } p ? await hierarchy.LoadAsync(p, ct) : null;
            await hierarchy.MoveCoreAsync(unit, parentId, parent, ct);
        }

        // New primary first, so ending the old one never promotes another membership in between.
        foreach (var m in diff.AddMemberships)
        {
            await memberships.AddCoreAsync(unitIds[m.Key], m.UserId, "primary", null, mapping.ConnectionId, ct);
        }
        foreach (var m in diff.EndMemberships)
        {
            await memberships.EndFromDirectoryAsync(m.MembershipId, ct);
        }
        foreach (var id in diff.Deactivate)
        {
            await hierarchy.DeactivateFromDirectoryAsync(id, ct);
        }

        return new MappingRunResult(await RecordAsync(mapping.ConnectionId, "apply", "succeeded", diff, ct), diff);
    }

    /// <summary>
    /// When the directory connection is deleted, its units and memberships become product-owned so admins can manage them
    /// again; nothing is deleted.
    /// </summary>
    public async Task ReleaseConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        await session.ExecAsync("update tenancy.org_unit set owner = 'product', owner_connection_id = null where owner_connection_id = @c", new { c = connectionId }, ct);
        await session.ExecAsync("update tenancy.org_membership set owner = 'product', owner_connection_id = null where owner_connection_id = @c", new { c = connectionId }, ct);
        await session.ExecAsync("delete from tenancy.directory_mapping_run where connection_id = @c", new { c = connectionId }, ct);
        await session.ExecAsync("delete from tenancy.directory_mapping where connection_id = @c", new { c = connectionId }, ct);
    }

    private async Task<(OrgMappingView Mapping, DirectorySnapshot Snapshot)> LoadAsync(CancellationToken ct)
    {
        var mapping = await GetAsync(ct) ?? throw AppException.Conflict("conflict.org_mapping.not_configured");
        var snapshot = await directory.ReadAsync(ct);
        if (snapshot is null || snapshot.ConnectionId != mapping.ConnectionId)
        {
            throw AppException.Conflict("conflict.org_mapping.no_directory");
        }
        return (mapping, snapshot);
    }

    private async Task<Dictionary<string, Guid>> DirectoryUnitIdsAsync(string source, CancellationToken ct) =>
        (await session.QueryAsync("select external_id, id from tenancy.org_unit where external_source = @s", new { s = source },
            r => (r.GetString(0), r.GetGuid(1)), ct)).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

    private async Task<MappingRun> RecordAsync(Guid connectionId, string mode, string outcome, MappingDiff diff, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>
        {
            ["create"] = diff.Create.Count + diff.Reactivate.Count,
            ["rename"] = diff.Rename.Count,
            ["move"] = diff.Move.Count,
            ["deactivate"] = diff.Deactivate.Count,
            ["flagged"] = diff.Flagged.Count,
            ["membershipsAdded"] = diff.AddMemberships.Count,
            ["membershipsEnded"] = diff.EndMemberships.Count,
            ["unmapped"] = diff.Unmapped.Count,
        };
        var id = Ids.New();
        await session.ExecAsync(
            "insert into tenancy.directory_mapping_run (tenant_id, id, connection_id, mode, outcome, counts, diff) values (@t, @id, @c, @m, @o, @counts, @diff)",
            new { t = context.RequireTenant(), id, c = connectionId, m = mode, o = outcome, counts = Jsonb.From(counts), diff = Jsonb.From(diff) }, ct);
        if (mode == "apply")
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.OrgDirectoryMappingApplied, "connection", connectionId.ToString(),
                Outcome: outcome == "succeeded" ? AuditOutcome.Success : AuditOutcome.Failure, Changes: new { outcome, counts }), ct);
        }
        return new MappingRun(id, mode, clock.UtcNow, outcome, counts);
    }

    private sealed record Existing(Guid Id, string Key, Guid? ParentId, string Status, string? Name);

    private sealed record Desired(string Key, string? ParentKey, Guid? ParentUnitId, string Name, string TypeKey, int Depth, Guid? MappedTo);

    private async Task<MappingDiff> PlanAsync(OrgMappingView mapping, DirectorySnapshot snapshot, CancellationToken ct)
    {
        var settings = mapping.Settings;
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var source = ExternalSource(mapping.ConnectionId);
        var valueMap = settings.ValueMap.ToDictionary(v => v.Key, v => v.UnitId, StringComparer.Ordinal);
        var aliases = settings.Aliases.GroupBy(a => Normalize(a.From)).ToDictionary(g => g.Key, g => g.First().To.Trim(), StringComparer.Ordinal);

        // 1. Desired units and each person's target unit key.
        var desired = new Dictionary<string, Desired>(StringComparer.Ordinal);
        var target = new Dictionary<Guid, string>();
        var unmapped = new List<UnmappedUser>();

        Desired Add(string key, string? parentKey, string name, string typeKey, int depth)
        {
            if (!desired.TryGetValue(key, out var d))
            {
                d = new Desired(key, parentKey, parentKey is null ? settings.ParentUnitId : null, name, typeKey, depth,
                    valueMap.TryGetValue(key, out var mapped) ? mapped : null);
                desired[key] = d;
            }
            return d;
        }

        switch (mapping.Strategy)
        {
            case MappingStrategies.Attributes:
                foreach (var person in snapshot.People)
                {
                    string? parentKey = null;
                    string? leaf = null;
                    for (var i = 0; i < settings.Levels.Count; i++)
                    {
                        var level = settings.Levels[i];
                        var raw = level switch
                        {
                            MappingAttributes.Company => person.Company,
                            MappingAttributes.Division => person.Division,
                            _ => person.Department,
                        };
                        if (string.IsNullOrWhiteSpace(raw))
                        {
                            break;
                        }
                        var normalized = Normalize(raw);
                        var display = aliases.TryGetValue(normalized, out var alias) ? alias : Clean(raw);
                        var segment = $"{level}={Normalize(display)}";
                        var key = parentKey is null ? segment : parentKey + "/" + segment;
                        Add(key, parentKey, display, MappingAttributes.UnitType[level], i);
                        parentKey = key;
                        leaf = key;
                    }
                    if (leaf is null)
                    {
                        unmapped.Add(new UnmappedUser(person.UserId, "no_value"));
                    }
                    else
                    {
                        target[person.UserId] = leaf;
                    }
                }
                break;

            case MappingStrategies.AdminUnits:
            case MappingStrategies.Groups:
                var kind = mapping.Strategy == MappingStrategies.AdminUnits ? "admin_unit" : "group";
                var selected = mapping.Strategy == MappingStrategies.Groups ? settings.Groups.ToHashSet(StringComparer.Ordinal) : null;
                foreach (var c in snapshot.Containers.Where(c => c.Kind == kind && (selected is null || selected.Contains(c.Id))))
                {
                    Add($"{kind}={c.Id}", null, Clean(c.DisplayName ?? c.Id), "department", 0);
                }
                foreach (var person in snapshot.People)
                {
                    var keys = (kind == "admin_unit" ? person.AdminUnits : person.Groups)
                        .Select(id => $"{kind}={id}").Where(desired.ContainsKey).ToList();
                    if (keys.Count == 1)
                    {
                        target[person.UserId] = keys[0];
                    }
                    else
                    {
                        // A person in several mapped units has no single primary unit; the admin decides.
                        unmapped.Add(new UnmappedUser(person.UserId, keys.Count == 0 ? "no_value" : "ambiguous"));
                    }
                }
                break;
        }

        // A value mapped to a product unit uses that unit and is never created.
        var memberUnit = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var d in desired.Values.Where(d => d.MappedTo is not null))
        {
            memberUnit[d.Key] = d.MappedTo!.Value;
        }

        // 2. Units: compare with what this connection owns.
        var existing = (await session.QueryAsync(
            """
            select u.id, u.external_id, u.parent_id, u.status, n.name
            from tenancy.org_unit u left join tenancy.org_unit_name n on n.org_unit_id = u.id and n.locale = @l
            where u.external_source = @s
            """, new { s = source, l = locale }, r => new Existing(r.GetGuid(0), r.GetString(1), r.Get<Guid?>("parent_id"), r.GetString(3), r.Get<string>("name")), ct))
            .ToDictionary(e => e.Key, StringComparer.Ordinal);

        Guid? ParentIdFor(Desired d) => d.ParentKey is null ? d.ParentUnitId
            : memberUnit.TryGetValue(d.ParentKey, out var mapped) ? mapped
            : existing.TryGetValue(d.ParentKey, out var e) ? e.Id : null;

        var create = new List<PlannedUnit>();
        var reactivate = new List<Guid>();
        var rename = new List<PlannedRename>();
        var move = new List<PlannedMove>();
        foreach (var d in desired.Values.Where(d => d.MappedTo is null).OrderBy(d => d.Depth).ThenBy(d => d.Key, StringComparer.Ordinal))
        {
            var parentKey = d.ParentKey is not null && !memberUnit.ContainsKey(d.ParentKey) ? d.ParentKey : null;
            var parentUnit = d.ParentKey is null ? d.ParentUnitId : memberUnit.GetValueOrDefault(d.ParentKey);
            if (!existing.TryGetValue(d.Key, out var e))
            {
                create.Add(new PlannedUnit(d.Key, parentKey, parentKey is null ? parentUnit : null, d.Name, d.TypeKey));
                continue;
            }
            memberUnit[d.Key] = e.Id;
            if (e.Status != "active")
            {
                reactivate.Add(e.Id);
            }
            if (e.Name != d.Name)
            {
                rename.Add(new PlannedRename(e.Id, d.Key, e.Name ?? string.Empty, d.Name));
            }
            var wantedParent = ParentIdFor(d);
            if (e.ParentId != wantedParent || (parentKey is not null && !existing.ContainsKey(parentKey)))
            {
                move.Add(new PlannedMove(e.Id, d.Key, e.ParentId, parentKey, parentKey is null ? parentUnit : null));
            }
        }

        // 3. Memberships owned by this connection.
        var current = await session.QueryAsync(
            "select id, user_id, org_unit_id from tenancy.org_membership where owner_connection_id = @c and valid_to is null",
            new { c = mapping.ConnectionId }, r => (Id: r.GetGuid(0), User: r.GetGuid(1), Unit: r.GetGuid(2)), ct);
        var currentByUser = current.GroupBy(m => m.User).ToDictionary(g => g.Key, g => g.ToList());
        var activeTenantMembers = (await session.QueryAsync("select user_id from tenancy.tenant_membership where status = 'active'", null, r => r.GetGuid(0), ct)).ToHashSet();
        var keyByUnit = existing.Values.ToDictionary(e => e.Id, e => e.Key);
        foreach (var (k, v) in memberUnit)
        {
            keyByUnit.TryAdd(v, k);
        }

        var add = new List<PlannedMembership>();
        var end = new List<EndedMembership>();
        foreach (var (userId, key) in target)
        {
            if (!activeTenantMembers.Contains(userId))
            {
                unmapped.Add(new UnmappedUser(userId, "inactive"));
                continue;
            }
            var have = currentByUser.GetValueOrDefault(userId) ?? [];
            var wantedUnit = memberUnit.TryGetValue(key, out var unitId) ? unitId : (Guid?)null;
            var keep = wantedUnit is { } w ? have.FirstOrDefault(m => m.Unit == w) : default;
            if (keep == default)
            {
                add.Add(new PlannedMembership(userId, key));
            }
            foreach (var m in have.Where(m => m != keep))
            {
                end.Add(new EndedMembership(m.Id, userId, m.Unit, Replaced: true));
            }
        }
        foreach (var m in current.Where(m => !target.ContainsKey(m.User) || !activeTenantMembers.Contains(m.User)))
        {
            end.Add(new EndedMembership(m.Id, m.User, m.Unit, Replaced: false));
        }

        // 4. Units that disappeared: deactivate only when nothing active is left in them (scenario 6).
        var gone = existing.Values.Where(e => e.Status == "active" && (!desired.TryGetValue(e.Key, out var d) || d.MappedTo is not null)).ToList();
        var goneIds = gone.Select(g => g.Id).ToHashSet();
        var deactivate = new List<Guid>();
        var flagged = new List<FlaggedUnit>();
        if (gone.Count > 0)
        {
            var movedAway = move.Select(m => m.UnitId).ToHashSet();
            var children = await session.QueryAsync(
                "select id, parent_id from tenancy.org_unit where parent_id = any(@ids) and status = 'active'",
                new { ids = goneIds.ToArray() }, r => (Id: r.GetGuid(0), Parent: r.GetGuid(1)), ct);
            var endedIds = end.Select(e => e.MembershipId).ToHashSet();
            var members = await session.QueryAsync(
                "select id, org_unit_id from tenancy.org_membership where org_unit_id = any(@ids) and valid_to is null",
                new { ids = goneIds.ToArray() }, r => (Id: r.GetGuid(0), Unit: r.GetGuid(1)), ct);
            foreach (var g in gone)
            {
                var activeChild = children.Any(c => c.Parent == g.Id && !goneIds.Contains(c.Id) && !movedAway.Contains(c.Id));
                var activeMember = members.Any(m => m.Unit == g.Id && !endedIds.Contains(m.Id));
                if (activeChild || activeMember)
                {
                    flagged.Add(new FlaggedUnit(g.Id, g.Key, activeChild ? "active_children" : "active_memberships"));
                }
                else
                {
                    deactivate.Add(g.Id);
                }
            }
            // Children before parents, so a parent never deactivates over an active child.
            deactivate = deactivate.OrderByDescending(id => gone.First(g => g.Id == id).Key.Count(c => c == '/')).ToList();
        }

        // 5. Deletion threshold (scenario 8): disappearing units, and memberships ended without a replacement.
        var activeUnits = existing.Values.Count(e => e.Status == "active");
        var removedMemberships = end.Count(e => !e.Replaced);
        var exceeded = (activeUnits > 0 && deactivate.Count * 100.0 / activeUnits > mapping.DeletionThresholdPct)
            || (current.Count > 0 && removedMemberships * 100.0 / current.Count > mapping.DeletionThresholdPct);

        return new MappingDiff(create, reactivate, rename, move, deactivate, flagged, add, end,
            unmapped.OrderBy(u => u.UserId).ToList(), exceeded);
    }

    /// <summary>Values that differ only in case, spacing or Unicode form are one unit (scenario 7).</summary>
    public static string Normalize(string value) => Spaces().Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " ").ToLower(CultureInfo.InvariantCulture);

    private static string Clean(string value)
    {
        var cleaned = Spaces().Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " ");
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    /// <summary>Stable unit code from the connection and the directory key; directory values never become codes.</summary>
    private static string Code(Guid connectionId, string key) =>
        "dir-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionId.ToString("N") + "|" + key)))[..16].ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
