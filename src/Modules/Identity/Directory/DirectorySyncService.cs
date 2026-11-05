using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Directory;

/// <summary>Which directory accounts become TeamBeats users: everyone, or members of selected groups (spec 003 US6 scenario 2).</summary>
public sealed record UserScope(string Mode, IReadOnlyList<string> Groups)
{
    public const string All = "all";
    public const string SelectedGroups = "groups";
}

public sealed record DirectorySettings(
    Guid ConnectionId,
    UserScope UserScope,
    IReadOnlyList<string> SyncGroups,
    int ScheduleMinutes,
    int DeletionThresholdPct,
    DateTimeOffset? LastRunAt,
    DateTimeOffset NextRunAt,
    int ConsecutiveFailures);

public sealed record DirectorySettingsRequest(UserScope? UserScope, IReadOnlyList<string>? SyncGroups, int? ScheduleMinutes, int? DeletionThresholdPct);

public sealed record SyncRun(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Outcome, string? Reason,
    int Created, int Updated, int Disabled, int Reenabled, int Groups, int Skipped);

/// <summary>"Sync now" from the admin page; handled on the worker.</summary>
public sealed record DirectorySyncRequested(Guid ConnectionId) : IIntegrationEvent;

/// <summary>Raised after 3 consecutive failures or when the deletion threshold stops a run (spec 003 US6 scenario 8).</summary>
public sealed record DirectorySyncNeedsAttention(Guid ConnectionId, string Reason, int ConsecutiveFailures) : IIntegrationEvent;

/// <summary>
/// Keeps users, tenant memberships and selected groups in step with a connected directory (spec 003 User Story 6,
/// FR-012 to FR-014). The directory is read through <see cref="IDirectorySource"/> as Graph deltas. Every account seen
/// is mirrored, so a change of user scope is applied without reading the directory again. Each run reconciles the
/// whole mirror, which makes runs idempotent and replayable. Users are keyed by the directory's immutable ID, never by
/// email or UPN. Nothing is hard-deleted.
/// </summary>
public sealed class DirectorySyncService(
    IDbSession session,
    TenantContextAccessor context,
    IConnectionFeatures connections,
    IDirectorySource directory,
    IEntitlementService entitlements,
    ITenantMemberships memberships,
    IAuthorizationProvisioning authz,
    ISessionRevocation sessions,
    Sso.IdentityProviderStore providers,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    ILogger<DirectorySyncService> logger)
{
    public const int AlertAfterFailures = 3;

    /// <summary>Fields the directory owns on the user record; read-only in the product (FR-014).</summary>
    public static readonly string[] OwnerFields = ["display_name", "email"];

    private const string SettingsSelect =
        """
        select connection_id, user_scope::text, sync_groups, schedule_minutes, deletion_threshold_pct, last_run_at, next_run_at, consecutive_failures
        from identity.directory_connection
        """;

    private static DirectorySettings MapSettings(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0),
        JsonSerializer.Deserialize<UserScope>(r.GetString(1), JsonDefaults.Options) ?? new UserScope(UserScope.All, []),
        r.GetFieldValue<string[]>(2),
        r.GetInt32(3),
        r.GetInt32(4),
        r.Get<DateTimeOffset?>("last_run_at"),
        r.GetFieldValue<DateTimeOffset>(6),
        r.GetInt32(7));

    public Task<DirectorySettings?> GetSettingsAsync(CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(SettingsSelect + " order by created_at limit 1", null, MapSettings, ct);

    public async Task<IReadOnlyList<SyncRun>> RecentRunsAsync(Guid connectionId, int limit, CancellationToken ct) =>
        await session.QueryAsync(
            """
            select id, started_at, finished_at, outcome, reason, created, updated, disabled, reenabled, groups, skipped
            from identity.directory_sync_run where connection_id = @c order by started_at desc limit @n
            """, new { c = connectionId, n = Math.Clamp(limit, 1, 50) },
            r => new SyncRun(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.Get<DateTimeOffset?>("finished_at"), r.GetString(3),
                r.Get<string>("reason"), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10)), ct);

    /// <summary>True when directory sync is configured on a usable connection. Sign-in then turns JIT off (US6 scenario 3).</summary>
    public async Task<bool> IsActiveAsync(CancellationToken ct)
    {
        var feature = await connections.FindAsync(ConnectionKinds.Microsoft365, Microsoft365Features.DirectorySync, ct);
        return feature is { Usable: true }
            && await session.ScalarAsync<bool>("select exists (select 1 from identity.directory_connection where connection_id = @c)", new { c = feature.ConnectionId }, ct);
    }

    public async Task<DirectorySettings> SaveSettingsAsync(DirectorySettingsRequest request, CancellationToken ct)
    {
        var feature = await connections.FindAsync(ConnectionKinds.Microsoft365, Microsoft365Features.DirectorySync, ct)
            ?? throw AppException.Conflict("conflict.directory.no_connection");
        var current = await session.QuerySingleOrDefaultAsync(SettingsSelect + " where connection_id = @c", new { c = feature.ConnectionId }, MapSettings, ct);

        var scope = request.UserScope ?? current?.UserScope ?? new UserScope(UserScope.All, []);
        var mode = scope.Mode?.Trim().ToLowerInvariant();
        if (mode is not (UserScope.All or UserScope.SelectedGroups))
        {
            throw AppException.Validation("userScope.mode", "validation.directory.scope_mode");
        }
        var scopeGroups = Guids("userScope.groups", scope.Groups ?? []);
        if (mode == UserScope.SelectedGroups && scopeGroups.Count == 0)
        {
            throw AppException.Validation("userScope.groups", "validation.directory.scope_groups_required");
        }
        var syncGroups = Guids("syncGroups", request.SyncGroups ?? current?.SyncGroups ?? []);
        var schedule = request.ScheduleMinutes ?? current?.ScheduleMinutes ?? 30;
        if (schedule is < 5 or > 1440)
        {
            throw AppException.Validation("scheduleMinutes", "validation.directory.schedule_range");
        }
        var threshold = request.DeletionThresholdPct ?? current?.DeletionThresholdPct ?? 10;
        if (threshold is < 1 or > 100)
        {
            throw AppException.Validation("deletionThresholdPct", "validation.directory.threshold_range");
        }

        var cleanScope = new UserScope(mode, mode == UserScope.All ? [] : scopeGroups);
        await session.ExecAsync(
            """
            insert into identity.directory_connection (tenant_id, id, connection_id, user_scope, sync_groups, schedule_minutes, deletion_threshold_pct)
            values (@t, @id, @c, @scope, @groups, @schedule, @threshold)
            on conflict (tenant_id, connection_id) do update set user_scope = excluded.user_scope, sync_groups = excluded.sync_groups,
              schedule_minutes = excluded.schedule_minutes, deletion_threshold_pct = excluded.deletion_threshold_pct, updated_at = now()
            """,
            new { t = context.RequireTenant(), id = Ids.New(), c = feature.ConnectionId, scope = Jsonb.From(cleanScope), groups = syncGroups.ToArray(), schedule, threshold }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DirectoryConnectionChanged, "connection", feature.ConnectionId.ToString(),
            Changes: new { scopeMode = mode, scopeGroups = cleanScope.Groups.Count, syncGroups = syncGroups.Count, scheduleMinutes = schedule, deletionThresholdPct = threshold, created = current is null }), ct);
        return (await session.QuerySingleOrDefaultAsync(SettingsSelect + " where connection_id = @c", new { c = feature.ConnectionId }, MapSettings, ct))!;
    }

    /// <summary>Runs the tenant's sync if its schedule is due. Called every minute by the worker.</summary>
    public async Task RunIfDueAsync(CancellationToken ct)
    {
        var due = await session.QueryAsync("select connection_id from identity.directory_connection where next_run_at <= now()", null, r => r.GetGuid(0), ct);
        foreach (var connectionId in due)
        {
            await RunAsync(connectionId, ct);
        }
    }

    /// <summary>One sync run. Locks the settings row so two workers never sync the same connection at once.</summary>
    public async Task<SyncRun?> RunAsync(Guid connectionId, CancellationToken ct)
    {
        context.SetActor(ActorType.System, null);
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select id, user_scope::text, sync_groups, schedule_minutes, deletion_threshold_pct, users_cursor, groups_cursor, consecutive_failures
            from identity.directory_connection where connection_id = @c for update skip locked
            """, new { c = connectionId },
            r => new RunSettings(r.GetGuid(0), JsonSerializer.Deserialize<UserScope>(r.GetString(1), JsonDefaults.Options)!, r.GetFieldValue<string[]>(2),
                r.GetInt32(3), r.GetInt32(4), r.Get<string>("users_cursor"), r.Get<string>("groups_cursor"), r.GetInt32(7)), ct);
        if (row is null)
        {
            return null;
        }

        var runId = Ids.New();
        await session.ExecAsync("insert into identity.directory_sync_run (tenant_id, id, connection_id, outcome) values (@t, @id, @c, 'running')",
            new { t = context.RequireTenant(), id = runId, c = connectionId }, ct);
        await session.ExecAsync("update identity.directory_connection set last_run_at = now(), next_run_at = now() + make_interval(mins => @m) where id = @id",
            new { id = row.Id, m = row.ScheduleMinutes }, ct);

        if (!await entitlements.IsEnabledAsync(Capabilities.IdentityDirectorySync, ct))
        {
            return await FinishAsync(runId, "failed", "not_in_package", new Counts(), ct);
        }
        var feature = await connections.FindAsync(ConnectionKinds.Microsoft365, Microsoft365Features.DirectorySync, ct);
        if (feature is not { Usable: true } || feature.ConnectionId != connectionId)
        {
            return await FailAsync(runId, row, connectionId, "connection_unusable", ct);
        }

        DirectoryChanges changes;
        try
        {
            changes = await directory.ReadChangesAsync(connectionId, row.UsersCursor, row.GroupsCursor, ct);
        }
        catch (DirectoryUnavailableException ex)
        {
            // Nothing is changed; existing users keep signing in through their identity provider (scenario 8).
            return await FailAsync(runId, row, connectionId, ex.Reason, ct);
        }

        var mirror = await LoadMirrorAsync(connectionId, ct);
        mirror.Apply(changes);
        var plan = await PlanAsync(connectionId, row.UserScope, mirror, ct);

        var activeLinked = plan.ActiveLinkedCount;
        if (plan.Disable.Count > 0 && activeLinked > 0 && plan.Disable.Count * 100.0 / activeLinked > row.DeletionThresholdPct)
        {
            // Too many accounts would lose access at once: apply nothing and keep the cursors, so the same changes are read again.
            await session.ExecAsync("update identity.directory_connection set consecutive_failures = 0 where id = @id", new { id = row.Id }, ct);
            await outbox.EnqueueAsync(new DirectorySyncNeedsAttention(connectionId, "threshold", 0), ct);
            return await FinishAsync(runId, "stopped_threshold", $"disable {plan.Disable.Count} of {activeLinked}", new Counts(), ct);
        }

        await mirror.SaveAsync(session, context.RequireTenant(), connectionId, ct);
        var counts = await ApplyAsync(connectionId, feature.ExternalTenantId, plan, ct);
        counts.Groups = await SyncGroupsAsync(connectionId, row.SyncGroups, mirror, ct);

        await session.ExecAsync(
            "update identity.directory_connection set users_cursor = @u, groups_cursor = @g, consecutive_failures = 0, updated_at = now() where id = @id",
            new { id = row.Id, u = changes.UsersCursor, g = changes.GroupsCursor }, ct);
        return await FinishAsync(runId, "succeeded", null, counts, ct);
    }

    private async Task<SyncRun> FailAsync(Guid runId, RunSettings row, Guid connectionId, string reason, CancellationToken ct)
    {
        var failures = row.ConsecutiveFailures + 1;
        await session.ExecAsync("update identity.directory_connection set consecutive_failures = @f where id = @id", new { id = row.Id, f = failures }, ct);
        if (failures == AlertAfterFailures)
        {
            await outbox.EnqueueAsync(new DirectorySyncNeedsAttention(connectionId, reason, failures), ct);
        }
        logger.LogWarning("Directory sync failed: {Reason} ({Failures} in a row)", reason, failures);
        return await FinishAsync(runId, "failed", reason, new Counts(), ct);
    }

    private async Task<SyncRun> FinishAsync(Guid runId, string outcome, string? reason, Counts c, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            update identity.directory_sync_run set finished_at = now(), outcome = @o, reason = @r, created = @cr, updated = @up,
              disabled = @di, reenabled = @re, groups = @gr, skipped = @sk where id = @id
            """, new { id = runId, o = outcome, r = reason, cr = c.Created, up = c.Updated, di = c.Disabled, re = c.Reenabled, gr = c.Groups, sk = c.Skipped }, ct);
        var now = clock.UtcNow;
        return new SyncRun(runId, now, now, outcome, reason, c.Created, c.Updated, c.Disabled, c.Reenabled, c.Groups, c.Skipped);
    }

    private async Task<Mirror> LoadMirrorAsync(Guid connectionId, CancellationToken ct)
    {
        var users = await session.QueryAsync("select directory_id, account_enabled, attributes::text from identity.directory_user where connection_id = @c",
            new { c = connectionId }, r => new MirrorUser(r.GetString(0), r.GetBoolean(1), JsonSerializer.Deserialize<MirrorAttributes>(r.GetString(2), JsonDefaults.Options)!), ct);
        var groups = await session.QueryAsync("select directory_id, display_name, members from identity.directory_group where connection_id = @c",
            new { c = connectionId }, r => new MirrorGroup(r.GetString(0), r.Get<string>("display_name"), r.GetFieldValue<string[]>(2).ToHashSet(StringComparer.Ordinal)), ct);
        return new Mirror(users.ToDictionary(u => u.Id, StringComparer.Ordinal), groups.ToDictionary(g => g.Id, StringComparer.Ordinal));
    }

    private async Task<Plan> PlanAsync(Guid connectionId, UserScope scope, Mirror mirror, CancellationToken ct)
    {
        var inScope = new HashSet<string>(StringComparer.Ordinal);
        if (scope.Mode == UserScope.All)
        {
            inScope.UnionWith(mirror.Users.Keys);
        }
        else
        {
            foreach (var g in scope.Groups)
            {
                if (mirror.Groups.TryGetValue(g, out var group))
                {
                    inScope.UnionWith(group.Members);
                }
            }
        }

        var links = await session.QueryAsync(
            """
            select l.directory_id, l.user_id, l.disabled_by_directory, u.display_name, u.email
            from identity.directory_user_link l join identity.user_account u on u.id = l.user_id where l.connection_id = @c
            """, new { c = connectionId }, r => new Link(r.GetString(0), r.GetGuid(1), r.GetBoolean(2), r.GetString(3), r.GetString(4)), ct);
        var linked = links.ToDictionary(l => l.DirectoryId, StringComparer.Ordinal);

        var plan = new Plan { ActiveLinkedCount = links.Count(l => !l.DisabledByDirectory) };
        foreach (var id in inScope)
        {
            if (!mirror.Users.TryGetValue(id, out var user) || !user.Enabled)
            {
                continue;
            }
            if (!linked.TryGetValue(id, out var link))
            {
                plan.Create.Add(user);
                continue;
            }
            if (link.DisabledByDirectory)
            {
                plan.Reenable.Add(link);
            }
            var name = DisplayName(user);
            var email = Email(user);
            if (name != link.DisplayName || (email is not null && email != link.Email))
            {
                plan.Update.Add((link, user));
            }
        }
        foreach (var link in links.Where(l => !l.DisabledByDirectory))
        {
            var gone = !mirror.Users.TryGetValue(link.DirectoryId, out var user) || !user.Enabled || !inScope.Contains(link.DirectoryId);
            if (gone)
            {
                plan.Disable.Add(link);
            }
        }
        return plan;
    }

    private async Task<Counts> ApplyAsync(Guid connectionId, string externalTenantId, Plan plan, CancellationToken ct)
    {
        var counts = new Counts();
        var tenantId = context.RequireTenant();
        var issuer = directory.IssuerFor(externalTenantId);
        var idp = await providers.GetEnabledAsync(ct);

        foreach (var user in plan.Create)
        {
            if (await CreateOrLinkAsync(tenantId, connectionId, issuer, idp?.Id, user, ct))
            {
                counts.Created++;
            }
            else
            {
                counts.Skipped++;
            }
        }

        foreach (var (link, user) in plan.Update)
        {
            var name = DisplayName(user);
            var email = Email(user) ?? link.Email;
            var emailTaken = email != link.Email && await session.ScalarAsync<bool>(
                "select exists (select 1 from identity.user_account where lower(email) = @e and id <> @u)", new { e = email, u = link.UserId }, ct);
            if (emailTaken)
            {
                email = link.Email;
                counts.Skipped++;
            }
            var changed = new List<string>();
            if (name != link.DisplayName) changed.Add("display_name");
            if (email != link.Email) changed.Add("email");
            if (changed.Count == 0)
            {
                continue;
            }
            await session.ExecAsync("update identity.user_account set display_name = @n, email = @e, owner_fields = @f where id = @u",
                new { n = name, e = email, f = OwnerFields, u = link.UserId }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.UserSynced, "user", link.UserId.ToString(), Changes: new { change = "updated", fields = changed }), ct);
            await outbox.EnqueueAsync(new UserProfileSynced(link.UserId, changed), ct);
            counts.Updated++;
        }

        foreach (var link in plan.Reenable)
        {
            await memberships.SetStatusAsync(link.UserId, MembershipStatus.Active, ct);
            await session.ExecAsync("update identity.directory_user_link set disabled_by_directory = false, updated_at = now() where user_id = @u", new { u = link.UserId }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ReenabledByDirectory, "user", link.UserId.ToString()), ct);
            counts.Reenabled++;
        }

        foreach (var link in plan.Disable)
        {
            if (await authz.IsLastTenantAdministratorAsync(link.UserId, ct))
            {
                // The tenant must keep an administrator; the run log shows it as skipped.
                logger.LogWarning("Kept the tenant's last administrator enabled although the directory disabled the account");
                counts.Skipped++;
                continue;
            }
            await memberships.SetStatusAsync(link.UserId, MembershipStatus.Disabled, ct);
            await session.ExecAsync("update identity.directory_user_link set disabled_by_directory = true, updated_at = now() where user_id = @u", new { u = link.UserId }, ct);
            await sessions.RevokeUserSessionsAsync(link.UserId, "directory_disabled", ct);
            await outbox.EnqueueAsync(new TenantMembershipDisabled(link.UserId), ct);
            await outbox.EnqueueAsync(new UserDeprovisioned(link.UserId, "directory"), ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.DisabledByDirectory, "user", link.UserId.ToString()), ct);
            counts.Disabled++;
        }
        return counts;
    }

    /// <summary>
    /// Creates the user for a directory account, or links an existing user that already signed in with the same issuer
    /// and immutable ID. Never links by email: an account that already uses the email is skipped (FR-003).
    /// </summary>
    private async Task<bool> CreateOrLinkAsync(Guid tenantId, Guid connectionId, string issuer, Guid? idpConfigId, MirrorUser user, CancellationToken ct)
    {
        var existing = await session.ScalarAsync<Guid?>(
            "select user_id from identity.external_identity where issuer = @i and subject = @s", new { i = issuer, s = user.Id }, ct);
        var email = Email(user);
        if (existing is null)
        {
            if (email is null)
            {
                return false;
            }
            var emailTaken = await session.ScalarAsync<bool>("select exists (select 1 from identity.user_account where lower(email) = @e)", new { e = email }, ct);
            if (emailTaken)
            {
                return false;
            }
            existing = Ids.New();
            await session.ExecAsync(
                """
                insert into identity.user_account (tenant_id, id, display_name, email, status, created_via, owner_fields)
                values (@t, @id, @name, @email, 'active', 'directory', @f)
                """, new { t = tenantId, id = existing, name = DisplayName(user), email, f = OwnerFields }, ct);
            await session.ExecAsync(
                "insert into identity.external_identity (tenant_id, id, idp_config_id, issuer, subject, user_id) values (@t, @id, @idp, @i, @s, @u)",
                new { t = tenantId, id = Ids.New(), idp = idpConfigId, i = issuer, s = user.Id, u = existing }, ct);
            await memberships.CreateAsync(existing.Value, MembershipTypes.Employee, ct);
            await outbox.EnqueueAsync(new UserProvisioned(existing.Value, "directory"), ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.UserSynced, "user", existing.ToString(), Changes: new { change = "created" }), ct);
        }
        else
        {
            if (await session.ScalarAsync<bool>("select exists (select 1 from identity.directory_user_link where user_id = @u)", new { u = existing }, ct))
            {
                return false;
            }
            await session.ExecAsync("update identity.user_account set owner_fields = @f where id = @u", new { f = OwnerFields, u = existing }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.UserSynced, "user", existing.ToString(), Changes: new { change = "linked" }), ct);
        }

        await session.ExecAsync(
            "insert into identity.directory_user_link (tenant_id, connection_id, directory_id, user_id) values (@t, @c, @d, @u)",
            new { t = tenantId, c = connectionId, d = user.Id, u = existing }, ct);
        return true;
    }

    /// <summary>
    /// Keeps selected directory groups in step as groups owned by the directory, and applies the identity provider's
    /// group-to-role mapping to them (scenario 6). A group that is no longer selected, or removed in the directory, is
    /// emptied, so its roles stop applying. Product groups are never changed.
    /// </summary>
    private async Task<int> SyncGroupsAsync(Guid connectionId, IReadOnlyList<string> selected, Mirror mirror, CancellationToken ct)
    {
        var userIds = (await session.QueryAsync("select directory_id, user_id from identity.directory_user_link where connection_id = @c",
            new { c = connectionId }, r => (r.GetString(0), r.GetGuid(1)), ct)).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        var known = (await session.QueryAsync("select directory_id from identity.directory_group_link where connection_id = @c",
            new { c = connectionId }, r => r.GetString(0), ct)).ToHashSet(StringComparer.Ordinal);
        var map = (await providers.GetEnabledAsync(ct))?.GroupRoleMap ?? [];
        var tenantId = context.RequireTenant();

        var synced = 0;
        foreach (var gid in selected)
        {
            if (!mirror.Groups.TryGetValue(gid, out var group))
            {
                continue;
            }
            var members = group.Members.Where(userIds.ContainsKey).Select(m => userIds[m]).ToList();
            var groupId = await authz.SyncDirectoryGroupAsync(GroupKey(gid), group.DisplayName ?? gid, members, ct);
            await session.ExecAsync(
                """
                insert into identity.directory_group_link (tenant_id, connection_id, directory_id, group_id) values (@t, @c, @d, @g)
                on conflict (tenant_id, connection_id, directory_id) do update set group_id = excluded.group_id
                """, new { t = tenantId, c = connectionId, d = gid, g = groupId }, ct);
            foreach (var m in map.Where(m => m.GroupId == gid))
            {
                await authz.AssignBuiltInRoleToGroupAsync(groupId, m.RoleKey, Sql.ParseEnum<ScopeType>(m.ScopeType), m.ScopeId, ct);
            }
            synced++;
        }

        foreach (var gid in known.Where(g => !selected.Contains(g) || !mirror.Groups.ContainsKey(g)))
        {
            var name = mirror.Groups.TryGetValue(gid, out var group) ? group.DisplayName ?? gid : gid;
            await authz.SyncDirectoryGroupAsync(GroupKey(gid), name, [], ct);
            await session.ExecAsync("delete from identity.directory_group_link where connection_id = @c and directory_id = @d", new { c = connectionId, d = gid }, ct);
        }
        return synced;
    }

    /// <summary>Removes directory sync data when its connection is deleted (spec 006 US5 scenario 8). Users and their content stay.</summary>
    public async Task RemoveConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        foreach (var table in new[] { "directory_group_link", "directory_user_link", "directory_user", "directory_group", "directory_sync_run", "directory_connection" })
        {
            await session.ExecAsync($"delete from identity.{table} where connection_id = @c", new { c = connectionId }, ct);
        }
    }

    public static string GroupKey(string directoryGroupId) => "entra:" + directoryGroupId;

    private static string DisplayName(MirrorUser u) =>
        string.IsNullOrWhiteSpace(u.Attributes.DisplayName) ? (Email(u) ?? u.Id) : u.Attributes.DisplayName.Trim();

    private static string? Email(MirrorUser u)
    {
        var value = !string.IsNullOrWhiteSpace(u.Attributes.Email) ? u.Attributes.Email : u.Attributes.UserPrincipalName;
        return string.IsNullOrWhiteSpace(value) || !value.Contains('@', StringComparison.Ordinal) ? null : value.Trim().ToLowerInvariant();
    }

    private static List<string> Guids(string field, IEnumerable<string> values)
    {
        var list = new List<string>();
        foreach (var v in values)
        {
            if (!Guid.TryParse(v, out var g))
            {
                throw AppException.Validation(field, "validation.directory.group_id");
            }
            list.Add(g.ToString("D"));
        }
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private sealed record RunSettings(Guid Id, UserScope UserScope, string[] SyncGroups, int ScheduleMinutes, int DeletionThresholdPct,
        string? UsersCursor, string? GroupsCursor, int ConsecutiveFailures);

    private sealed record Link(string DirectoryId, Guid UserId, bool DisabledByDirectory, string DisplayName, string Email);

    private sealed class Plan
    {
        public int ActiveLinkedCount { get; init; }
        public List<MirrorUser> Create { get; } = [];
        public List<(Link Link, MirrorUser User)> Update { get; } = [];
        public List<Link> Reenable { get; } = [];
        public List<Link> Disable { get; } = [];
    }

    private sealed class Counts
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Disabled { get; set; }
        public int Reenabled { get; set; }
        public int Groups { get; set; }
        public int Skipped { get; set; }
    }
}

/// <summary>Directory values kept for an account. Only mapped attributes are read (scenario 7).</summary>
internal sealed record MirrorAttributes(string? DisplayName, string? Email, string? UserPrincipalName, string? JobTitle, string? Office,
    string? Phone, string? EmployeeId, string? PreferredLanguage);

internal sealed record MirrorUser(string Id, bool Enabled, MirrorAttributes Attributes);

internal sealed record MirrorGroup(string Id, string? DisplayName, HashSet<string> Members);

/// <summary>The stored mirror plus the latest changes, held in memory until the run is allowed to apply them.</summary>
internal sealed class Mirror(Dictionary<string, MirrorUser> users, Dictionary<string, MirrorGroup> groups)
{
    private readonly HashSet<string> _changedUsers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedUsers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changedGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedGroups = new(StringComparer.Ordinal);

    public Dictionary<string, MirrorUser> Users { get; } = users;
    public Dictionary<string, MirrorGroup> Groups { get; } = groups;

    public void Apply(DirectoryChanges changes)
    {
        foreach (var u in changes.Users)
        {
            if (u.Removed)
            {
                Users.Remove(u.ExternalId);
                _removedUsers.Add(u.ExternalId);
                _changedUsers.Remove(u.ExternalId);
                continue;
            }
            Users.TryGetValue(u.ExternalId, out var previous);
            var a = previous?.Attributes;
            var attributes = new MirrorAttributes(u.DisplayName ?? a?.DisplayName, u.Email ?? a?.Email, u.UserPrincipalName ?? a?.UserPrincipalName,
                u.JobTitle ?? a?.JobTitle, u.Office ?? a?.Office, u.Phone ?? a?.Phone, u.EmployeeId ?? a?.EmployeeId, u.PreferredLanguage ?? a?.PreferredLanguage);
            // An account whose state was never reported is treated as disabled, so access is never granted by default.
            Users[u.ExternalId] = new MirrorUser(u.ExternalId, u.AccountEnabled ?? previous?.Enabled ?? false, attributes);
            _changedUsers.Add(u.ExternalId);
            _removedUsers.Remove(u.ExternalId);
        }

        foreach (var g in changes.Groups)
        {
            if (g.Removed)
            {
                Groups.Remove(g.ExternalId);
                _removedGroups.Add(g.ExternalId);
                _changedGroups.Remove(g.ExternalId);
                continue;
            }
            var group = Groups.TryGetValue(g.ExternalId, out var existing)
                ? existing with { DisplayName = g.DisplayName ?? existing.DisplayName }
                : new MirrorGroup(g.ExternalId, g.DisplayName, new HashSet<string>(StringComparer.Ordinal));
            group.Members.ExceptWith(g.MembersRemoved);
            group.Members.UnionWith(g.MembersAdded);
            Groups[g.ExternalId] = group;
            _changedGroups.Add(g.ExternalId);
            _removedGroups.Remove(g.ExternalId);
        }
    }

    public async Task SaveAsync(IDbSession session, Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        if (_removedUsers.Count > 0)
        {
            await session.ExecAsync("delete from identity.directory_user where connection_id = @c and directory_id = any(@ids)",
                new { c = connectionId, ids = _removedUsers.ToArray() }, ct);
        }
        foreach (var id in _changedUsers)
        {
            var u = Users[id];
            await session.ExecAsync(
                """
                insert into identity.directory_user (tenant_id, connection_id, directory_id, account_enabled, attributes) values (@t, @c, @d, @e, @a)
                on conflict (tenant_id, connection_id, directory_id) do update set account_enabled = excluded.account_enabled, attributes = excluded.attributes
                """, new { t = tenantId, c = connectionId, d = id, e = u.Enabled, a = Jsonb.From(u.Attributes) }, ct);
        }
        if (_removedGroups.Count > 0)
        {
            await session.ExecAsync("delete from identity.directory_group where connection_id = @c and directory_id = any(@ids)",
                new { c = connectionId, ids = _removedGroups.ToArray() }, ct);
        }
        foreach (var id in _changedGroups)
        {
            var g = Groups[id];
            await session.ExecAsync(
                """
                insert into identity.directory_group (tenant_id, connection_id, directory_id, display_name, members) values (@t, @c, @d, @n, @m)
                on conflict (tenant_id, connection_id, directory_id) do update set display_name = excluded.display_name, members = excluded.members
                """, new { t = tenantId, c = connectionId, d = id, n = g.DisplayName, m = g.Members.Order(StringComparer.Ordinal).ToArray() }, ct);
        }
    }
}
