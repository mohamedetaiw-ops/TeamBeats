using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <param name="Days">ISO weekdays, 1 for Monday to 7 for Sunday.</param>
public sealed record BriefingRequest(string? Time, List<int>? Days, string? TimeZone);

public sealed record BriefingSubscription(string Time, IReadOnlyList<int> Days, string TimeZone, DateTimeOffset NextRunAt, DateTimeOffset? LastRunAt);

/// <summary>Whether the agent offers the person a daily briefing now, and their subscription if they have one.</summary>
public sealed record BriefingView(Guid AgentId, bool Offered, string DefaultTimeZone, BriefingSubscription? Subscription);

/// <summary>
/// Daily briefings (spec 020 US8 scenarios 2 to 4, FR-014; spec 027 AGV-003). A person opts in per agent, at a time on
/// chosen weekdays; the option is offered only when the published agent offers briefings, the person may use it, and
/// the tenant policy allows scheduled agents of its risk class. Opting out deletes the subscription, so no further
/// briefing runs.
/// </summary>
public sealed class AgentBriefings(IDbSession session, ITenantContext context, IEntitlementService entitlements, IOrganizationHierarchy org,
    AgentPolicyService policies, ITenantSettingsReader settings, IClock clock)
{
    public const string NotificationCategory = NotificationCategories.AgentBriefings;
    public const string Template = "agents.briefing";
    public const int MaxNotificationText = 1500;
    public static readonly IReadOnlyList<int> WorkingDays = [1, 2, 3, 4, 5];

    private static AgentDefinition Definition(string json) => JsonSerializer.Deserialize<AgentDefinition>(json, AgentPolicyService.Json)!;

    /// <summary>The policy check of US8 scenario 4: scheduled agents on, and this risk class among those allowed.</summary>
    public static bool Allowed(AgentDefinition definition, AgentPolicy policy, string? riskClass) =>
        AgentBriefingSettings.Offers(definition) && policy.ScheduledAgents && riskClass is not null && policy.ScheduledClasses.Contains(riskClass);

    private sealed record Published(string Status, AgentDefinition? Definition, string? Risk);

    private async Task<Published?> AgentAsync(Guid agentId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            """
            select a.status, v.definition::text, v.risk_class from agents.agent a
            left join agents.agent_version v on v.agent_id = a.id and v.number = a.current_version
            where a.id = @agentId
            """, new { agentId }, r => new Published(r.GetString(0), r.Get<string?>("definition") is { } d ? Definition(d) : null, r.Get<string?>("risk_class")), ct);

    private async Task<(Guid User, Published Agent, bool Offered)> OfferAsync(Guid agentId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsBasic, ct);
        var user = context.RequireUser();
        var agent = await AgentAsync(agentId, ct);
        if (agent?.Definition is not { } definition || !await AgentAccess.InAudienceAsync(session, org, agentId, definition.Audience, user, ct))
        {
            throw AppException.NotFound();
        }
        var offered = agent.Status == AgentStatuses.Published && Allowed(definition, (await policies.CurrentAsync(ct)).Policy, agent.Risk);
        return (user, agent, offered);
    }

    private async Task<BriefingSubscription?> SubscriptionAsync(Guid agentId, Guid user, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            "select time, days, time_zone, next_run_at, last_run_at from agents.agent_briefing where agent_id = @agentId and user_id = @user",
            new { agentId, user }, r => new BriefingSubscription(r.GetString(0), r.GetFieldValue<int[]>(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
                r.Get<DateTimeOffset?>("last_run_at")), ct);

    /// <summary><c>GET /api/v1/agents/{id}/briefing</c>.</summary>
    public async Task<BriefingView> GetAsync(Guid agentId, CancellationToken ct)
    {
        var (user, _, offered) = await OfferAsync(agentId, ct);
        return new BriefingView(agentId, offered, (await settings.GetLocaleSettingsAsync(ct)).TimeZone, await SubscriptionAsync(agentId, user, ct));
    }

    /// <summary><c>PUT /api/v1/agents/{id}/briefing</c>: opts in, or changes the time and days.</summary>
    public async Task<BriefingView> SubscribeAsync(Guid agentId, BriefingRequest request, CancellationToken ct)
    {
        var (user, _, offered) = await OfferAsync(agentId, ct);
        if (!offered)
        {
            throw AppException.Conflict("conflict.agent.briefing_not_offered");
        }
        var errors = new ValidationErrors();
        if (request.Time is null || !TimeOnly.TryParseExact(request.Time, "HH:mm", out var time))
        {
            errors.Add("time", "validation.agent.briefing_time");
            time = default;
        }
        var days = (request.Days ?? [.. WorkingDays]).Distinct().Order().ToList();
        if (days.Count == 0 || days.Any(d => d is < 1 or > 7))
        {
            errors.Add("days", "validation.agent.briefing_days");
        }
        var zoneId = string.IsNullOrWhiteSpace(request.TimeZone) ? (await settings.GetLocaleSettingsAsync(ct)).TimeZone : request.TimeZone.Trim();
        TimeZoneInfo? zone = null;
        if (zoneId.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out zone))
        {
            errors.Add("timeZone", "validation.agent.briefing_time_zone");
        }
        errors.ThrowIfAny();
        var now = clock.UtcNow;
        var next = NextRun(now, time, days, zone!);
        await session.ExecAsync(
            """
            insert into agents.agent_briefing (tenant_id, user_id, agent_id, time, days, time_zone, created_at, next_run_at)
            values (platform.current_tenant_id(), @user, @agentId, @time, @days, @zoneId, @now, @next)
            on conflict (tenant_id, user_id, agent_id) do update set time = excluded.time, days = excluded.days, time_zone = excluded.time_zone, next_run_at = excluded.next_run_at
            """, new { user, agentId, time = time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), days = days.ToArray(), zoneId, now, next }, ct);
        return await GetAsync(agentId, ct);
    }

    /// <summary><c>DELETE /api/v1/agents/{id}/briefing</c>: opts out (US8 scenario 3). Allowed even when the agent no longer offers briefings.</summary>
    public async Task UnsubscribeAsync(Guid agentId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsBasic, ct);
        var user = context.RequireUser();
        if (await session.ExecAsync("delete from agents.agent_briefing where agent_id = @agentId and user_id = @user", new { agentId, user }, ct) == 0)
        {
            throw AppException.NotFound();
        }
    }

    /// <summary>The next time after <paramref name="after"/> that falls on one of the days at the local time in the zone.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset after, TimeOnly time, IReadOnlyCollection<int> days, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(after, zone);
        for (var i = 0; i <= 7; i++)
        {
            var date = DateOnly.FromDateTime(local.Date).AddDays(i);
            var iso = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
            if (!days.Contains(iso))
            {
                continue;
            }
            var wall = date.ToDateTime(time);
            if (zone.IsInvalidTime(wall))
            {
                wall = wall.AddHours(1);
            }
            var at = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            if (at > after)
            {
                return at.ToUniversalTime();
            }
        }
        throw new InvalidOperationException("No briefing day within a week.");
    }

    internal sealed record Due(Guid User, Guid Agent, string Time, int[] Days, string Zone);

    /// <summary>Claims the briefings due now in the current tenant, moving each to its next time first, so a crash never runs one twice.</summary>
    internal async Task<IReadOnlyList<Due>> ClaimDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            "select user_id, agent_id, time, days, time_zone from agents.agent_briefing where next_run_at <= @now order by next_run_at limit @limit for update skip locked",
            new { now, limit }, r => new Due(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<int[]>(3), r.GetString(4)), ct);
        foreach (var d in due)
        {
            var zone = TimeZoneInfo.TryFindSystemTimeZoneById(d.Zone, out var z) ? z : TimeZoneInfo.Utc;
            var next = NextRun(now, TimeOnly.ParseExact(d.Time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture), d.Days, zone);
            await session.ExecAsync("update agents.agent_briefing set next_run_at = @next, last_run_at = @now where user_id = @user and agent_id = @agent",
                new { next, now, user = d.User, agent = d.Agent }, ct);
        }
        return due;
    }

    internal Task RecordAsync(Guid user, Guid agent, Guid runId, CancellationToken ct) =>
        session.ExecAsync("update agents.agent_briefing set last_run_id = @runId where user_id = @user and agent_id = @agent", new { runId, user, agent }, ct);

    /// <summary>A person's briefings end when they leave the tenant (spec 003).</summary>
    public Task ForgetUserAsync(Guid userId, CancellationToken ct) =>
        session.ExecAsync("delete from agents.agent_briefing where user_id = @userId", new { userId }, ct);
}

/// <summary>
/// The briefing worker (spec 020 US8 scenario 2, spec 027 AGV-003): in the current tenant, claims the due briefings and
/// runs each in its own scope as the subscribing person, with their permissions at that time. A disabled person or one
/// who left gets nothing. The answer arrives as a notification in the agent briefing category.
/// </summary>
public sealed class AgentBriefingRunner(IServiceScopeFactory scopes, ITenantContext context, IClock clock, ILogger<AgentBriefingRunner> logger)
{
    public const int BatchSize = 50;

    private async Task<T> InScopeAsync<T>(Guid tenant, Guid? user, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(user is null ? ActorType.System : ActorType.User, user);
        var result = await action(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync();
        return result;
    }

    /// <summary>Runs the briefings due now; returns how many were delivered.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var now = clock.UtcNow;
        var due = await InScopeAsync(tenant, null, sp => sp.GetRequiredService<AgentBriefings>().ClaimDueAsync(now, BatchSize, ct));
        var delivered = 0;
        foreach (var d in due)
        {
            try
            {
                delivered += await InScopeAsync(tenant, d.User, sp => DeliverAsync(sp, d, ct)) ? 1 : 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Briefing of agent {AgentId} failed", d.Agent);
            }
        }
        return delivered;
    }

    private static async Task<bool> DeliverAsync(IServiceProvider sp, AgentBriefings.Due d, CancellationToken ct)
    {
        var person = await sp.GetRequiredService<IUserDirectory>().GetAsync(d.User, ct);
        if (person is not { Status: "active", IsTest: false })
        {
            return false;
        }
        var run = await sp.GetRequiredService<AgentRuns>().BriefingAsync(d.Agent, person.PreferredLocale ?? "en", ct);
        if (run is null)
        {
            return false;
        }
        await sp.GetRequiredService<AgentBriefings>().RecordAsync(d.User, d.Agent, run.Id, ct);
        if (run.Answer is not { Length: > 0 } answer)
        {
            return false;
        }
        var text = answer.Length > AgentBriefings.MaxNotificationText ? answer[..AgentBriefings.MaxNotificationText] + "…" : answer;
        await sp.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest([d.User], AgentBriefings.NotificationCategory, AgentBriefings.Template,
            $"/agents/{d.Agent}", new Dictionary<string, string> { ["answer"] = text }, DedupeKey: $"briefing:{d.Agent}:{run.Id}"), ct);
        return true;
    }
}

/// <summary>Leavers (spec 003 deprovisioning): their briefings stop.</summary>
public sealed class AgentBriefingLeavers(AgentBriefings briefings) : TeamBeats.Messaging.IMessageHandler<UserDeprovisioned>
{
    public Task HandleAsync(UserDeprovisioned message, CancellationToken ct) => briefings.ForgetUserAsync(message.UserId, ct);
}
