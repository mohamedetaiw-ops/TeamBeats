using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

public sealed record DelegationRequest(Guid? DelegateId, DateOnly? StartsOn, DateOnly? EndsOn);

public sealed record DelegationView(PersonRef Delegate, DateOnly StartsOn, DateOnly EndsOn, bool Active);

/// <summary>
/// Delegation while away (spec 016 US4, "Manage Replacement"): a person hands their approvals to one delegate for a
/// range of days on their own calendar. The engine asks <see cref="IDelegations"/> who acts for whom, so tasks go to the
/// delegate inside the range and come back after it, unless already acted on. The delegator still sees their tasks.
/// </summary>
public sealed class Delegations(IDbSession session, ITenantContext context, IEntitlementService entitlements, IUserDirectory users, IWorkingCalendar calendars,
    IAuditWriter audit, IClock clock) : IDelegations
{
    public const int MaxDays = 366;

    public async Task<IReadOnlyList<Guid>> DelegatorsOfAsync(Guid delegateId, DateTimeOffset at, CancellationToken ct = default)
    {
        // The widest calendar day anywhere is within a day of UTC, so only ranges around that day can apply.
        var utcDay = DateOnly.FromDateTime(at.UtcDateTime);
        var rows = await session.QueryAsync(
            "select user_id, starts_on, ends_on from services.delegation where delegate_user_id = @delegateId and starts_on <= @latest and ends_on >= @earliest",
            new { delegateId, latest = utcDay.AddDays(1), earliest = utcDay.AddDays(-1) },
            r => (User: r.GetGuid(0), Starts: r.GetFieldValue<DateOnly>(1), Ends: r.GetFieldValue<DateOnly>(2)), ct);
        var active = new List<Guid>();
        foreach (var row in rows)
        {
            var today = (await calendars.ForUserAsync(row.User, ct)).Today(at);
            if (row.Starts <= today && today <= row.Ends && await users.GetAsync(row.User, ct) is { Status: "active" })
            {
                active.Add(row.User);
            }
        }
        return active;
    }

    public async Task<DelegationView?> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        var row = await session.QuerySingleOrDefaultAsync(
            "select delegate_user_id, starts_on, ends_on from services.delegation where user_id = @user", new { user },
            r => (Delegate: r.GetGuid(0), Starts: r.GetFieldValue<DateOnly>(1), Ends: r.GetFieldValue<DateOnly>(2)), ct);
        if (row.Delegate == Guid.Empty)
        {
            return null;
        }
        var today = (await calendars.ForUserAsync(user, ct)).Today(clock.UtcNow);
        if (row.Ends < today)
        {
            return null;
        }
        var person = await users.GetAsync(row.Delegate, ct);
        return new DelegationView(new PersonRef(row.Delegate, person?.DisplayName ?? string.Empty), row.Starts, row.Ends, row.Starts <= today);
    }

    public async Task<DelegationView> SetAsync(DelegationRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        var today = (await calendars.ForUserAsync(user, ct)).Today(clock.UtcNow);
        var errors = new ValidationErrors();
        if (request.DelegateId is not { } delegateId || delegateId == user || await users.GetAsync(delegateId, ct) is not { Status: "active" })
        {
            errors.Add("delegateId", "validation.services.delegate");
        }
        if (request.StartsOn is not { } starts || request.EndsOn is not { } ends || ends < starts || ends < today || ends.DayNumber - starts.DayNumber >= MaxDays)
        {
            errors.Add("endsOn", "validation.services.delegation_dates");
        }
        errors.ThrowIfAny();

        var previous = await session.ScalarAsync<Guid?>("select delegate_user_id from services.delegation where user_id = @user", new { user }, ct);
        await session.ExecAsync(
            """
            insert into services.delegation (tenant_id, user_id, delegate_user_id, starts_on, ends_on, updated_at)
            values (platform.current_tenant_id(), @user, @delegateId, @starts, @ends, @now)
            on conflict (tenant_id, user_id) do update set delegate_user_id = excluded.delegate_user_id, starts_on = excluded.starts_on,
              ends_on = excluded.ends_on, updated_at = excluded.updated_at
            """, new { user, delegateId = request.DelegateId!.Value, starts = request.StartsOn!.Value, ends = request.EndsOn!.Value, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(previous is null ? AuditActions.DelegationSet : AuditActions.DelegationChanged, "delegation", user.ToString(),
            Changes: new { delegator = user, @delegate = request.DelegateId, previous, startsOn = request.StartsOn, endsOn = request.EndsOn }), ct);
        return (await MineAsync(ct))!;
    }

    /// <summary>Ends the delegation now; open tasks come back to the delegator at once.</summary>
    public async Task EndAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        var previous = await session.ScalarAsync<Guid?>("delete from services.delegation where user_id = @user returning delegate_user_id", new { user }, ct);
        if (previous is not null)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.DelegationEnded, "delegation", user.ToString(), Changes: new { delegator = user, @delegate = previous }), ct);
        }
    }
}
