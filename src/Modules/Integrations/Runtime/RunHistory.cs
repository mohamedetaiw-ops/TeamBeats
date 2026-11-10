using System.Globalization;
using TeamBeats.Audit.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>A run as history shows it (spec 017 US3 scenario 1): no payload content, ever.</summary>
public sealed record RunView(
    Guid Id,
    string Action,
    string Mode,
    string Status,
    int Attempts,
    string? ErrorClass,
    string? ErrorCode,
    string? PreviousErrorClass,
    string? ExternalReference,
    int? RecordCount,
    int? DurationMs,
    string? CorrelationId,
    int Replays,
    DateTimeOffset? LastReplayedAt,
    bool Replayable,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

public sealed record RunPage(IReadOnlyList<RunView> Items, string? NextCursor);

public sealed record ReplayRequest(IReadOnlyList<Guid>? RunIds);

public sealed record ReplayResult(IReadOnlyList<Guid> Replayed, IReadOnlyList<Guid> Skipped);

/// <summary>
/// Run history, dead letters and replay (spec 017 FR-006, FR-007, US3). A failed or unknown run is a dead letter for
/// 14 days: replay queues it again as a job with its original idempotency key, keeps the earlier error class next to
/// the new outcome, and is audited. History is kept 90 days. Access is checked by the endpoints.
/// </summary>
public sealed class RunHistory(IDbSession session, ITenantContext context, IAuditWriter audit, IClock clock)
{
    public const int MaxReplay = 100;

    /// <summary>Status filter values: a run status, or <c>dead_letter</c> for runs that can be replayed.</summary>
    public static readonly IReadOnlySet<string> StatusFilters = new HashSet<string>(StringComparer.Ordinal)
    {
        "queued", "running", "retrying", "succeeded", "business_error", "failed", "unknown", "refused", "dead_letter",
    };

    public async Task<RunPage> ListAsync(Guid connectionId, string? status, string? action, string? cursor, int limit, CancellationToken ct)
    {
        await RequireConnectionAsync(connectionId, ct);
        if (status is not null && !StatusFilters.Contains(status))
        {
            throw AppException.Validation("status", "validation.connector.run_status_invalid");
        }
        limit = Math.Clamp(limit, 1, 100);
        var (beforeAt, beforeId) = ParseCursor(cursor);
        var now = clock.UtcNow;
        var since = now - RuntimePolicy.DeadLetterRetention;
        var rows = await session.QueryAsync(
            """
            select id, action, mode, status, attempts, error_class, error_code, previous_error_class, external_reference, record_count,
              duration_ms, correlation_id, replays, last_replayed_at, created_at, finished_at,
              (status in ('failed', 'unknown') and input is not null and finished_at > @since) as replayable
            from integrations.run
            where connection_id = @connectionId
              and (@status::text is null or (@status = 'dead_letter' and status in ('failed', 'unknown') and input is not null and finished_at > @since)
                   or status = @status)
              and (@action::text is null or action = @action)
              and (@beforeAt::timestamptz is null or (created_at, id) < (@beforeAt, @beforeId))
            order by created_at desc, id desc
            limit @take
            """, new { connectionId, status, action, beforeAt, beforeId, since, take = limit + 1 },
            r => new RunView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.Get<string>("error_class"),
                r.Get<string>("error_code"), r.Get<string>("previous_error_class"), r.Get<string>("external_reference"), r.Get<int?>("record_count"),
                r.Get<int?>("duration_ms"), r.Get<string>("correlation_id"), r.GetInt32(12), r.Get<DateTimeOffset?>("last_replayed_at"),
                r.GetBoolean(16), r.GetFieldValue<DateTimeOffset>(14), r.Get<DateTimeOffset?>("finished_at")), ct);
        var more = rows.Count > limit;
        var items = more ? rows.Take(limit).ToList() : rows;
        var next = more ? $"{items[^1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{items[^1].Id:N}" : null;
        return new RunPage(items, next);
    }

    public async Task<ReplayResult> ReplayAsync(Guid connectionId, ReplayRequest request, CancellationToken ct)
    {
        await RequireConnectionAsync(connectionId, ct);
        var ids = (request.RunIds ?? []).Distinct().ToArray();
        if (ids.Length is 0 or > MaxReplay)
        {
            throw AppException.Validation("runIds", "validation.connector.replay_selection");
        }
        var now = clock.UtcNow;
        var replayed = await session.QueryAsync(
            """
            update integrations.run set previous_error_class = error_class, status = 'queued', mode = 'job', attempts = 0, max_attempts = @max,
              next_attempt_at = @now, error_class = null, error_code = null, finished_at = null, replays = replays + 1,
              last_replayed_at = @now, last_replayed_by = @actor
            where connection_id = @connectionId and id = any(@ids) and status in ('failed', 'unknown') and input is not null and finished_at > @since
            returning id
            """, new { connectionId, ids, now, since = now - RuntimePolicy.DeadLetterRetention, actor = context.ActorId, max = RuntimePolicy.JobAttempts },
            r => r.GetGuid(0), ct);
        if (replayed.Count > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ConnectorReplay, "connection", connectionId.ToString(),
                Changes: new { runs = replayed }), ct);
        }
        return new ReplayResult(replayed, ids.Except(replayed).ToList());
    }

    /// <summary>Daily: history after 90 days, inputs of dead letters after 14 days, circuit attempts after a day.</summary>
    public async Task PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync("delete from integrations.run where created_at < @before", new { before = now - RuntimePolicy.HistoryRetention }, ct);
        await session.ExecAsync("update integrations.run set input = null where input is not null and finished_at < @before",
            new { before = now - RuntimePolicy.DeadLetterRetention }, ct);
        await session.ExecAsync("delete from integrations.call_attempt where at < @before", new { before = now - RuntimePolicy.AttemptRetention }, ct);
    }

    private async Task RequireConnectionAsync(Guid id, CancellationToken ct)
    {
        if (!await session.ScalarAsync<bool>("select exists (select 1 from integrations.connection where id = @id)", new { id }, ct))
        {
            throw AppException.NotFound();
        }
    }

    private static (DateTimeOffset? At, Guid? Id) ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return (null, null);
        }
        var parts = cursor.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            throw AppException.Validation("cursor", "validation.cursor.invalid");
        }
        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }
}
