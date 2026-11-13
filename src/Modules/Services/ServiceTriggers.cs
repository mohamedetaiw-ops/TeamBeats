using System.Globalization;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Start triggers of services (spec 016 FR-014, US8 scenario 2) and start service steps. Product events start one
/// request of each published service that listens to them, for the person the event is about; schedules start one
/// request each period for the service's system requester; a start service step starts the other service's request
/// for the same person. Every start is idempotent per event, and a chain of requests started by requests stops past
/// depth 5 with an alert on the request that would have gone deeper. A requester who leaves has their open requests
/// cancelled, except on services marked "continue after exit". Triggers need <c>workflow.automation</c> when they run.
/// </summary>
public sealed class ServiceTriggers(IDbSession session, Requests requests, IWorkingCalendar calendars, IEntitlementService entitlements,
    IAuditWriter audit, IClock clock)
    : IMessageHandler<UserProvisioned>, IMessageHandler<UserDeprovisioned>, IMessageHandler<OrganizationMembershipChanged>,
      IMessageHandler<RequestCompleted>, IMessageHandler<ProcessActionRequested>
{
    private sealed record Listener(Guid ServiceId, ServiceRoute Route, DateTimeOffset PublishedAt);

    /// <summary>Published form services whose current version has triggers.</summary>
    private async Task<IReadOnlyList<Listener>> ListenersAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select s.id, v.route::text, v.published_at from services.service s
            join services.service_version v on v.service_id = s.id and v.version = s.current_version
            where s.status = 'published' and s.kind = 'form' and jsonb_typeof(v.route->'triggers') = 'array' and jsonb_array_length(v.route->'triggers') > 0
            order by s.id
            """, null, r => (Id: r.GetGuid(0), Route: r.GetString(1), At: r.Get<DateTimeOffset?>("published_at")), ct);
        return rows.Select(r => new Listener(r.Id, JsonSerializer.Deserialize<ServiceRoute>(r.Route, JsonDefaults.Options) ?? ServiceRoute.None,
            r.At ?? DateTimeOffset.MinValue)).ToList();
    }

    private async Task StartForEventAsync(string @event, Guid subject, string eventKey, int depth, Func<ServiceTrigger, bool> matches, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.WorkflowAutomation, ct))
        {
            return;
        }
        foreach (var listener in await ListenersAsync(ct))
        {
            if (listener.Route.Triggers!.Any(t => t.Kind == TriggerKinds.Event && t.Event == @event && matches(t)))
            {
                await requests.StartByTriggerAsync(listener.ServiceId, subject, eventKey, depth, @event, ct);
            }
        }
    }

    public Task HandleAsync(UserProvisioned message, CancellationToken ct) =>
        StartForEventAsync(TriggerEvents.UserProvisioned, message.UserId, $"{TriggerEvents.UserProvisioned}:{message.UserId:N}", 0, _ => true, ct);

    /// <summary>
    /// The leaving person's open requests are cancelled first, and then the services that start on leaving (for example
    /// final settlement) start theirs, which the cancel leaves alone in whichever order the two run.
    /// </summary>
    public async Task HandleAsync(UserDeprovisioned message, CancellationToken ct)
    {
        await requests.CancelForLeaverAsync(message.UserId, ct);
        await StartForEventAsync(TriggerEvents.UserDeprovisioned, message.UserId, $"{TriggerEvents.UserDeprovisioned}:{message.UserId:N}", 0, _ => true, ct);
    }

    /// <summary>A unit change starts one request per person, unit and change (added or ended).</summary>
    public Task HandleAsync(OrganizationMembershipChanged message, CancellationToken ct) =>
        StartForEventAsync(TriggerEvents.MembershipChanged, message.UserId,
            $"{TriggerEvents.MembershipChanged}:{message.UserId:N}:{message.OrgUnitId:N}:{message.Change}", 0,
            t => t.OrgUnitId is null || t.OrgUnitId == message.OrgUnitId, ct);

    public async Task HandleAsync(RequestCompleted message, CancellationToken ct)
    {
        var (subject, depth) = await requests.ChainOfAsync(message.RequestId, ct);
        if (subject == Guid.Empty)
        {
            return;
        }
        if (depth + 1 > Requests.MaxChainDepth)
        {
            if ((await ListenersAsync(ct)).Any(l => l.Route.Triggers!.Any(t => RequestCompletedMatches(t, message))))
            {
                await StopChainAsync(message.RequestId, null, ct);
            }
            return;
        }
        await StartForEventAsync(TriggerEvents.RequestCompleted, subject, $"{TriggerEvents.RequestCompleted}:{message.RequestId:N}", depth + 1,
            t => RequestCompletedMatches(t, message), ct);
    }

    private static bool RequestCompletedMatches(ServiceTrigger t, RequestCompleted message) =>
        t.Kind == TriggerKinds.Event && t.Event == TriggerEvents.RequestCompleted && t.ServiceId == message.ServiceId
        && (t.Status is null || t.Status == message.Status);

    /// <summary>A start service step of a request's route starts the other service's request for the same person.</summary>
    public async Task HandleAsync(ProcessActionRequested message, CancellationToken ct)
    {
        if (message.Action != StepTypes.StartService || Tasks.RequestIdOf(message.DefinitionKey, message.BusinessKey) is not { } source
            || message.Inputs["service"]?.GetValue<string>() is not { } text || !Guid.TryParse(text, out var target))
        {
            return;
        }
        var (subject, depth) = await requests.ChainOfAsync(source, ct);
        if (subject == Guid.Empty)
        {
            return;
        }
        if (depth + 1 > Requests.MaxChainDepth)
        {
            await StopChainAsync(source, message.ElementId, ct);
            return;
        }
        var started = await requests.StartByTriggerAsync(target, subject, $"action:{message.ActionId:N}", depth + 1, StepTypes.StartService, ct);
        if (started is { } id)
        {
            var number = await session.ScalarAsync<string>("select number from services.request where id = @id", new { id }, ct);
            await requests.EventAsync(source, "service_started", message.ElementId, null, null, number, ct);
        }
    }

    /// <summary>The alert of a chain deeper than 5 (edge cases: trigger loops): Tracking of the request and the audit log.</summary>
    private async Task StopChainAsync(Guid requestId, string? step, CancellationToken ct)
    {
        await requests.EventAsync(requestId, "chain_stopped", step, null, null, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RequestChainStopped, "request", requestId.ToString(),
            Changes: new { depth = Requests.MaxChainDepth }), ct);
    }

    // ---- Schedules ----

    /// <summary>
    /// Starts the scheduled requests that are due: each schedule starts one request per period (ISO week, month or
    /// quarter) on or after its day, on the system requester's working calendar. A period whose day came before the
    /// version was published is not caught up, so publishing a schedule never starts a request for the past.
    /// </summary>
    public async Task<int> RunSchedulesAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.WorkflowAutomation, ct))
        {
            return 0;
        }
        var started = 0;
        var now = clock.UtcNow;
        foreach (var listener in await ListenersAsync(ct))
        {
            foreach (var t in listener.Route.Triggers!.Where(t => t.Kind == TriggerKinds.Schedule && t.RequesterId is not null && t.Day is not null))
            {
                var requester = t.RequesterId!.Value;
                var today = (await calendars.ForUserAsync(requester, ct)).Today(now);
                if (Period(t.Frequency!, t.Day!.Value, today) is not { } period || period.RunOn > today
                    || period.RunOn < DateOnly.FromDateTime(listener.PublishedAt.UtcDateTime))
                {
                    continue;
                }
                var key = $"schedule:{t.Frequency}:{t.Day}:{period.Key}";
                if (await requests.StartByTriggerAsync(listener.ServiceId, requester, key, 0, TriggerKinds.Schedule, ct) is not null)
                {
                    started++;
                }
            }
        }
        return started;
    }

    /// <summary>The period <paramref name="today"/> falls in, and the date in it on which the schedule runs.</summary>
    public static (string Key, DateOnly RunOn)? Period(string frequency, int day, DateOnly today)
    {
        switch (frequency)
        {
            case ScheduleFrequencies.Weekly when day is >= 1 and <= 7:
                var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                var date = today.ToDateTime(TimeOnly.MinValue);
                return ($"{ISOWeek.GetYear(date)}-W{ISOWeek.GetWeekOfYear(date):D2}", monday.AddDays(day - 1));
            case ScheduleFrequencies.Monthly when day is >= 1 and <= 28:
                return ($"{today.Year}-{today.Month:D2}", new DateOnly(today.Year, today.Month, day));
            case ScheduleFrequencies.Quarterly when day is >= 1 and <= 28:
                var quarter = (today.Month - 1) / 3;
                return ($"{today.Year}-Q{quarter + 1}", new DateOnly(today.Year, quarter * 3 + 1, day));
            default:
                return null;
        }
    }
}
