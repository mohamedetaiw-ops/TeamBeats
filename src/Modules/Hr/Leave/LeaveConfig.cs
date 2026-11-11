using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Leave;

public static class LeaveUnits
{
    public const string Day = "day";
    public const string Hour = "hour";

    /// <summary>The working day an hour-based request is measured against on the team calendar (R2 plan default).</summary>
    public const decimal HoursPerDay = 8m;
}

public sealed record LeaveTypeView(
    Guid Id,
    string Key,
    IReadOnlyDictionary<string, string> Names,
    string Unit,
    bool HalfDays,
    bool Paid,
    bool Sensitive,
    decimal? DocumentAfterDays,
    int NoticeDays,
    decimal? MaxDaysPerRequest,
    bool NegativeAllowed,
    decimal NegativeMax,
    IReadOnlyList<string>? MembershipTypes,
    decimal CarryOverMax,
    int YearStartMonth,
    bool CancelNeedsApproval,
    bool Active,
    int Version)
{
    public string ETag => LeaveConfig.ETag(Version);
}

public sealed record LeaveTypeRequest(
    IReadOnlyDictionary<string, string>? Names,
    string? Unit,
    bool? HalfDays,
    bool? Paid,
    bool? Sensitive,
    decimal? DocumentAfterDays,
    int? NoticeDays,
    decimal? MaxDaysPerRequest,
    bool? NegativeAllowed,
    decimal? NegativeMax,
    IReadOnlyList<string>? MembershipTypes,
    decimal? CarryOverMax,
    int? YearStartMonth,
    bool? CancelNeedsApproval,
    bool? Active);

public sealed record LeavePolicyView(Guid Id, string TypeKey, Guid? OrgUnitId, IReadOnlyList<LeaveBand> Bands, string Accrual, int Version, bool CanEdit)
{
    public string ETag => LeaveConfig.ETag(Version);
}

public sealed record LeavePolicyRequest(string? TypeKey, Guid? OrgUnitId, IReadOnlyList<LeaveBand>? Bands, string? Accrual);

/// <summary>A leave type as the rest of the module reads it.</summary>
internal sealed record LeaveTypeRow(
    Guid Id, string Key, Dictionary<string, string> Names, string Unit, bool HalfDays, bool Paid, bool Sensitive, decimal? DocumentAfterDays, int NoticeDays,
    decimal? MaxDaysPerRequest, bool NegativeAllowed, decimal NegativeMax, string[]? MembershipTypes, decimal CarryOverMax, int YearStartMonth,
    bool CancelNeedsApproval, bool Active, int Version)
{
    public LeaveTypeView View() => new(Id, Key, Names, Unit, HalfDays, Paid, Sensitive, DocumentAfterDays, NoticeDays, MaxDaysPerRequest, NegativeAllowed,
        NegativeMax, MembershipTypes, CarryOverMax, YearStartMonth, CancelNeedsApproval, Active, Version);

    public bool Eligible(string membershipType) => MembershipTypes is not { Length: > 0 } list || list.Contains(membershipType, StringComparer.Ordinal);
}

internal sealed record LeavePolicyRow(Guid Id, Guid TypeId, Guid? OrgUnitId, IReadOnlyList<LeaveBand> Bands, string Accrual, int Version);

/// <summary>
/// Leave types and leave policies (spec 022 FR-001), versioned and validated configuration (constitution XXIX). Types
/// are tenant-wide and managed with <c>hr.manage</c> at tenant scope; a policy for an org unit applies to its subtree,
/// the nearest ancestor's policy wins, and it is managed by holders of <c>hr.manage</c> on that unit.
/// </summary>
public sealed partial class LeaveConfig(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOrganizationHierarchy hierarchy)
{
    public const int MaxTypes = 50;
    public const int MaxBands = 20;

    public static string ETag(int version) => $"\"v{version}\"";

    internal const string TypeColumns =
        """
        id, key, names::text, unit, half_days, paid, sensitive, document_after_days, notice_days, max_days_per_request, negative_allowed, negative_max,
        membership_types, carry_over_max, year_start_month, cancel_needs_approval, active, version
        """;

    internal static LeaveTypeRow ReadType(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? [], r.GetString(3), r.GetBoolean(4),
        r.GetBoolean(5), r.GetBoolean(6), r.Get<decimal?>("document_after_days"), r.GetInt32(8), r.Get<decimal?>("max_days_per_request"), r.GetBoolean(10),
        r.GetDecimal(11), r.Get<string[]?>("membership_types"), r.GetDecimal(13), r.GetInt16(14), r.GetBoolean(15), r.GetBoolean(16), r.GetInt32(17));

    internal Task<List<LeaveTypeRow>> TypesAsync(CancellationToken ct) =>
        session.QueryAsync($"select {TypeColumns} from hr.leave_type order by key", null, ReadType, ct);

    internal async Task<LeaveTypeRow?> TypeAsync(Guid id, CancellationToken ct) =>
        (await session.QueryAsync($"select {TypeColumns} from hr.leave_type where id = @id", new { id }, ReadType, ct)).SingleOrDefault();

    internal async Task<LeaveTypeRow?> TypeByKeyAsync(string key, CancellationToken ct) =>
        (await session.QueryAsync($"select {TypeColumns} from hr.leave_type where key = @key", new { key }, ReadType, ct)).SingleOrDefault();

    internal Task<List<LeavePolicyRow>> PoliciesAsync(Guid? typeId, CancellationToken ct) =>
        session.QueryAsync(
            "select id, leave_type_id, org_unit_id, bands::text, accrual, version from hr.leave_policy where (@typeId::uuid is null or leave_type_id = @typeId)",
            new { typeId },
            r => new LeavePolicyRow(r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("org_unit_id"),
                JsonSerializer.Deserialize<List<LeaveBand>>(r.GetString(3), JsonDefaults.Options) ?? [], r.GetString(4), r.GetInt32(5)),
            ct);

    /// <summary>The policy of a type that applies at a unit: the nearest ancestor's, else the tenant's, else none.</summary>
    internal static LeavePolicyRow? Applicable(IReadOnlyList<LeavePolicyRow> policies, IReadOnlyList<Guid> pathFromRoot) =>
        pathFromRoot.Reverse().Select(u => policies.FirstOrDefault(p => p.OrgUnitId == u)).FirstOrDefault(p => p is not null)
        ?? policies.FirstOrDefault(p => p.OrgUnitId is null);

    // ---- Types ----

    /// <summary>Types for the request form: the active ones; HR officers at tenant scope may ask for all.</summary>
    public async Task<IReadOnlyList<LeaveTypeView>> ListTypesAsync(bool all, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        context.RequireUser();
        if (all && !(await authz.CheckAsync(Permissions.HrManage, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        return (await TypesAsync(ct)).Where(t => all || t.Active).Select(t => t.View()).ToList();
    }

    /// <summary>Creates the type with this key, or replaces it with <c>If-Match</c>. A type is never deleted; it is made inactive.</summary>
    public async Task<LeaveTypeView> PutTypeAsync(string key, string? ifMatch, LeaveTypeRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        await authz.DemandAsync(Permissions.HrManage, AuthzTarget.Tenant, ct);
        var errors = new ValidationErrors();
        if (!KeyPattern().IsMatch(key))
        {
            errors.Add("key", "validation.leave.type_key");
        }
        var names = CleanNames(request.Names);
        if (names is null)
        {
            errors.Add("names", "validation.leave.type_names");
        }
        var unit = request.Unit ?? LeaveUnits.Day;
        if (unit is not (LeaveUnits.Day or LeaveUnits.Hour))
        {
            errors.Add("unit", "validation.leave.unit");
        }
        if (request.DocumentAfterDays is < 0 or > LeaveMath.MaxDaysPerYear)
        {
            errors.Add("documentAfterDays", "validation.leave.number");
        }
        if (request.NoticeDays is < 0 or > 365)
        {
            errors.Add("noticeDays", "validation.leave.number");
        }
        if (request.MaxDaysPerRequest is <= 0 or > LeaveMath.MaxDaysPerYear)
        {
            errors.Add("maxDaysPerRequest", "validation.leave.number");
        }
        if (request.NegativeMax is < 0 or > LeaveMath.MaxDaysPerYear)
        {
            errors.Add("negativeMax", "validation.leave.number");
        }
        if (request.CarryOverMax is < 0 or > LeaveMath.MaxDaysPerYear)
        {
            errors.Add("carryOverMax", "validation.leave.number");
        }
        if (request.YearStartMonth is < 1 or > 12)
        {
            errors.Add("yearStartMonth", "validation.leave.year_start_month");
        }
        var membership = request.MembershipTypes is { Count: > 0 } m ? m.Distinct(StringComparer.Ordinal).ToArray() : null;
        if (membership?.Any(t => !Tenancy.Contracts.MembershipTypes.All.Contains(t)) == true)
        {
            errors.Add("membershipTypes", "validation.leave.membership_types");
        }
        errors.ThrowIfAny();

        var existing = await TypeByKeyAsync(key, ct);
        var now = clock.UtcNow;
        var args = new
        {
            id = existing?.Id ?? Ids.New(), key, names = Jsonb.From(names!), unit, half = request.HalfDays ?? true, paid = request.Paid ?? true,
            sensitive = request.Sensitive ?? true, doc = request.DocumentAfterDays, notice = request.NoticeDays ?? 0, max = request.MaxDaysPerRequest,
            negative = request.NegativeAllowed ?? false, negativeMax = request.NegativeMax ?? 0m, membership, carry = request.CarryOverMax ?? 0m,
            startMonth = (short)(request.YearStartMonth ?? 1), cancelApproval = request.CancelNeedsApproval ?? false, active = request.Active ?? true,
            now, actor = context.ActorId,
        };
        if (existing is null)
        {
            if (await session.ScalarAsync<long>("select count(*) from hr.leave_type", null, ct) >= MaxTypes)
            {
                throw AppException.Conflict("conflict.leave.too_many_types");
            }
            await session.ExecAsync(
                """
                insert into hr.leave_type (tenant_id, id, key, names, unit, half_days, paid, sensitive, document_after_days, notice_days, max_days_per_request,
                  negative_allowed, negative_max, membership_types, carry_over_max, year_start_month, cancel_needs_approval, active, created_at, updated_at, updated_by)
                values (platform.current_tenant_id(), @id, @key, @names, @unit, @half, @paid, @sensitive, @doc, @notice, @max, @negative, @negativeMax, @membership,
                  @carry, @startMonth, @cancelApproval, @active, @now, @now, @actor)
                """, args, ct);
        }
        else
        {
            if (ifMatch != ETag(existing.Version))
            {
                throw new AppException(ProblemCodes.Conflict, 409, "conflict.leave.changed", null,
                    new Dictionary<string, object?> { ["etag"] = ETag(existing.Version) });
            }
            if (existing.Unit != unit && await session.ScalarAsync<bool>("select exists (select 1 from hr.leave_ledger where leave_type_id = @id)", new { id = existing.Id }, ct))
            {
                throw AppException.Validation("unit", "validation.leave.unit_in_use");
            }
            await session.ExecAsync(
                """
                update hr.leave_type set names = @names, unit = @unit, half_days = @half, paid = @paid, sensitive = @sensitive, document_after_days = @doc,
                  notice_days = @notice, max_days_per_request = @max, negative_allowed = @negative, negative_max = @negativeMax, membership_types = @membership,
                  carry_over_max = @carry, year_start_month = @startMonth, cancel_needs_approval = @cancelApproval, active = @active, version = version + 1,
                  updated_at = @now, updated_by = @actor
                where id = @id
                """, args, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLeaveTypeChanged, "leave_type", key,
            Changes: new { operation = existing is null ? "created" : "updated", args.names, unit, args.sensitive, args.carry, args.negative, args.active }), ct);
        return (await TypeByKeyAsync(key, ct))!.View();
    }

    // ---- Policies ----

    private async Task<OrgCoverage> CoverageAsync(CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        return coverage.Any ? coverage : throw AppException.Denied();
    }

    public async Task<IReadOnlyList<LeavePolicyView>> ListPoliciesAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var coverage = await CoverageAsync(ct);
        var keys = (await TypesAsync(ct)).ToDictionary(t => t.Id, t => t.Key);
        return (await PoliciesAsync(null, ct))
            .Where(p => p.OrgUnitId is null || coverage.Covers(p.OrgUnitId))
            .OrderBy(p => keys.GetValueOrDefault(p.TypeId), StringComparer.Ordinal).ThenBy(p => p.OrgUnitId.HasValue)
            .Select(p => View(p, keys, coverage)).ToList();
    }

    public async Task<LeavePolicyView> CreatePolicyAsync(LeavePolicyRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        await DemandUnitAsync(request.OrgUnitId, ct);
        var (type, bands, accrual) = await ValidateAsync(request, ct);
        if ((await PoliciesAsync(type.Id, ct)).Any(p => p.OrgUnitId == request.OrgUnitId))
        {
            throw AppException.Conflict("conflict.leave.policy_exists");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into hr.leave_policy (tenant_id, id, leave_type_id, org_unit_id, bands, accrual, created_at, updated_at, updated_by)
            values (platform.current_tenant_id(), @id, @type, @unit, @bands, @accrual, @now, @now, @actor)
            """, new { id, type = type.Id, unit = request.OrgUnitId, bands = BandsJson(bands), accrual, now, actor = context.ActorId }, ct);
        await PolicyChangedAsync(id, "created", type.Key, request.OrgUnitId, bands, accrual, ct);
        return await PolicyViewAsync(id, ct);
    }

    public async Task<LeavePolicyView> UpdatePolicyAsync(Guid id, string? ifMatch, LeavePolicyRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var coverage = await CoverageAsync(ct);
        var row = (await PoliciesAsync(null, ct)).SingleOrDefault(p => p.Id == id);
        if (row is null || !(row.OrgUnitId is null || coverage.Covers(row.OrgUnitId)))
        {
            throw AppException.NotFound();
        }
        await DemandUnitAsync(row.OrgUnitId, ct);
        if (ifMatch != ETag(row.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.leave.changed", null, new Dictionary<string, object?> { ["etag"] = ETag(row.Version) });
        }
        var type = await TypeAsync(row.TypeId, ct);
        var (_, bands, accrual) = await ValidateAsync(request with { TypeKey = type!.Key, OrgUnitId = row.OrgUnitId }, ct);
        await session.ExecAsync(
            "update hr.leave_policy set bands = @bands, accrual = @accrual, version = version + 1, updated_at = @now, updated_by = @actor where id = @id",
            new { id, bands = BandsJson(bands), accrual, now = clock.UtcNow, actor = context.ActorId }, ct);
        await PolicyChangedAsync(id, "updated", type.Key, row.OrgUnitId, bands, accrual, ct);
        return await PolicyViewAsync(id, ct);
    }

    public async Task DeletePolicyAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var coverage = await CoverageAsync(ct);
        var row = (await PoliciesAsync(null, ct)).SingleOrDefault(p => p.Id == id);
        if (row is null || !(row.OrgUnitId is null || coverage.Covers(row.OrgUnitId)))
        {
            throw AppException.NotFound();
        }
        await DemandUnitAsync(row.OrgUnitId, ct);
        var type = await TypeAsync(row.TypeId, ct);
        await session.ExecAsync("delete from hr.leave_policy where id = @id", new { id }, ct);
        await PolicyChangedAsync(id, "deleted", type!.Key, row.OrgUnitId, [], row.Accrual, ct);
    }

    private async Task<LeavePolicyView> PolicyViewAsync(Guid id, CancellationToken ct)
    {
        var coverage = await CoverageAsync(ct);
        var keys = (await TypesAsync(ct)).ToDictionary(t => t.Id, t => t.Key);
        return View((await PoliciesAsync(null, ct)).Single(p => p.Id == id), keys, coverage);
    }

    private static LeavePolicyView View(LeavePolicyRow p, IReadOnlyDictionary<Guid, string> keys, OrgCoverage coverage) =>
        new(p.Id, keys.GetValueOrDefault(p.TypeId) ?? string.Empty, p.OrgUnitId, p.Bands, p.Accrual, p.Version,
            p.OrgUnitId is null ? coverage.WholeTenant : coverage.Covers(p.OrgUnitId));

    private async Task DemandUnitAsync(Guid? unit, CancellationToken ct)
    {
        if (unit is { } u && !await hierarchy.UnitExistsAsync(u, ct))
        {
            throw AppException.Validation("orgUnitId", "validation.leave.unit_unknown");
        }
        await authz.DemandAsync(Permissions.HrManage, unit is { } id ? AuthzTarget.OrgUnit(id) : AuthzTarget.Tenant, ct);
    }

    private async Task<(LeaveTypeRow Type, IReadOnlyList<LeaveBand> Bands, string Accrual)> ValidateAsync(LeavePolicyRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var type = request.TypeKey is { } key ? await TypeByKeyAsync(key, ct) : null;
        if (type is null)
        {
            errors.Add("typeKey", "validation.leave.type_unknown");
        }
        var bands = request.Bands ?? [];
        if (bands.Count is 0 or > MaxBands
            || bands.Any(b => b.Days is < 0 or > LeaveMath.MaxDaysPerYear || b.MinServiceYears is < 0 or > 60
                || b.Grades?.Any(g => string.IsNullOrWhiteSpace(g) || g.Length > 40) == true))
        {
            errors.Add("bands", "validation.leave.bands");
        }
        var accrual = request.Accrual ?? "monthly";
        if (accrual is not ("monthly" or "yearly"))
        {
            errors.Add("accrual", "validation.leave.accrual");
        }
        errors.ThrowIfAny();
        var clean = bands.Select(b => new LeaveBand(b.Days, b.MinServiceYears, b.Grades is { Count: > 0 } g ? g.Select(x => x.Trim()).ToList() : null)).ToList();
        return (type!, clean, accrual);
    }

    private static Jsonb BandsJson(IReadOnlyList<LeaveBand> bands) => new(JsonSerializer.Serialize(bands, JsonDefaults.Options));

    private Task PolicyChangedAsync(Guid id, string operation, string typeKey, Guid? unit, IReadOnlyList<LeaveBand> bands, string accrual, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditActions.HrLeavePolicyChanged, "leave_policy", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { operation, typeKey, orgUnitId = unit, bands, accrual }), ct);

    private static Dictionary<string, string>? CleanNames(IReadOnlyDictionary<string, string>? names)
    {
        var clean = (names ?? new Dictionary<string, string>()).Where(n => !string.IsNullOrWhiteSpace(n.Value))
            .ToDictionary(n => n.Key, n => n.Value.Trim(), StringComparer.Ordinal);
        return clean.Count == 0 || clean.Any(n => !LocaleKey().IsMatch(n.Key) || n.Value.Length > 80 || n.Value.Any(char.IsControl)) ? null : clean;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex LocaleKey();
}
