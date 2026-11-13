using System.Globalization;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi;

/// <summary>One public API call as the call log keeps it (section 6): no body, no query string, no token.</summary>
public sealed record CallRecord(Guid AppId, Guid? UserId, string Operation, int Status, string? ErrorClass, int LatencyMs, string? CorrelationId);

/// <summary>
/// The call log and the monthly call counter (spec 035 US5, FR-017). Every call a token was accepted for is logged,
/// refused ones too; only calls that were processed count against <c>api.monthly_calls</c>. API administrators are told
/// once a month when the tenant reaches 80% and 100% of the quota.
/// </summary>
public sealed class CallLog(IDbSession session, IClock clock, IAuthorizationProvisioning authorization, INotificationSender notifications)
{
    public const string Quota80Template = "publicapi.quota_80";
    public const string Quota100Template = "publicapi.quota_100";

    public const string RateLimited = "rate_limited";
    public const string Quota = "quota";

    /// <summary>The error class of a status: null for a success, else denied, not_found, client or server.</summary>
    public static string? ClassOf(int status) => status switch
    {
        < 400 => null,
        403 => "denied",
        404 => "not_found",
        429 => RateLimited,
        < 500 => "client",
        _ => "server",
    };

    public static DateOnly MonthOf(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month, 1);

    public Task<long> MonthCallsAsync(CancellationToken ct) =>
        session.ScalarAsync<long>("select coalesce((select calls from publicapi.usage_month where month = @month), 0)", new { month = MonthOf(clock.UtcNow) }, ct);

    /// <summary>Writes the call, and counts it against the month when it was processed, sending the 80% and 100% alerts.</summary>
    public async Task RecordAsync(CallRecord call, long? monthlyLimit, CancellationToken ct)
    {
        var now = clock.UtcNow;
        // One round trip: the call, when the app (and the person's consent) was last used, and the month's count.
        // Refused calls (rate limit, quota) are logged but not counted against the month.
        var counted = call.ErrorClass is not (RateLimited or Quota);
        var month = MonthOf(now);
        var calls = await session.ScalarAsync<long>(
            """
            with logged as (
              insert into publicapi.call_log (tenant_id, id, at, app_id, user_id, operation, status, error_class, latency_ms, correlation_id)
              values (platform.current_tenant_id(), @id, @now, @app, @user::uuid, @operation, @status, @error, @latency, @correlation)
              returning 1),
            app_used as (
              update publicapi.app set last_used_at = @now
              where id = @app and (last_used_at is null or last_used_at < @now - interval '1 minute')
              returning 1),
            consent_used as (
              update publicapi.consent set last_used_at = @now
              where @user::uuid is not null and app_id = @app and user_id = @user::uuid and revoked_at is null
                and (last_used_at is null or last_used_at < @now - interval '1 minute')
              returning 1),
            counted_month as (
              insert into publicapi.usage_month (tenant_id, month, calls)
              select platform.current_tenant_id(), @month, 1 where @counted
              on conflict (tenant_id, month) do update set calls = publicapi.usage_month.calls + 1
              returning calls)
            select coalesce((select calls from counted_month), 0)
            """,
            new
            {
                id = Ids.New(), now, app = call.AppId, user = call.UserId, operation = call.Operation[..Math.Min(call.Operation.Length, 200)], status = call.Status,
                error = call.ErrorClass, latency = call.LatencyMs, correlation = call.CorrelationId is { Length: <= 100 } c ? c : null, month, counted,
            }, ct);
        if (!counted)
        {
            return;
        }
        if (monthlyLimit is not { } limit || limit <= 0)
        {
            return;
        }
        if (calls >= limit)
        {
            await AlertAsync(month, "alerted_100", Quota100Template, limit, ct);
        }
        if (calls * 5 >= limit * 4)
        {
            await AlertAsync(month, "alerted_80", Quota80Template, limit, ct);
        }
    }

    private async Task AlertAsync(DateOnly month, string column, string template, long limit, CancellationToken ct)
    {
        // Only the call that flips the flag sends the alert, so two instances never both send it.
        var flipped = await session.ExecAsync($"update publicapi.usage_month set {column} = true where month = @month and not {column}", new { month }, ct);
        if (flipped == 0)
        {
            return;
        }
        var admins = await authorization.TenantHoldersAsync(Permissions.ApiManage, ct);
        if (admins.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(admins, NotificationCategories.IntegrationAlert, template, "/admin/api?tab=usage",
            new Dictionary<string, string> { ["limit"] = limit.ToString("N0", CultureInfo.InvariantCulture) },
            DedupeKey: $"publicapi:quota:{month:yyyyMM}:{column}"), ct);
    }

    /// <summary>Usage per app over the last days, at most 90 (US5 scenario 3), for API administrators.</summary>
    public async Task<ApiUsageView> UsageAsync(int days, long? monthlyLimit, CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-days);
        var totals = await session.QueryAsync(
            """
            select a.id, a.name, a.kind, count(l.id), count(*) filter (where l.error_class = 'denied'), count(*) filter (where l.error_class = 'rate_limited'),
              max(l.at)
            from publicapi.app a left join publicapi.call_log l on l.app_id = a.id and l.at >= @since
            group by a.id, a.name, a.kind
            order by count(l.id) desc, a.name, a.id
            """, new { since },
            r => (Id: r.GetGuid(0), Name: r.GetString(1), Kind: r.GetString(2), Calls: r.GetInt64(3), Denied: r.GetInt64(4), Limited: r.GetInt64(5),
                Last: r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6)), ct);
        var errors = await session.QueryAsync(
            "select app_id, error_class, count(*) from publicapi.call_log where at >= @since and error_class is not null group by app_id, error_class", new { since },
            r => (App: r.GetGuid(0), Class: r.GetString(1), Count: r.GetInt64(2)), ct);
        var operations = await session.QueryAsync(
            """
            select app_id, operation, calls from (
              select app_id, operation, count(*) calls, row_number() over (partition by app_id order by count(*) desc, operation) n
              from publicapi.call_log where at >= @since group by app_id, operation) t
            where n <= 5 order by app_id, calls desc, operation
            """, new { since }, r => (App: r.GetGuid(0), Operation: r.GetString(1), Calls: r.GetInt64(2)), ct);
        var apps = totals.Select(t => new AppUsageView(t.Id, t.Name, t.Kind, t.Calls,
            errors.Where(e => e.App == t.Id).ToDictionary(e => e.Class, e => e.Count, StringComparer.Ordinal), t.Denied, t.Limited,
            operations.Where(o => o.App == t.Id).Select(o => new OperationUsageView(o.Operation, o.Calls)).ToList(), t.Last)).ToList();
        return new ApiUsageView(days, await MonthCallsAsync(ct), monthlyLimit, apps);
    }

    /// <summary>Removes calls older than 90 days and month counters older than 13 months, in batches.</summary>
    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        var removed = await session.ExecAsync(
            "delete from publicapi.call_log where id in (select id from publicapi.call_log where at < @before order by at limit 5000)",
            new { before = now.AddDays(-ApiRules.CallLogDays) }, ct);
        await session.ExecAsync("delete from publicapi.usage_month where month < @before", new { before = MonthOf(now).AddMonths(-13) }, ct);
        return removed;
    }
}

/// <summary>The usage page of API administrators (US5 scenario 3), readable with <c>api.view</c>.</summary>
public sealed class ApiUsage(CallLog log, IAuthorizationService authz, IEntitlementService entitlements)
{
    public async Task<ApiUsageView> GetAsync(int days, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        await authz.DemandAsync(Permissions.ApiView, AuthzTarget.Tenant, ct);
        if (days is < 1 or > ApiRules.CallLogDays)
        {
            throw AppException.Validation("days", "validation.api.usage_days");
        }
        return await log.UsageAsync(days, await entitlements.GetLimitAsync(Capabilities.ApiMonthlyCalls, ct), ct);
    }
}
