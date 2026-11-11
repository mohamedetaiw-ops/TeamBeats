using System.Globalization;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>
/// Employee data changes written back to HR (spec 022 FR-019 through spec 019). Each approved change is one durable
/// connector job on the first enabled HR REST connection with a data change address. When HR is unavailable it stays
/// queued and is sent again every hour for three days, then it fails. The values are cleared from this table when HR
/// records or refuses the change, and the runtime drops a run's input when it finishes, so nothing sensitive stays.
/// </summary>
public sealed class HrDataChange(
    IDbSession session,
    IClock clock,
    IEntitlementService entitlements,
    IOutbox outbox,
    IConnectorRuntime runtime,
    HrRecords records) : IHrDataChange
{
    public const string CorrelationPrefix = "hr-data-change:";
    public static readonly TimeSpan RetryEvery = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryWindow = TimeSpan.FromDays(3);

    private async Task<Guid?> ConnectionAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            return null;
        }
        var rows = await session.QueryAsync("select id, settings::text from integrations.connection where kind = @kind and enabled order by created_at, id",
            new { kind = HrRestPack.KindName }, r => (Id: r.GetGuid(0), Settings: r.GetString(1)), ct);
        return rows.Where(r => HrRestPack.Parse(JsonDocument.Parse(r.Settings).RootElement)?.DataChangePath is not null).Select(r => (Guid?)r.Id).FirstOrDefault();
    }

    public async Task<bool> AvailableAsync(CancellationToken ct = default) => await ConnectionAsync(ct) is not null;

    public async Task<string> HandOffAsync(HrDataChangeHandOffRequest request, CancellationToken ct = default)
    {
        var existing = await session.ScalarAsync<string?>("select state from integrations.hr_data_change_handoff where request_id = @id", new { id = request.RequestId }, ct);
        if (existing is not null)
        {
            return existing;
        }
        if (await ConnectionAsync(ct) is null)
        {
            return HrDataChangeHandOffStates.Unavailable;
        }
        var link = await records.LinkAsync(request.UserId, ct);
        var employeeNumber = link is null ? null : await session.ScalarAsync<string?>(
            "select data->>'employee_number' from integrations.hr_employee where record_id = @record", new { record = link.Record }, ct);
        var input = JsonSerializer.SerializeToElement(new
        {
            request_id = request.RequestId.ToString(),
            record_id = link?.Record,
            employee_number = employeeNumber,
            group = request.Group,
            values = request.Values,
        });
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into integrations.hr_data_change_handoff (tenant_id, id, request_id, user_id, input, state, created_at, next_retry_at)
            values (platform.current_tenant_id(), @id, @requestId, @userId, @input, 'queued', @now, @now)
            """, new { id, requestId = request.RequestId, userId = request.UserId, input = Jsonb.From(input), now }, ct);
        await SendAsync(id, now, ct);
        return await session.ScalarAsync<string>("select state from integrations.hr_data_change_handoff where id = @id", new { id }, ct) ?? HrDataChangeHandOffStates.Queued;
    }

    /// <summary>Queues the job for one hand-off; without a matched record or a usable connection it waits for the next retry.</summary>
    private async Task SendAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select request_id, input::text, attempt, input->>'record_id' as record_id, created_at from integrations.hr_data_change_handoff where id = @id and state = 'queued' for update",
            new { id }, r => (Request: r.GetGuid(0), Input: r.GetString(1), Attempt: r.GetInt32(2), Record: r.Get<string>("record_id"), Created: r.GetFieldValue<DateTimeOffset>(4)), ct);
        if (row == default)
        {
            return;
        }
        if (row.Record is null || await ConnectionAsync(ct) is not { } connection)
        {
            var error = row.Record is null ? "not_matched" : "no_connection";
            if (now - row.Created >= RetryWindow)
            {
                await FinishAsync(id, HrDataChangeHandOffStates.Failed, null, error, ct);
                return;
            }
            await session.ExecAsync("update integrations.hr_data_change_handoff set next_retry_at = @next, error_class = @error where id = @id",
                new { id, next = now + RetryEvery, error }, ct);
            return;
        }
        var attempt = row.Attempt + 1;
        await session.ExecAsync("update integrations.hr_data_change_handoff set attempt = @attempt, connection_id = @connection, next_retry_at = null where id = @id",
            new { id, attempt, connection }, ct);
        var result = await runtime.EnqueueAsync(new ConnectorCall(connection, HrRestPack.SubmitDataChange, JsonDocument.Parse(row.Input).RootElement,
            $"hr-data-change:{row.Request:N}:{attempt}", $"{CorrelationPrefix}{id:N}:{attempt}"), ct);
        if (result.Outcome == ConnectorOutcomes.Refused)
        {
            await RetryLaterAsync(id, result.ErrorClass, ct);
        }
    }

    private async Task RetryLaterAsync(Guid id, string? errorClass, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var created = await session.ScalarAsync<DateTimeOffset>("select created_at from integrations.hr_data_change_handoff where id = @id", new { id }, ct);
        if (now - created >= RetryWindow)
        {
            await FinishAsync(id, HrDataChangeHandOffStates.Failed, null, errorClass, ct);
            return;
        }
        await session.ExecAsync("update integrations.hr_data_change_handoff set next_retry_at = @next, error_class = @errorClass where id = @id",
            new { id, next = now + RetryEvery, errorClass }, ct);
    }

    /// <summary>Ends the hand-off and clears its values, whatever HR answered.</summary>
    private async Task FinishAsync(Guid id, string state, string? reference, string? errorClass, CancellationToken ct)
    {
        var request = await session.QuerySingleOrDefaultAsync(
            """
            update integrations.hr_data_change_handoff set state = @state, input = null, external_reference = @reference, error_class = @errorClass,
              next_retry_at = null, recorded_at = case when @state = 'recorded' then @now else recorded_at end
            where id = @id and state = 'queued'
            returning request_id
            """, new { id, state, reference, errorClass, now = clock.UtcNow }, r => (Guid?)r.GetGuid(0), ct);
        if (request is { } requestId)
        {
            // The runs keep their input after a technical failure for the run history; this one's values go now.
            await session.ExecAsync("update integrations.run set input = null where action = @action and correlation_id like @prefix",
                new { action = HrRestPack.SubmitDataChange, prefix = $"{CorrelationPrefix}{id:N}:%" }, ct);
            await outbox.EnqueueAsync(new HrDataChangeHandOffChanged(requestId, state, reference), ct);
        }
    }

    /// <summary>A submit job finished. A technical failure waits for the next hourly retry; an answer for an earlier attempt is ignored.</summary>
    public async Task OnCompletedAsync(ConnectorActionCompleted done, CancellationToken ct)
    {
        if (done.Action != HrRestPack.SubmitDataChange || done.CorrelationId is not { } correlation || !correlation.StartsWith(CorrelationPrefix, StringComparison.Ordinal)
            || correlation[CorrelationPrefix.Length..].Split(':') is not [var idText, var attemptText]
            || !Guid.TryParseExact(idText, "N", out var id) || !int.TryParse(attemptText, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt)
            || !await session.ScalarAsync<bool>("select exists (select 1 from integrations.hr_data_change_handoff where id = @id and attempt = @attempt and state = 'queued')",
                new { id, attempt }, ct))
        {
            return;
        }
        switch (done.Outcome)
        {
            case ConnectorOutcomes.Succeeded:
                await FinishAsync(id, HrDataChangeHandOffStates.Recorded, done.ExternalReference, null, ct);
                break;
            case ConnectorOutcomes.BusinessError or ConnectorOutcomes.Unknown:
                await FinishAsync(id, HrDataChangeHandOffStates.Failed, done.ExternalReference, done.ErrorCode ?? done.ErrorClass ?? done.Outcome, ct);
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
            "select id from integrations.hr_data_change_handoff where state = 'queued' and next_retry_at <= @now order by next_retry_at, id limit @limit",
            new { now, limit }, r => r.GetGuid(0), ct);
        foreach (var id in due)
        {
            await SendAsync(id, now, ct);
        }
        return due.Count;
    }
}

/// <summary>Data change hand-off jobs that finished (spec 022 FR-019).</summary>
public sealed class HrDataChangeSubmitFinished(HrDataChange changes) : IMessageHandler<ConnectorActionCompleted>
{
    public Task HandleAsync(ConnectorActionCompleted message, CancellationToken ct) => changes.OnCompletedAsync(message, ct);
}
