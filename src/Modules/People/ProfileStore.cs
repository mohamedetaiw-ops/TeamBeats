using System.Text.Json;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.People;

/// <summary>One stored profile.</summary>
public sealed record ProfileRow(
    Guid UserId,
    string DisplayName,
    IReadOnlyDictionary<string, string> Names,
    string Email,
    string MembershipType,
    bool Active,
    Guid? PrimaryUnitId,
    IReadOnlyList<Guid> UnitIds,
    string? DirectoryId,
    IReadOnlyDictionary<string, string> DirectoryValues,
    IReadOnlyDictionary<string, string> OwnValues,
    IReadOnlyDictionary<string, string> AdminValues,
    IReadOnlyDictionary<string, string> HrValues,
    string? LocationKey,
    DateOnly? HireDate,
    DateOnly FirstSeen,
    bool NewcomerOptOut,
    int Version)
{
    /// <summary>The value of a field under its current source, or null when the source holds none.</summary>
    public string? Value(FieldPolicy policy)
    {
        if (policy.Key == ProfileFields.Email)
        {
            return Email;
        }
        var values = policy.Source switch
        {
            FieldSources.Directory => DirectoryValues,
            FieldSources.Person => OwnValues,
            FieldSources.Hr => HrValues,
            _ => AdminValues,
        };
        return values.TryGetValue(policy.Key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }
}

public sealed record PeopleSettings(int NewcomerDays, bool AllowNewcomerOptOut, string LocationAttribute, DateTimeOffset? DirectorySyncedAt, int Version);

/// <summary>
/// Profile storage and the account read model. Names, emails, status and units belong to Identity and Tenancy; they
/// are copied here through their contracts whenever the hierarchy version moves (membership and unit changes bump it)
/// and when directory sync reports changes, so search can run in this module's own tables (constitution: no reads
/// of another module's tables).
/// </summary>
public sealed class ProfileStore(IDbSession session, IUserDirectory users, ITenantMemberships memberships, IOrganizationHierarchy hierarchy)
{
    private const string Columns =
        """
        user_id, display_name, names::text, email, membership_type, active, primary_unit_id, unit_ids, directory_id,
        directory_values::text, own_values::text, admin_values::text, location_key, hire_date, first_seen, newcomer_opt_out, version, hr_values::text
        """;

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private IReadOnlyList<FieldPolicy>? _policies;

    private static IReadOnlyDictionary<string, string> Dict(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    private static ProfileRow Map(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), Dict(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.Get<Guid?>("primary_unit_id"),
        r.GetFieldValue<Guid[]>(7), r.Get<string>("directory_id"), Dict(r.GetString(9)), Dict(r.GetString(10)), Dict(r.GetString(11)), Dict(r.GetString(17)),
        r.Get<string>("location_key"), r.IsDBNull(13) ? null : r.GetFieldValue<DateOnly>(13), r.GetFieldValue<DateOnly>(14), r.GetBoolean(15), r.GetInt32(16));

    public async Task<ProfileRow?> GetAsync(Guid userId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from people.profile where user_id = @u", new { u = userId }, Map, ct);

    public async Task<IReadOnlyList<ProfileRow>> GetManyAsync(IEnumerable<Guid> userIds, CancellationToken ct) =>
        await session.QueryAsync($"select {Columns} from people.profile where user_id = any(@ids)", new { ids = userIds.Distinct().ToArray() }, Map, ct);

    public async Task<IReadOnlyList<ProfileRow>> QueryAsync(string where, object? args, CancellationToken ct) =>
        await session.QueryAsync($"select {Columns} from people.profile where {where}", args, Map, ct);

    public async Task<PeopleSettings> SettingsAsync(CancellationToken ct)
    {
        await session.ExecAsync("insert into people.settings (tenant_id) values (platform.current_tenant_id()) on conflict do nothing", null, ct);
        return (await session.QuerySingleOrDefaultAsync(
            "select newcomer_days, allow_newcomer_opt_out, location_attribute, directory_synced_at, version from people.settings", null,
            r => new PeopleSettings(r.GetInt32(0), r.GetBoolean(1), r.GetString(2), r.Get<DateTimeOffset?>("directory_synced_at"), r.GetInt32(4)), ct))!;
    }

    /// <summary>The effective policy: stored choices over the section 6 defaults, then custom fields in order.</summary>
    public async Task<IReadOnlyList<FieldPolicy>> PoliciesAsync(CancellationToken ct)
    {
        if (_policies is not null)
        {
            return _policies;
        }
        var stored = await session.QueryAsync("select field_key, source, visibility, is_custom, names::text, position from people.field_policy", null,
            r => new FieldPolicy(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), Dict(r.GetString(4)), r.GetInt32(5)), ct);
        var byKey = stored.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var result = ProfileFields.Defaults().Select(d => byKey.TryGetValue(d.Key, out var s) ? s with { Custom = false, Position = d.Position } : d).ToList();
        result.AddRange(stored.Where(p => p.Custom).OrderBy(p => p.Position).ThenBy(p => p.Key, StringComparer.Ordinal));
        return _policies = result;
    }

    public void ForgetPolicies() => _policies = null;

    /// <summary>
    /// Brings names, emails, status and units up to date when the hierarchy version has moved since the last refresh.
    /// Concurrent callers wait on the settings row, then find the work done.
    /// </summary>
    public async Task EnsureFreshAsync(CancellationToken ct)
    {
        await SettingsAsync(ct);
        var version = await hierarchy.GetVersionAsync(ct);
        var stored = await session.ScalarAsync<long>("select hierarchy_version from people.settings", null, ct);
        if (stored == version)
        {
            return;
        }
        stored = await session.ScalarAsync<long>("select hierarchy_version from people.settings for update", null, ct);
        if (stored == version)
        {
            return;
        }
        await RefreshAccountsAsync(ct);
        await session.ExecAsync("update people.settings set hierarchy_version = @v", new { v = version }, ct);
    }

    /// <summary>Refreshes one person's account values now, for example after directory sync renamed them.</summary>
    public Task RefreshAsync(Guid userId, CancellationToken ct) => RefreshAccountsAsync(ct, userId);

    /// <summary>Refreshes every person's account values now, for a full search rebuild.</summary>
    public Task RefreshAllAsync(CancellationToken ct) => RefreshAccountsAsync(ct);

    private async Task RefreshAccountsAsync(CancellationToken ct, Guid? only = null)
    {
        var accounts = only is { } id ? (await users.GetAsync(id, ct) is { } one ? [one] : []) : await users.ListAllAsync(ct);
        var tenantMemberships = only is { } u ? (await memberships.GetAsync(u, ct) is { } m ? [m] : []) : await memberships.ListAsync(ct);
        var orgMemberships = only is { } x
            ? (await hierarchy.GetActiveMembershipsAsync(x, ct)).Select(m => new UserOrgMembership(x, m.OrgUnitId, m.IsPrimary)).ToList()
            : await hierarchy.ListActiveMembershipsAsync(ct);
        var byUser = orgMemberships.GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.ToList());
        var accountById = accounts.ToDictionary(a => a.Id);

        var rows = new List<object>();
        foreach (var membership in tenantMemberships)
        {
            if (!accountById.TryGetValue(membership.UserId, out var account))
            {
                continue;
            }
            var units = byUser.GetValueOrDefault(membership.UserId) ?? [];
            var primary = units.FirstOrDefault(m => m.IsPrimary)?.OrgUnitId ?? units.Select(m => (Guid?)m.OrgUnitId).OrderBy(g => g).FirstOrDefault();
            rows.Add(new
            {
                userId = account.Id,
                displayName = account.DisplayName,
                email = account.Email,
                membershipType = membership.MembershipType,
                active = membership.Status == MembershipStatus.Active && account.Status == "active",
                primaryUnitId = primary,
                unitIds = units.Select(m => m.OrgUnitId).Distinct().ToArray(),
                searchName = SearchText.Normalize(account.DisplayName),
                searchSkeleton = SearchText.Skeletons(account.DisplayName),
            });
        }

        await session.ExecAsync(
            """
            insert into people.profile (tenant_id, user_id, display_name, email, membership_type, active, primary_unit_id, unit_ids, search_name, search_skeleton)
            select platform.current_tenant_id(), r."userId", r."displayName", r.email, r."membershipType", r.active, r."primaryUnitId",
                   coalesce(r."unitIds", '{}'), r."searchName", r."searchSkeleton"
            from jsonb_to_recordset(@rows) as r("userId" uuid, "displayName" text, email text, "membershipType" text, active boolean,
                 "primaryUnitId" uuid, "unitIds" uuid[], "searchName" text, "searchSkeleton" text)
            on conflict (tenant_id, user_id) do update set
              display_name = excluded.display_name, email = excluded.email, membership_type = excluded.membership_type, active = excluded.active,
              primary_unit_id = excluded.primary_unit_id, unit_ids = excluded.unit_ids, search_name = excluded.search_name,
              search_skeleton = excluded.search_skeleton, updated_at = now()
            where (people.profile.display_name, people.profile.email, people.profile.membership_type, people.profile.active,
                   people.profile.primary_unit_id, people.profile.unit_ids)
              is distinct from (excluded.display_name, excluded.email, excluded.membership_type, excluded.active, excluded.primary_unit_id, excluded.unit_ids)
            """, new { rows = Jsonb.From(rows) }, ct);

        if (only is null)
        {
            // Accounts that are gone, or no longer members, stay as "no longer available" (US1 scenario 4).
            await session.ExecAsync("update people.profile set active = false, updated_at = now() where active and not (user_id = any(@ids))",
                new { ids = tenantMemberships.Where(m => accountById.ContainsKey(m.UserId)).Select(m => m.UserId).ToArray() }, ct);
        }
        else if (rows.Count == 0)
        {
            await session.ExecAsync("update people.profile set active = false, updated_at = now() where user_id = @u and active", new { u = only.Value }, ct);
        }
    }

    /// <summary>Normalized values of the searchable fields under the current sources (phones as digits).</summary>
    public static Dictionary<string, string> SearchFields(ProfileRow row, IReadOnlyList<FieldPolicy> policies)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var policy in policies.Where(p => ProfileFields.Searchable.Contains(p.Key) && p.Key != ProfileFields.Email))
        {
            if (row.Value(policy) is { } value)
            {
                result[policy.Key] = ProfileFields.ByKey[policy.Key].IsPhone ? SearchText.Digits(value) : SearchText.Normalize(value);
            }
        }
        return result;
    }

    /// <summary>Rebuilds the stored search values of every profile, after a source change in the policy.</summary>
    public async Task RebuildSearchFieldsAsync(CancellationToken ct)
    {
        var policies = await PoliciesAsync(ct);
        var rows = await QueryAsync("true", null, ct);
        await WriteSearchFieldsAsync(rows, policies, ct);
    }

    public async Task WriteSearchFieldsAsync(IEnumerable<ProfileRow> rows, IReadOnlyList<FieldPolicy> policies, CancellationToken ct) =>
        await session.ExecAsync(
            """
            update people.profile p set search_fields = r."searchFields"
            from jsonb_to_recordset(@rows) as r("userId" uuid, "searchFields" jsonb)
            where p.user_id = r."userId" and p.search_fields is distinct from r."searchFields"
            """, new { rows = Jsonb.From(rows.Select(r => new { userId = r.UserId, searchFields = SearchFields(r, policies) })) }, ct);

    public static IReadOnlyDictionary<string, string> NoValues => Empty;
}
