using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>
/// The connector runtime (spec 017 FR-005 to FR-010, US2 and US3). Each call is a run row keyed by connection, action and
/// idempotency key, so a repeated key never acts twice: a finished run answers again with its stored outcome, a running
/// one answers "in progress", and a failed one runs again. Attempts are recorded for a circuit breaker every instance
/// shares, and a per-connection token bucket limits the rate. A technical error is retried with backoff and jitter only
/// when it is safe: the attempt never reached the system, the action is a read, or the action is idempotent. Otherwise a
/// write that may have acted stops as "outcome unknown". Input is kept while the run may still run or be replayed, and
/// cleared once it succeeded; outputs are returned to the caller and never stored.
/// </summary>
public sealed class ConnectorRuntime(
    IDbSession session,
    ITenantContext context,
    IEnumerable<IConnectorPack> packs,
    IEntitlementService entitlements,
    IOutbox outbox,
    IClock clock,
    ConnectorRateLimits rateLimits,
    ILogger<ConnectorRuntime> logger) : IConnectorRuntime
{
    private sealed record Connection(Guid Id, string Kind, int PackVersion, string? ExternalId, bool Enabled, int PerMinute, string Settings,
        DateTimeOffset? CircuitOpenedAt, string Status);

    private sealed record Resolved(Connection Connection, IConnectorPack Pack, ConnectorActionDefinition Action);

    private sealed record Run(Guid Id, Guid ConnectionId, string Action, string Mode, string Key, string? CorrelationId, string Status,
        int Attempts, int MaxAttempts, string? ExternalReference, string? ErrorClass, string? ErrorCode, string? Input, int DurationMs);

    /// <summary>One attempt, or the reason it was not made (circuit open, rate limited).</summary>
    private sealed record Attempt(PackCallResult? Result, string? NotCalled, double Milliseconds);

    private static readonly string[] Finished = [ConnectorOutcomes.Succeeded, ConnectorOutcomes.BusinessError];
    private static readonly string[] Pending = ["queued", "running", "retrying"];

    public async Task<ConnectorCallResult> InvokeAsync(ConnectorCall call, CancellationToken ct = default)
    {
        Validate(call);
        var (resolved, refusal, connection) = await ResolveAsync(call.ConnectionId, call.Action, ct);
        if (connection is null)
        {
            return new ConnectorCallResult(null, ConnectorOutcomes.Refused, ErrorClass: ConnectorErrorClasses.ConnectionMissing);
        }
        var start = await StartAsync(call, "inline", RuntimePolicy.InlineAttempts, ct);
        if (start.Answer is not null)
        {
            return start.Answer;
        }
        var runId = start.RunId!.Value;
        if (refusal is not null)
        {
            await FinishAsync(runId, ConnectorOutcomes.Refused, 0, 0, null, refusal, null, null, ct);
            return new ConnectorCallResult(runId, ConnectorOutcomes.Refused, ErrorClass: refusal);
        }

        var deadline = Stopwatch.StartNew();
        var attempts = 0;
        double elapsed = 0;
        while (true)
        {
            attempts++;
            var remaining = RuntimePolicy.InlineBudget - deadline.Elapsed;
            var attempt = await AttemptAsync(resolved!, call.Input, call.IdempotencyKey, attempts, remaining, waitForRate: true, ct);
            elapsed += attempt.Milliseconds;
            if (attempt.NotCalled is not null)
            {
                await FinishAsync(runId, ConnectorOutcomes.Failed, attempts - 1, (int)elapsed, null, attempt.NotCalled, null, null, ct);
                return new ConnectorCallResult(runId, ConnectorOutcomes.Failed, ErrorClass: attempt.NotCalled);
            }
            var r = attempt.Result!;
            var outcome = Classify(resolved!.Action, r, attempts, RuntimePolicy.InlineAttempts);
            if (outcome == "retry")
            {
                var delay = RuntimePolicy.Jitter(RuntimePolicy.InlineDelays[Math.Min(attempts - 1, RuntimePolicy.InlineDelays.Length - 1)]);
                if (r.RetryAfter is { } advice && advice > delay)
                {
                    delay = advice;
                }
                if (deadline.Elapsed + delay < RuntimePolicy.InlineBudget)
                {
                    await Task.Delay(delay, ct);
                    continue;
                }
                outcome = ConnectorOutcomes.Failed;
            }
            await FinishAsync(runId, outcome, attempts, (int)elapsed, r.ExternalReference, r.ErrorClass, r.ErrorCode, r.RecordCount, ct, r.Output);
            return new ConnectorCallResult(runId, outcome, outcome == ConnectorOutcomes.Succeeded ? r.Output : null, r.ExternalReference, r.ErrorClass, r.ErrorCode);
        }
    }

    public async Task<ConnectorCallResult> EnqueueAsync(ConnectorCall call, CancellationToken ct = default)
    {
        Validate(call);
        var (_, refusal, connection) = await ResolveAsync(call.ConnectionId, call.Action, ct);
        if (connection is null)
        {
            return new ConnectorCallResult(null, ConnectorOutcomes.Refused, ErrorClass: ConnectorErrorClasses.ConnectionMissing);
        }
        if (refusal == ConnectorErrorClasses.UnknownAction)
        {
            return new ConnectorCallResult(null, ConnectorOutcomes.Refused, ErrorClass: refusal);
        }
        var start = await StartAsync(call, "job", RuntimePolicy.JobAttempts, ct);
        if (start.Answer is not null)
        {
            return start.Answer;
        }
        // Disabled or outside the package: the job waits, and its error class tells the operator why.
        await session.ExecAsync("update integrations.run set status = 'queued', next_attempt_at = @now, error_class = @refusal where id = @id",
            new { id = start.RunId, now = clock.UtcNow, refusal }, ct);
        return new ConnectorCallResult(start.RunId, ConnectorOutcomes.Queued, ErrorClass: refusal);
    }

    /// <summary>Runs one attempt of each due job (spec 017 FR-005), oldest first.</summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        var due = await session.QueryAsync(
            """
            select id from integrations.run
            where status in ('queued', 'retrying') and next_attempt_at <= @now
            order by next_attempt_at, id limit @limit for update skip locked
            """, new { now, limit }, r => r.GetGuid(0), ct);
        foreach (var id in due)
        {
            var run = await LoadRunAsync(id, ct);
            if (run is null)
            {
                continue;
            }
            await RunJobAttemptAsync(run, ct);
        }
        return due.Count;
    }

    private async Task RunJobAttemptAsync(Run run, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var (resolved, refusal, connection) = await ResolveAsync(run.ConnectionId, run.Action, ct);
        if (connection is null || refusal == ConnectorErrorClasses.UnknownAction)
        {
            await FinishJobAsync(run, ConnectorOutcomes.Refused, run.Attempts, run.DurationMs, null, refusal ?? ConnectorErrorClasses.ConnectionMissing, null, null, ct);
            return;
        }
        if (refusal is not null)
        {
            await RescheduleAsync(run.Id, now + RuntimePolicy.PausedRecheck, run.Attempts, refusal, "queued", ct);
            return;
        }
        var input = JsonDocument.Parse(run.Input ?? "{}").RootElement;
        var attemptNo = run.Attempts + 1;
        var attempt = await AttemptAsync(resolved!, input, run.Key, attemptNo, RuntimePolicy.JobAttemptTimeout, waitForRate: false, ct);
        var duration = run.DurationMs + (int)attempt.Milliseconds;
        if (attempt.NotCalled == ConnectorErrorClasses.CircuitOpen)
        {
            var opened = connection.CircuitOpenedAt ?? now;
            await RescheduleAsync(run.Id, Max(opened + RuntimePolicy.CircuitHalfOpenAfter, now + RuntimePolicy.RateLimitedRecheck), run.Attempts,
                ConnectorErrorClasses.CircuitOpen, run.Status, ct);
            return;
        }
        if (attempt.NotCalled is not null)
        {
            await RescheduleAsync(run.Id, now + RuntimePolicy.RateLimitedRecheck, run.Attempts, attempt.NotCalled, run.Status, ct);
            return;
        }
        var r = attempt.Result!;
        var outcome = Classify(resolved!.Action, r, attemptNo, run.MaxAttempts);
        if (outcome == "retry")
        {
            var delay = RuntimePolicy.JobDelay(attemptNo);
            if (r.RetryAfter is { } advice && advice > delay)
            {
                delay = advice;
            }
            await session.ExecAsync(
                """
                update integrations.run set status = 'retrying', attempts = @attempts, next_attempt_at = @next, error_class = @errorClass,
                  duration_ms = @duration
                where id = @id
                """, new { id = run.Id, attempts = attemptNo, next = now + delay, errorClass = r.ErrorClass, duration }, ct);
            return;
        }
        await FinishJobAsync(run, outcome, attemptNo, duration, r.ExternalReference, r.ErrorClass, r.ErrorCode, r.RecordCount, ct, r.Output);
    }

    private async Task FinishJobAsync(Run run, string outcome, int attempts, int duration, string? reference, string? errorClass, string? errorCode,
        int? records, CancellationToken ct, JsonElement? output = null)
    {
        await FinishAsync(run.Id, outcome, attempts, duration, reference, errorClass, errorCode, records, ct, output);
        await outbox.EnqueueAsync(new ConnectorActionCompleted(run.Id, run.ConnectionId, run.Action, outcome, reference, errorClass, errorCode, run.CorrelationId), ct);
    }

    private Task RescheduleAsync(Guid id, DateTimeOffset next, int attempts, string errorClass, string status, CancellationToken ct) =>
        session.ExecAsync("update integrations.run set status = @status, next_attempt_at = @next, attempts = @attempts, error_class = @errorClass where id = @id",
            new { id, next, attempts, errorClass, status = status == "retrying" ? "retrying" : "queued" }, ct);

    /// <summary>"retry", or the final outcome of the run after this attempt.</summary>
    private static string Classify(ConnectorActionDefinition action, PackCallResult r, int attempts, int maxAttempts)
    {
        switch (r.Kind)
        {
            case PackResultKind.Ok:
                return ConnectorOutcomes.Succeeded;
            case PackResultKind.BusinessError:
                return ConnectorOutcomes.BusinessError;
            case PackResultKind.Fatal:
                return ConnectorOutcomes.Failed;
        }
        var safe = !r.MayHaveActed || action.Idempotent || action.SideEffect == ConnectorSideEffects.Read;
        if (!safe)
        {
            return ConnectorOutcomes.Unknown;
        }
        return attempts < maxAttempts ? "retry" : ConnectorOutcomes.Failed;
    }

    private async Task<Attempt> AttemptAsync(Resolved resolved, JsonElement input, string key, int attemptNo, TimeSpan timeout, bool waitForRate,
        CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var c = resolved.Connection;
        var now = clock.UtcNow;
        var opened = (await session.QuerySingleOrDefaultAsync("select circuit_opened_at from integrations.connection where id = @id", new { id = c.Id },
            r => new { At = r.Get<DateTimeOffset?>("circuit_opened_at") }, ct))?.At;
        var halfOpen = false;
        if (opened is { } at)
        {
            if (now - at < RuntimePolicy.CircuitHalfOpenAfter)
            {
                return new Attempt(null, ConnectorErrorClasses.CircuitOpen, 0);
            }
            halfOpen = true;
        }

        var limiter = rateLimits.For(tenantId, c.Id, c.PerMinute);
        using var lease = waitForRate
            ? await AcquireAsync(limiter, ct)
            : limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            return new Attempt(null, ConnectorErrorClasses.RateLimited, 0);
        }

        var started = Stopwatch.GetTimestamp();
        PackCallResult result;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMilliseconds(1));
            try
            {
                result = await resolved.Pack.CallAsync(new PackCall(ToPack(c), resolved.Action.Name, input, key, attemptNo), cts.Token);
            }
            catch (Exception ex) when (Blocked(ex))
            {
                result = PackCallResult.Fatal(ConnectorErrorClasses.BlockedAddress);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: true);
            }
            catch (HttpRequestException ex)
            {
                result = PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: ex.HttpRequestError != HttpRequestError.ConnectionError
                    && ex.HttpRequestError != HttpRequestError.NameResolutionError);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Connector pack {Kind} failed on {Action}", c.Kind, resolved.Action.Name);
                result = PackCallResult.Technical(ConnectorErrorClasses.Unexpected, mayHaveActed: true);
            }
        }
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var ok = result.Kind is PackResultKind.Ok or PackResultKind.BusinessError;
        ConnectorTelemetry.Record(tenantId, c.Kind, resolved.Action.Name, ok ? (result.Kind == PackResultKind.Ok ? "ok" : "business_error") : result.ErrorClass ?? "error", ms);
        await RecordAttemptAsync(c, ok, halfOpen, ct);
        return new Attempt(result, null, ms);
    }

    private static async Task<RateLimitLease> AcquireAsync(RateLimiter limiter, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            return await limiter.AcquireAsync(1, wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new NotAcquired();
        }
    }

    private sealed class NotAcquired : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private static bool Blocked(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is BlockedAddressException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Records the attempt and moves the circuit: a success in half-open state closes it, a failure there opens it again,
    /// and a failure ratio of half or more over the last 20 attempts in 5 minutes opens it (spec 017 US3 scenario 4).
    /// </summary>
    private async Task RecordAttemptAsync(Connection c, bool ok, bool halfOpen, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await session.ExecAsync("insert into integrations.call_attempt (tenant_id, connection_id, at, ok) values (platform.current_tenant_id(), @id, @now, @ok)",
            new { id = c.Id, now, ok }, ct);
        if (ok)
        {
            if (halfOpen)
            {
                var closed = await session.ExecAsync(
                    """
                    update integrations.connection set circuit_opened_at = null,
                      status = case when status = 'degraded' then 'healthy' else status end, updated_at = @now
                    where id = @id and circuit_opened_at is not null
                    """, new { id = c.Id, now }, ct);
                if (closed > 0)
                {
                    await outbox.EnqueueAsync(new ConnectorRestored(c.Id), ct);
                }
            }
            return;
        }
        if (halfOpen)
        {
            await session.ExecAsync("update integrations.connection set circuit_opened_at = @now where id = @id", new { id = c.Id, now }, ct);
            return;
        }
        var (total, failures) = await session.QuerySingleOrDefaultAsync(
            """
            select count(*), count(*) filter (where not ok) from (
              select ok from integrations.call_attempt where connection_id = @id and at > @since order by at desc limit @n) a
            """, new { id = c.Id, since = now - RuntimePolicy.CircuitWindow, n = RuntimePolicy.CircuitMinimumCalls },
            r => (r.GetInt64(0), r.GetInt64(1)), ct);
        if (total >= RuntimePolicy.CircuitMinimumCalls && failures * 2 >= total)
        {
            var opened = await session.ExecAsync(
                """
                update integrations.connection set circuit_opened_at = @now,
                  status = case when status = 'healthy' then 'degraded' else status end, updated_at = @now
                where id = @id and circuit_opened_at is null
                """, new { id = c.Id, now }, ct);
            if (opened > 0)
            {
                await outbox.EnqueueAsync(new ConnectorUnavailable(c.Id, ConnectionStatuses.Degraded), ct);
            }
        }
    }

    private sealed record Start(Guid? RunId, ConnectorCallResult? Answer);

    /// <summary>Claims the run for this key: a new row, a failed one run again, or the stored answer of a finished or running one.</summary>
    private async Task<Start> StartAsync(ConnectorCall call, string mode, int maxAttempts, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var input = new Jsonb(call.Input.ValueKind == JsonValueKind.Undefined ? "{}" : call.Input.GetRawText());
        var id = await session.ScalarAsync<Guid?>(
            """
            insert into integrations.run (tenant_id, id, connection_id, action, mode, idempotency_key, correlation_id, status, max_attempts, input, created_at)
            values (platform.current_tenant_id(), @id, @connection, @action, @mode, @key, @correlation, 'running', @max, @input, @now)
            on conflict (tenant_id, connection_id, action, idempotency_key) do nothing
            returning id
            """, new { id = Ids.New(), connection = call.ConnectionId, action = call.Action, mode, key = call.IdempotencyKey, correlation = call.CorrelationId,
                max = maxAttempts, input, now }, ct);
        if (id is not null)
        {
            return new Start(id, null);
        }
        var existing = await session.QuerySingleOrDefaultAsync(
            """
            select id, status, external_reference, error_class, error_code from integrations.run
            where connection_id = @connection and action = @action and idempotency_key = @key
            """, new { connection = call.ConnectionId, action = call.Action, key = call.IdempotencyKey },
            r => (Id: r.GetGuid(0), Status: r.GetString(1), Reference: r.Get<string>("external_reference"), ErrorClass: r.Get<string>("error_class"),
                ErrorCode: r.Get<string>("error_code")), ct);
        if (Finished.Contains(existing.Status))
        {
            return new Start(null, new ConnectorCallResult(existing.Id, existing.Status, null, existing.Reference, existing.ErrorClass, existing.ErrorCode, Repeated: true));
        }
        if (Pending.Contains(existing.Status))
        {
            return new Start(null, new ConnectorCallResult(existing.Id, ConnectorOutcomes.InProgress));
        }
        var claimed = await session.ScalarAsync<Guid?>(
            """
            update integrations.run set status = 'running', mode = @mode, attempts = 0, max_attempts = @max, input = @input, error_class = null,
              error_code = null, finished_at = null, next_attempt_at = null, duration_ms = null
            where id = @id and status in ('failed', 'unknown', 'refused')
            returning id
            """, new { id = existing.Id, mode, max = maxAttempts, input }, ct);
        return claimed is null
            ? new Start(null, new ConnectorCallResult(existing.Id, ConnectorOutcomes.InProgress))
            : new Start(claimed, null);
    }

    private async Task FinishAsync(Guid id, string outcome, int attempts, int duration, string? reference, string? errorClass, string? errorCode, int? records,
        CancellationToken ct, JsonElement? output = null)
    {
        await PayloadCapture.KeepAsync(session, id, output, clock.UtcNow, ct);
        await session.ExecAsync(
            """
            update integrations.run set status = @outcome, attempts = @attempts, duration_ms = @duration, external_reference = @reference,
              error_class = @errorClass, error_code = @errorCode, record_count = @records, finished_at = @now, next_attempt_at = null,
              input = case when @outcome in ('succeeded', 'business_error') then null else input end
            where id = @id
            """, new { id, outcome, attempts, duration, reference, errorClass, errorCode, records, now = clock.UtcNow }, ct);
    }

    private async Task<(Resolved? Resolved, string? Refusal, Connection? Connection)> ResolveAsync(Guid connectionId, string action, CancellationToken ct)
    {
        var c = await session.QuerySingleOrDefaultAsync(
            """
            select id, kind, pack_version, external_tenant_id, enabled, rate_limit_per_minute, settings::text, circuit_opened_at, status
            from integrations.connection where id = @connectionId
            """, new { connectionId },
            r => new Connection(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.Get<string>("external_tenant_id"), r.GetBoolean(4), r.GetInt32(5), r.GetString(6),
                r.Get<DateTimeOffset?>("circuit_opened_at"), r.GetString(8)), ct);
        if (c is null)
        {
            return (null, ConnectorErrorClasses.ConnectionMissing, null);
        }
        var pack = packs.FirstOrDefault(p => p.Kind == c.Kind);
        var definition = pack is null ? null
            : (await pack.ActionsForAsync(ToPack(c), ct)).FirstOrDefault(a => a.Name == action);
        if (pack is null || definition is null)
        {
            return (null, ConnectorErrorClasses.UnknownAction, c);
        }
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct) || !await entitlements.IsEnabledAsync(pack.CapabilityKey, ct))
        {
            return (null, ConnectorErrorClasses.NotInPackage, c);
        }
        if (!c.Enabled)
        {
            return (null, ConnectorErrorClasses.ConnectionDisabled, c);
        }
        return (new Resolved(c, pack, definition), null, c);
    }

    private async Task<Run?> LoadRunAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            """
            select id, connection_id, action, mode, idempotency_key, correlation_id, status, attempts, max_attempts, external_reference,
              error_class, error_code, input::text, coalesce(duration_ms, 0)
            from integrations.run where id = @id
            """, new { id },
            r => new Run(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Get<string>("correlation_id"), r.GetString(6),
                r.GetInt32(7), r.GetInt32(8), r.Get<string>("external_reference"), r.Get<string>("error_class"), r.Get<string>("error_code"),
                r.Get<string>("input"), r.GetInt32(13)), ct);

    private static void Validate(ConnectorCall call)
    {
        if (string.IsNullOrWhiteSpace(call.Action))
        {
            throw AppException.Validation("action", "validation.connector.action_required");
        }
        if (string.IsNullOrWhiteSpace(call.IdempotencyKey) || call.IdempotencyKey.Length > 200)
        {
            throw AppException.Validation("idempotencyKey", "validation.connector.idempotency_key_invalid");
        }
    }

    private PackConnection ToPack(Connection c) =>
        new(context.RequireTenant(), c.Id, c.Kind, c.PackVersion, c.ExternalId, JsonDocument.Parse(c.Settings).RootElement);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
