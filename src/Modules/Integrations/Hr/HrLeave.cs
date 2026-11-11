using System.Globalization;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

public sealed record HrLeaveSettingRequest(string? Source, bool? AllowStaleSubmit);

/// <summary>A leave hand-off as the HR integration page lists it.</summary>
public sealed record HrLeaveHandOffRow(Guid Id, Guid RequestId, int Change, string Kind, string State, int Attempt, string? ExternalReference, string? ErrorClass,
    DateTimeOffset CreatedAt, DateTimeOffset? RecordedAt, DateTimeOffset? NextRetryAt);

/// <summary>
/// HR as the leave balance source (spec 019 US7, FR-013; spec 022 FR-006). The HR administrator chooses the source.
/// Balances are read live through the HR REST pack and the last known are kept with their time. Each hand-off is a
/// durable connector job keyed by request and change; when HR is unavailable it stays queued and is sent again every
/// hour for three days, then it is failed and shown on the HR integration page.
/// </summary>
public sealed class HrLeave(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IConnectorRuntime runtime,
    HrRecords records,
    HrRestPack rest) : IHrLeave
{
    public const string CorrelationPrefix = "hr-leave:";
    public static readonly TimeSpan RetryEvery = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryWindow = TimeSpan.FromDays(3);

    private HrLeaveSetting? _setting;
    private readonly Dictionary<Guid, HrLeaveBalances> _balances = [];

    public static string ETag(int version) => $"\"v{version}\"";

    public async Task<HrLeaveSetting> SettingAsync(CancellationToken ct = default)
    {
        if (_setting is not null)
        {
            return _setting;
        }
        var row = await session.QuerySingleOrDefaultAsync("select source, allow_stale_submit, version from integrations.hr_leave_setting", null,
            r => (Source: r.GetString(0), Stale: r.GetBoolean(1), Version: r.GetInt32(2)), ct);
        var (source, stale, version) = row == default ? (HrLeaveSources.TeamBeats, false, 0) : row;
        var fromHr = source == HrLeaveSources.Hr && await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct);
        return _setting = new HrLeaveSetting(source, stale, version, fromHr);
    }

    /// <summary>The HR administrator chooses the source (FR-013). The change is versioned and audited.</summary>
    public async Task<HrLeaveSetting> PutSettingAsync(string? ifMatch, HrLeaveSettingRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrIntegration, ct);
        await authz.DemandAsync(Permissions.HrIntegrationManage, AuthzTarget.Tenant, ct);
        var current = await SettingAsync(ct);
        if (ifMatch != ETag(current.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.hr.leave_setting_changed", null,
                new Dictionary<string, object?> { ["etag"] = ETag(current.Version) });
        }
        var source = request.Source ?? current.Source;
        if (!HrLeaveSources.All.Contains(source))
        {
            throw AppException.Validation("source", "validation.hr.leave_source");
        }
        var stale = request.AllowStaleSubmit ?? current.AllowStaleSubmit;
        await session.ExecAsync(
            """
            insert into integrations.hr_leave_setting (tenant_id, source, allow_stale_submit, version, updated_at, updated_by)
            values (platform.current_tenant_id(), @source, @stale, 1, @now, @actor)
            on conflict (tenant_id) do update set source = excluded.source, allow_stale_submit = excluded.allow_stale_submit,
              version = integrations.hr_leave_setting.version + 1, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, new { source, stale, now = clock.UtcNow, actor = context.ActorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLeaveSourceChanged, "hr_leave_setting", "tenant",
            Changes: new { from = current.Source, to = source, allowStaleSubmit = stale }), ct);
        _setting = null;
        return await SettingAsync(ct);
    }

    public async Task<HrLeaveBalances> BalancesAsync(Guid userId, CancellationToken ct = default)
    {
        if (_balances.TryGetValue(userId, out var cached))
        {
            return cached;
        }
        var result = await ReadBalancesAsync(userId, ct);
        _balances[userId] = result;
        return result;
    }

    private async Task<HrLeaveBalances> ReadBalancesAsync(Guid userId, CancellationToken ct)
    {
        if (!(await SettingAsync(ct)).FromHr)
        {
            return new HrLeaveBalances(HrLeaveBalances.Unavailable, [], null);
        }
        if (await records.LinkAsync(userId, ct) is not { } link)
        {
            return new HrLeaveBalances(HrLeaveBalances.NotMatched, [], null);
        }
        if (link.Connection is { } connection)
        {
            try
            {
                var (balances, _) = await rest.ReadLeaveBalancesAsync(connection, link.Record, ct);
                if (balances is not null)
                {
                    var now = clock.UtcNow;
                    await session.ExecAsync("delete from integrations.hr_leave_balance where user_id = @userId", new { userId }, ct);
                    await session.ExecAsync(
                        """
                        insert into integrations.hr_leave_balance (tenant_id, user_id, type_key, balance, read_at)
                        select platform.current_tenant_id(), @userId, k, b, @now from unnest(@keys, @values) as x(k, b)
                        """, new { userId, keys = balances.Select(b => b.TypeKey).ToArray(), values = balances.Select(b => b.Balance).ToArray(), now }, ct);
                    return new HrLeaveBalances(HrLeaveBalances.Ok, balances, now);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // HR did not answer: the last known balances follow.
            }
        }
        var known = await session.QueryAsync("select type_key, balance, read_at from integrations.hr_leave_balance where user_id = @userId", new { userId },
            r => (Balance: new HrLeaveBalance(r.GetString(0), r.GetDecimal(1)), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        return known.Count == 0
            ? new HrLeaveBalances(HrLeaveBalances.Unavailable, [], null)
            : new HrLeaveBalances(HrLeaveBalances.Stale, known.Select(k => k.Balance).ToList(), known.Min(k => k.At));
    }

    public async Task<string> HandOffAsync(HrLeaveHandOffRequest request, int change, CancellationToken ct = default)
    {
        if (!(await SettingAsync(ct)).FromHr)
        {
            return HrLeaveHandOffStates.Unavailable;
        }
        var existing = await session.ScalarAsync<string?>("select state from integrations.hr_leave_handoff where request_id = @id and change = @change",
            new { id = request.RequestId, change }, ct);
        if (existing is not null)
        {
            return existing;
        }
        var link = await records.LinkAsync(request.UserId, ct);
        var employeeNumber = link is null ? null : await session.ScalarAsync<string?>(
            "select data->>'employee_number' from integrations.hr_employee where record_id = @record", new { record = link.Record }, ct);
        var input = JsonSerializer.SerializeToElement(new
        {
            request_id = request.RequestId.ToString(),
            change = change.ToString(CultureInfo.InvariantCulture),
            kind = request.Kind,
            record_id = link?.Record,
            employee_number = employeeNumber,
            type = request.TypeKey,
            start = request.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end = request.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            start_half = request.StartHalf ? "true" : "false",
            end_half = request.EndHalf ? "true" : "false",
            days = request.Days.ToString(CultureInfo.InvariantCulture),
        });
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into integrations.hr_leave_handoff (tenant_id, id, request_id, change, kind, user_id, input, state, created_at, next_retry_at)
            values (platform.current_tenant_id(), @id, @requestId, @change, @kind, @userId, @input, 'queued', @now, @now)
            """, new { id, requestId = request.RequestId, change, kind = request.Kind, userId = request.UserId, input = Jsonb.From(input), now = clock.UtcNow }, ct);
        await SendAsync(id, clock.UtcNow, ct);
        return HrLeaveHandOffStates.Queued;
    }

    private async Task<Guid?> ConnectionAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            return null;
        }
        var rows = await session.QueryAsync("select id, settings::text from integrations.connection where kind = @kind and enabled order by created_at, id",
            new { kind = HrRestPack.KindName }, r => (Id: r.GetGuid(0), Settings: r.GetString(1)), ct);
        return rows.Where(r => HrRestPack.Parse(JsonDocument.Parse(r.Settings).RootElement)?.LeaveSubmitPath is not null).Select(r => (Guid?)r.Id).FirstOrDefault();
    }

    /// <summary>Queues the job for one hand-off; without a usable connection it waits for the next retry.</summary>
    private async Task SendAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select request_id, change, input::text, attempt, input->>'record_id' as record_id, created_at from integrations.hr_leave_handoff where id = @id and state = 'queued' for update",
            new { id }, r => (Request: r.GetGuid(0), Change: r.GetInt32(1), Input: r.GetString(2), Attempt: r.GetInt32(3), Record: r.Get<string>("record_id"),
                Created: r.GetFieldValue<DateTimeOffset>(5)), ct);
        if (row == default)
        {
            return;
        }
        if (row.Record is null || await ConnectionAsync(ct) is not { } connection)
        {
            var error = row.Record is null ? "not_matched" : "no_connection";
            if (now - row.Created >= RetryWindow)
            {
                await FinishAsync(id, HrLeaveHandOffStates.Failed, null, error, ct);
                return;
            }
            await session.ExecAsync("update integrations.hr_leave_handoff set next_retry_at = @next, error_class = @error where id = @id",
                new { id, next = now + RetryEvery, error }, ct);
            return;
        }
        var attempt = row.Attempt + 1;
        await session.ExecAsync("update integrations.hr_leave_handoff set attempt = @attempt, connection_id = @connection, next_retry_at = null where id = @id",
            new { id, attempt, connection }, ct);
        var key = $"hr-leave:{row.Request:N}:{row.Change}:{attempt}";
        var result = await runtime.EnqueueAsync(new ConnectorCall(connection, HrRestPack.SubmitLeave, JsonDocument.Parse(row.Input).RootElement, key,
            $"{CorrelationPrefix}{id:N}:{attempt}"), ct);
        if (result.Outcome == ConnectorOutcomes.Refused)
        {
            await RetryLaterAsync(id, result.ErrorClass, ct);
        }
    }

    /// <summary>A technical failure waits an hour; three days after the hand-off (or its manual resend) it is failed.</summary>
    private async Task RetryLaterAsync(Guid id, string? errorClass, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var created = await session.ScalarAsync<DateTimeOffset>("select created_at from integrations.hr_leave_handoff where id = @id", new { id }, ct);
        if (now - created >= RetryWindow)
        {
            await FinishAsync(id, HrLeaveHandOffStates.Failed, null, errorClass, ct);
            return;
        }
        await session.ExecAsync("update integrations.hr_leave_handoff set next_retry_at = @next, error_class = @errorClass where id = @id",
            new { id, next = now + RetryEvery, errorClass }, ct);
    }

    private async Task FinishAsync(Guid id, string state, string? reference, string? errorClass, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            update integrations.hr_leave_handoff set state = @state, external_reference = @reference, error_class = @errorClass, next_retry_at = null,
              recorded_at = case when @state = 'recorded' then @now else recorded_at end
            where id = @id and state = 'queued'
            returning request_id, kind, change
            """, new { id, state, reference, errorClass, now = clock.UtcNow }, r => (Request: r.GetGuid(0), Kind: r.GetString(1), Change: r.GetInt32(2)), ct);
        if (row != default)
        {
            await outbox.EnqueueAsync(new HrLeaveHandOffChanged(row.Request, row.Kind, state, reference, row.Change), ct);
        }
    }

    /// <summary>A submit job finished. A technical failure waits for the next hourly retry; an answer for an earlier attempt is ignored.</summary>
    public async Task OnCompletedAsync(ConnectorActionCompleted done, CancellationToken ct)
    {
        if (done.Action != HrRestPack.SubmitLeave || done.CorrelationId is not { } correlation || !correlation.StartsWith(CorrelationPrefix, StringComparison.Ordinal)
            || correlation[CorrelationPrefix.Length..].Split(':') is not [var idText, var attemptText]
            || !Guid.TryParseExact(idText, "N", out var id) || !int.TryParse(attemptText, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt)
            || !await session.ScalarAsync<bool>("select exists (select 1 from integrations.hr_leave_handoff where id = @id and attempt = @attempt and state = 'queued')",
                new { id, attempt }, ct))
        {
            return;
        }
        switch (done.Outcome)
        {
            case ConnectorOutcomes.Succeeded:
                await FinishAsync(id, HrLeaveHandOffStates.Recorded, done.ExternalReference, null, ct);
                break;
            case ConnectorOutcomes.BusinessError or ConnectorOutcomes.Unknown:
                // HR refused it, or may hold it without telling us: a person has to look.
                await FinishAsync(id, HrLeaveHandOffStates.Failed, done.ExternalReference, done.ErrorCode ?? done.ErrorClass ?? done.Outcome, ct);
                break;
            default:
                await RetryLaterAsync(id, done.ErrorClass, ct);
                break;
        }
    }

    /// <summary>Sends the queued hand-offs whose retry time has come (each connector tick).</summary>
    public async Task<int> RetryDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            "select id from integrations.hr_leave_handoff where state = 'queued' and next_retry_at <= @now order by next_retry_at, id limit @limit",
            new { now, limit }, r => r.GetGuid(0), ct);
        foreach (var id in due)
        {
            await SendAsync(id, now, ct);
        }
        return due.Count;
    }

    public async Task<IReadOnlyList<HrLeaveHandOffRow>> ListAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.HrIntegrationManage, AuthzTarget.Tenant, ct);
        return await session.QueryAsync(
            """
            select id, request_id, change, kind, state, attempt, external_reference, error_class, created_at, recorded_at, next_retry_at
            from integrations.hr_leave_handoff order by created_at desc, id desc limit 100
            """, null,
            r => new HrLeaveHandOffRow(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.Get<string>("external_reference"),
                r.Get<string>("error_class"), r.GetFieldValue<DateTimeOffset>(8), r.Get<DateTimeOffset?>("recorded_at"), r.Get<DateTimeOffset?>("next_retry_at")), ct);
    }

    /// <summary>The HR administrator sends a failed hand-off again (after fixing it in HR or in the connection).</summary>
    public async Task RetryAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.HrIntegrationManage, AuthzTarget.Tenant, ct);
        var now = clock.UtcNow;
        var row = await session.QuerySingleOrDefaultAsync(
            """
            update integrations.hr_leave_handoff set state = 'queued', error_class = null, external_reference = null, next_retry_at = @now, created_at = @now
            where id = @id and state = 'failed' returning request_id, kind, change
            """, new { id, now }, r => (Request: r.GetGuid(0), Kind: r.GetString(1), Change: r.GetInt32(2)), ct);
        if (row == default)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLeaveHandOffRetried, "hr_leave_handoff", id.ToString(),
            Changes: new { requestId = row.Request, kind = row.Kind }), ct);
        await outbox.EnqueueAsync(new HrLeaveHandOffChanged(row.Request, row.Kind, HrLeaveHandOffStates.Queued, null, row.Change), ct);
        await SendAsync(id, now, ct);
    }
}

/// <summary>Leave hand-off jobs that finished (spec 019 US7).</summary>
public sealed class HrLeaveSubmitFinished(HrLeave leave) : IMessageHandler<ConnectorActionCompleted>
{
    public Task HandleAsync(ConnectorActionCompleted message, CancellationToken ct) => leave.OnCompletedAsync(message, ct);
}
