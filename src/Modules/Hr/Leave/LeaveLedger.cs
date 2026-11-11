using System.Globalization;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Leave;

/// <summary>What leave rules need to know about a person: membership type, primary unit and its path, and HR grade and hire date.</summary>
internal sealed record LeavePerson(Guid UserId, string MembershipType, bool Active, Guid? PrimaryUnit, IReadOnlyList<Guid> Path, string? Grade, DateOnly? HireDate);

public sealed record AdjustmentRequest(Guid UserId, string? TypeKey, int? LeaveYear, decimal Delta, string? Note);

public sealed record LedgerEntryView(Guid Id, string TypeKey, int LeaveYear, decimal Delta, string Reason, Guid? RequestId, string? Note, DateTimeOffset At);

/// <summary>
/// The leave balance ledger (spec 022 section 14): balances change only through approved requests, the accrual and
/// carry-over job, and audited HR adjustments. A balance is the sum of the ledger for a person, type and leave year.
/// </summary>
public sealed class LeaveLedger(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    ITenantSettingsReader settings,
    IHrRecords hrRecords,
    IHrLeave hrLeave,
    LeaveConfig config)
{
    public const int MaxNoteLength = 500;

    public async Task<DateOnly> TodayAsync(CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await settings.GetLocaleSettingsAsync(ct)).TimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow.UtcDateTime, zone));
    }

    internal async Task<IReadOnlyDictionary<Guid, LeavePerson>> PeopleAsync(IReadOnlyCollection<Guid>? userIds, CancellationToken ct)
    {
        var members = userIds is null ? await memberships.ListAsync(ct) : (await memberships.GetManyAsync(userIds, ct)).Values.ToList();
        var wanted = members.Select(m => m.UserId).ToHashSet();
        var primary = new Dictionary<Guid, Guid>();
        if (userIds is null || userIds.Count > 20)
        {
            foreach (var m in (await hierarchy.ListActiveMembershipsAsync(ct)).Where(m => m.IsPrimary && wanted.Contains(m.UserId)))
            {
                primary[m.UserId] = m.OrgUnitId;
            }
        }
        else
        {
            foreach (var id in wanted)
            {
                if ((await hierarchy.GetActiveMembershipsAsync(id, ct)).FirstOrDefault(m => m.IsPrimary) is { } p)
                {
                    primary[id] = p.OrgUnitId;
                }
            }
        }
        var paths = primary.Count == 0 ? new Dictionary<Guid, IReadOnlyList<Guid>>() : await hierarchy.GetPathsAsync(primary.Values.Distinct(), ct);
        var records = (await hrRecords.MatchedAsync(userIds is null ? null : wanted, ct)).ToDictionary(r => r.UserId);
        return members.ToDictionary(m => m.UserId, m =>
        {
            var unit = primary.TryGetValue(m.UserId, out var u) ? u : (Guid?)null;
            var fields = records.TryGetValue(m.UserId, out var r) ? r.Fields : null;
            var hired = fields?.GetValueOrDefault(HrFields.HireDate) is { } h
                && DateOnly.TryParseExact(h, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
            return new LeavePerson(m.UserId, m.MembershipType, m.Status == MembershipStatus.Active, unit,
                unit is { } x && paths.TryGetValue(x, out var path) ? path : [], fields?.GetValueOrDefault(HrFields.Grade), hired);
        });
    }

    internal async Task<LeavePerson?> PersonAsync(Guid userId, CancellationToken ct) => (await PeopleAsync([userId], ct)).GetValueOrDefault(userId);

    /// <summary>Whether the HR system owns balances (spec 019 FR-013, spec 022 FR-006): the ledger is then not written.</summary>
    internal async Task<bool> HrOwnsAsync(CancellationToken ct) => (await hrLeave.SettingAsync(ct)).FromHr;

    /// <summary>The HR balances of a person when HR owns balances, else null.</summary>
    internal async Task<HrLeaveBalances?> HrBalancesAsync(Guid userId, CancellationToken ct) =>
        await HrOwnsAsync(ct) ? await hrLeave.BalancesAsync(userId, ct) : null;

    /// <summary>
    /// What TeamBeats holds against an HR balance for a type: pending requests, and approved requests HR has not
    /// recorded yet, leaving out one request (the one being changed).
    /// </summary>
    internal Task<decimal> HeldForHrAsync(Guid userId, Guid typeId, Guid? except, CancellationToken ct) =>
        session.ScalarAsync<decimal>(
            """
            select coalesce(sum(r.days), 0) from hr.leave_request r
            where r.user_id = @userId and r.leave_type_id = @typeId and (@except::uuid is null or r.id <> @except)
              and (r.status = 'pending' or (r.status = 'approved' and r.hr_state in ('queued', 'failed')))
            """, new { userId, typeId, except }, ct);

    internal Task<decimal> BalanceAsync(Guid userId, Guid typeId, int year, CancellationToken ct) =>
        session.ScalarAsync<decimal>(
            "select coalesce(sum(delta), 0) from hr.leave_ledger where user_id = @userId and leave_type_id = @typeId and leave_year = @year",
            new { userId, typeId, year }, ct);

    /// <summary>Days held by pending requests in a leave year, leaving out one request (the one being changed).</summary>
    internal Task<decimal> PendingAsync(Guid userId, Guid typeId, int year, Guid? except, CancellationToken ct) =>
        session.ScalarAsync<decimal>(
            """
            select coalesce(sum(case when t.unit = 'hour' then r.days else d.fraction end), 0)
            from hr.leave_request r join hr.leave_request_day d on d.request_id = r.id join hr.leave_type t on t.id = r.leave_type_id
            where r.user_id = @userId and r.leave_type_id = @typeId and r.status = 'pending' and d.leave_year = @year and (@except::uuid is null or r.id <> @except)
            """, new { userId, typeId, year, except }, ct);

    /// <summary>The yearly entitlement that applies to the person for a type, or null when no policy applies or the person is not eligible.</summary>
    internal async Task<(decimal Days, string Accrual)?> EntitlementAsync(LeavePerson person, LeaveTypeRow type, DateOnly on, CancellationToken ct)
    {
        if (!type.Eligible(person.MembershipType) || LeaveConfig.Applicable(await config.PoliciesAsync(type.Id, ct), person.Path) is not { } policy)
        {
            return null;
        }
        return (LeaveMath.Entitlement(policy.Bands, LeaveMath.ServiceYears(person.HireDate, on), person.Grade), policy.Accrual);
    }

    /// <summary>
    /// What the person may still take in a leave year. The current and past years read the ledger less pending
    /// requests. The next year has had no carry-over or accrual yet, so they are projected: what the current year
    /// carries over after <paramref name="takenThisYear"/>, plus the entitlement accrued by <paramref name="lastDay"/>,
    /// plus what the ledger already holds for that year (approved leave, cancellations, adjustments).
    /// </summary>
    internal async Task<(decimal Balance, decimal Pending, decimal Available)> AvailableAsync(LeavePerson person, LeaveTypeRow type, int year, DateOnly today,
        DateOnly lastDay, decimal takenThisYear, Guid? except, CancellationToken ct)
    {
        var current = LeaveMath.LeaveYear(today, type.YearStartMonth);
        if (await HrBalancesAsync(person.UserId, ct) is { } hr)
        {
            // HR keeps one balance per type; days of an earlier leave year in the same request come off it first.
            var held = await HeldForHrAsync(person.UserId, type.Id, except, ct) + (year > current ? takenThisYear : 0m);
            var fromHr = hr.Balances.FirstOrDefault(b => b.TypeKey == type.Key)?.Balance ?? 0m;
            return (fromHr, held, fromHr - held);
        }
        var balance = await BalanceAsync(person.UserId, type.Id, year, ct);
        var pending = await PendingAsync(person.UserId, type.Id, year, except, ct);
        if (year <= current)
        {
            return (balance, pending, balance - pending);
        }
        var now = await BalanceAsync(person.UserId, type.Id, current, ct) - await PendingAsync(person.UserId, type.Id, current, except, ct) - takenThisYear;
        var (carry, _) = LeaveMath.CarryOver(now, type.CarryOverMax);
        var projected = carry + balance;
        if (await EntitlementAsync(person, type, LeaveMath.YearStart(year, type.YearStartMonth), ct) is var (days, accrual))
        {
            projected += accrual == "yearly" ? days
                : Enumerable.Range(0, LeaveMath.MonthIndex(lastDay, type.YearStartMonth) + 1).Sum(i => LeaveMath.MonthlyAccrual(days, i));
        }
        return (projected, pending, projected - pending);
    }

    // ---- Accrual and carry-over (daily job) ----

    /// <summary>
    /// Accrues every eligible active member for every active type with a policy, and carries the previous leave year
    /// over once its year ended. Idempotent per person, type and period through the ledger's period key.
    /// </summary>
    public async Task<int> RunAccrualAsync(CancellationToken ct)
    {
        // When HR owns balances it accrues and carries over itself.
        if (!await entitlements.IsEnabledAsync(Capabilities.HrLeave, ct) || await HrOwnsAsync(ct))
        {
            return 0;
        }
        var today = await TodayAsync(ct);
        var types = (await config.TypesAsync(ct)).Where(t => t.Active).ToList();
        if (types.Count == 0)
        {
            return 0;
        }
        var people = (await PeopleAsync(null, ct)).Values.Where(p => p.Active).ToList();
        var written = 0;
        foreach (var type in types)
        {
            var year = LeaveMath.LeaveYear(today, type.YearStartMonth);
            var start = LeaveMath.YearStart(year, type.YearStartMonth);
            var policies = await config.PoliciesAsync(type.Id, ct);
            var rows = new List<(Guid User, int Year, decimal Delta, string Reason, string Key)>();
            foreach (var person in people)
            {
                if (!type.Eligible(person.MembershipType) || LeaveConfig.Applicable(policies, person.Path) is not { } policy)
                {
                    continue;
                }
                var days = LeaveMath.Entitlement(policy.Bands, LeaveMath.ServiceYears(person.HireDate, today), person.Grade);
                if (days <= 0)
                {
                    continue;
                }
                if (policy.Accrual == "yearly")
                {
                    rows.Add((person.UserId, year, days, "accrual", $"accrual:{year}"));
                    continue;
                }
                // Months of this leave year that started, from the hire month when the person joined during the year.
                var from = person.HireDate is { } hired && hired > start ? LeaveMath.MonthIndex(hired, type.YearStartMonth) : 0;
                for (var i = from; i <= LeaveMath.MonthIndex(today, type.YearStartMonth); i++)
                {
                    rows.Add((person.UserId, year, LeaveMath.MonthlyAccrual(days, i), "accrual", $"accrual:{year}:{i + 1:00}"));
                }
            }
            written += await InsertAsync(type.Id, rows, ct);
            written += await CarryOverAsync(type, year - 1, ct);
        }
        return written;
    }

    /// <summary>Moves what is left of a finished leave year into the next, up to the type's limit; the rest lapses.</summary>
    private async Task<int> CarryOverAsync(LeaveTypeRow type, int previous, CancellationToken ct)
    {
        var balances = await session.QueryAsync(
            """
            select user_id, sum(delta) from hr.leave_ledger l
            where leave_type_id = @type and leave_year = @previous
              and not exists (select 1 from hr.leave_ledger c where c.user_id = l.user_id and c.leave_type_id = l.leave_type_id and c.period_key = @key)
            group by user_id
            """, new { type = type.Id, previous, key = $"carry:{previous + 1}" }, r => (User: r.GetGuid(0), Balance: r.GetDecimal(1)), ct);
        var rows = new List<(Guid User, int Year, decimal Delta, string Reason, string Key)>();
        foreach (var (user, balance) in balances)
        {
            var (carry, lapse) = LeaveMath.CarryOver(balance, type.CarryOverMax);
            rows.Add((user, previous + 1, carry, "carry_over", $"carry:{previous + 1}"));
            rows.Add((user, previous, -carry, "carry_over", $"carry_out:{previous}"));
            if (lapse != 0)
            {
                rows.Add((user, previous, -lapse, "lapse", $"lapse:{previous}"));
            }
        }
        return await InsertAsync(type.Id, rows, ct);
    }

    private async Task<int> InsertAsync(Guid typeId, IReadOnlyList<(Guid User, int Year, decimal Delta, string Reason, string Key)> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }
        return await session.ExecAsync(
            """
            insert into hr.leave_ledger (tenant_id, id, user_id, leave_type_id, leave_year, delta, reason, period_key, at)
            select platform.current_tenant_id(), gen_random_uuid(), u, @typeId, y, d, r, k, @now
            from unnest(@users, @years, @deltas, @reasons, @keys) as x(u, y, d, r, k)
            on conflict (tenant_id, user_id, leave_type_id, period_key) where period_key is not null do nothing
            """, new
            {
                typeId, now = clock.UtcNow, users = rows.Select(r => r.User).ToArray(), years = rows.Select(r => r.Year).ToArray(),
                deltas = rows.Select(r => r.Delta).ToArray(), reasons = rows.Select(r => r.Reason).ToArray(), keys = rows.Select(r => r.Key).ToArray(),
            }, ct);
    }

    /// <summary>Ledger rows for an approved, cancelled or shortened request, one per leave year.</summary>
    internal async Task PostRequestAsync(Guid userId, Guid typeId, Guid requestId, string reason, IEnumerable<(int Year, decimal Days)> perYear, CancellationToken ct)
    {
        foreach (var (year, days) in perYear.Where(p => p.Days != 0))
        {
            await session.ExecAsync(
                """
                insert into hr.leave_ledger (tenant_id, id, user_id, leave_type_id, leave_year, delta, reason, request_id, actor_id, at)
                values (platform.current_tenant_id(), @id, @userId, @typeId, @year, @delta, @reason, @requestId, @actor, @now)
                """, new { id = Ids.New(), userId, typeId, year, delta = reason == "request" ? -days : days, reason, requestId, actor = context.ActorId, now = clock.UtcNow }, ct);
        }
    }

    // ---- HR adjustments ----

    /// <summary>An HR officer covering the person's primary unit adds or removes days, with a reason (section 14).</summary>
    public async Task<LedgerEntryView> AdjustAsync(AdjustmentRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        if (await PersonAsync(request.UserId, ct) is not { } person)
        {
            throw AppException.NotFound();
        }
        await DemandHrAsync(person, ct);
        if (await HrOwnsAsync(ct))
        {
            throw AppException.Conflict("conflict.leave.hr_owns_balances");
        }
        var errors = new ValidationErrors();
        var type = request.TypeKey is { } key ? await config.TypeByKeyAsync(key, ct) : null;
        if (type is null)
        {
            errors.Add("typeKey", "validation.leave.type_unknown");
        }
        if (request.Delta == 0 || Math.Abs(request.Delta) > LeaveMath.MaxDaysPerYear || decimal.Round(request.Delta, 2) != request.Delta)
        {
            errors.Add("delta", "validation.leave.delta");
        }
        var note = request.Note?.Trim();
        if (note is not { Length: > 0 and <= MaxNoteLength })
        {
            errors.Add("note", "validation.leave.note");
        }
        errors.ThrowIfAny();
        var today = await TodayAsync(ct);
        var current = LeaveMath.LeaveYear(today, type!.YearStartMonth);
        var year = request.LeaveYear ?? current;
        if (year < current - 1 || year > current + 1)
        {
            throw AppException.Validation("leaveYear", "validation.leave.year");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into hr.leave_ledger (tenant_id, id, user_id, leave_type_id, leave_year, delta, reason, note, actor_id, at)
            values (platform.current_tenant_id(), @id, @user, @type, @year, @delta, 'adjustment', @note, @actor, @now)
            """, new { id, user = person.UserId, type = type.Id, year, delta = request.Delta, note, actor = context.ActorId, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrBalanceAdjusted, "leave_balance", person.UserId.ToString(), TargetOrgUnitId: person.PrimaryUnit,
            Changes: new { person = person.UserId, type = type.Key, year, delta = request.Delta, reason = note }), ct);
        return new LedgerEntryView(id, type.Key, year, request.Delta, "adjustment", null, note, now);
    }

    /// <summary>A person's ledger, for themselves or an HR officer covering them.</summary>
    public async Task<IReadOnlyList<LedgerEntryView>> EntriesAsync(Guid userId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        if (userId != context.RequireUser())
        {
            if (await PersonAsync(userId, ct) is not { } person)
            {
                throw AppException.NotFound();
            }
            await DemandHrAsync(person, ct);
        }
        return await session.QueryAsync(
            """
            select l.id, t.key, l.leave_year, l.delta, l.reason, l.request_id, l.note, l.at
            from hr.leave_ledger l join hr.leave_type t on t.id = l.leave_type_id
            where l.user_id = @userId order by l.at desc, l.id limit 500
            """, new { userId },
            r => new LedgerEntryView(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetDecimal(3), r.GetString(4), r.Get<Guid?>("request_id"), r.Get<string?>("note"),
                r.GetFieldValue<DateTimeOffset>(7)), ct);
    }

    internal async Task<bool> HrCoversAsync(LeavePerson person, CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        return coverage.WholeTenant || (person.PrimaryUnit is { } u && coverage.Covers(u));
    }

    /// <summary>Not found rather than denied, so a sibling unit's HR officer learns nothing about the person.</summary>
    internal async Task DemandHrAsync(LeavePerson person, CancellationToken ct)
    {
        if (!await HrCoversAsync(person, ct))
        {
            throw (await authz.CoverageAsync(Permissions.HrManage, ct)).Any ? AppException.NotFound() : AppException.Denied();
        }
    }
}
