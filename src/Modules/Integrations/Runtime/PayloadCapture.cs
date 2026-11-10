using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>
/// Turns capture on for <paramref name="Hours"/> (1 to 24). <paramref name="PublicOrInternal"/> is the administrator's
/// statement that the connection carries Public or Internal data only (spec 017 section 8); capture is refused without it.
/// </summary>
public sealed record CaptureRequest(int? Hours, bool? PublicOrInternal);

public sealed record CaptureView(DateTimeOffset? Until);

/// <summary>The input and output a run kept while capture was on, until <paramref name="ExpiresAt"/>.</summary>
public sealed record RunCaptureView(Guid RunId, JsonElement? Input, JsonElement? Output, DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Payload capture for debugging (spec 017 FR-007, US3 scenario 1): off by default, turned on per connection for at
/// most 24 hours, for Public and Internal data only. While it is on, each finished run keeps its input and output for
/// 24 hours; outside a window nothing is kept. Microsoft 365 and directory connections cannot capture, because they
/// carry personal and confidential data. Turning it on or off is audited; reading a capture needs <c>integration.operate</c>
/// (checked by the endpoint). Each payload is kept up to 64 KB.
/// </summary>
public sealed class PayloadCapture(IDbSession session, IAuditWriter audit, IClock clock)
{
    public const int MaxHours = 24;
    public const int MaxBytes = 64 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private static readonly string[] NoCapture = [ConnectionKinds.Microsoft365, ConnectionKinds.OnPremAd];

    private async Task<string> KindAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select kind from integrations.connection where id = @id", new { id }, r => r.GetString(0), ct)
        ?? throw AppException.NotFound();

    public async Task<CaptureView> StartAsync(Guid id, CaptureRequest request, CancellationToken ct)
    {
        var kind = await KindAsync(id, ct);
        var errors = new ValidationErrors();
        if (NoCapture.Contains(kind))
        {
            errors.Add("kind", "validation.connector.capture_kind");
        }
        if (request.Hours is not { } hours || hours < 1 || hours > MaxHours)
        {
            errors.Add("hours", "validation.connector.capture_hours");
        }
        if (request.PublicOrInternal != true)
        {
            errors.Add("publicOrInternal", "validation.connector.capture_classification");
        }
        errors.ThrowIfAny();
        var until = clock.UtcNow.AddHours(request.Hours!.Value);
        await session.ExecAsync("update integrations.connection set capture_until = @until, updated_at = @now where id = @id",
            new { id, until, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionChanged, "connection", id.ToString(),
            Changes: new { payloadCapture = "on", hours = request.Hours, until }), ct);
        return new CaptureView(until);
    }

    public async Task StopAsync(Guid id, CancellationToken ct)
    {
        await KindAsync(id, ct);
        var changed = await session.ExecAsync("update integrations.connection set capture_until = null, updated_at = @now where id = @id and capture_until is not null",
            new { id, now = clock.UtcNow }, ct);
        if (changed > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionChanged, "connection", id.ToString(), Changes: new { payloadCapture = "off" }), ct);
        }
    }

    public async Task<CaptureView> GetAsync(Guid id, CancellationToken ct)
    {
        var until = await session.QuerySingleOrDefaultAsync("select capture_until from integrations.connection where id = @id", new { id },
            r => (Found: true, Until: r.Get<DateTimeOffset?>("capture_until")), ct);
        if (!until.Found)
        {
            throw AppException.NotFound();
        }
        return new CaptureView(until.Until > clock.UtcNow ? until.Until : null);
    }

    public async Task<RunCaptureView> RunAsync(Guid connectionId, Guid runId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select run_id, input::text, output::text, captured_at, expires_at from integrations.run_capture
            where connection_id = @connectionId and run_id = @runId and expires_at > @now
            """, new { connectionId, runId, now = clock.UtcNow },
            r => new RunCaptureView(r.GetGuid(0), Parse(r.Get<string>("input")), Parse(r.Get<string>("output")), r.GetFieldValue<DateTimeOffset>(3),
                r.GetFieldValue<DateTimeOffset>(4)), ct);
        return row ?? throw AppException.NotFound();
    }

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Called by the runtime as a run finishes: keeps its input and output only while the connection's window is open.</summary>
    internal static Task KeepAsync(IDbSession session, Guid runId, JsonElement? output, DateTimeOffset now, CancellationToken ct)
    {
        var text = output is { ValueKind: not JsonValueKind.Undefined } o ? o.GetRawText() : null;
        return session.ExecAsync(
            """
            insert into integrations.run_capture (tenant_id, run_id, connection_id, input, output, captured_at, expires_at)
            select r.tenant_id, r.id, r.connection_id,
              case when octet_length(r.input::text) <= @max then r.input else '{"truncated":true}'::jsonb end,
              case when @output::text is null then null when octet_length(@output::text) <= @max then @output::jsonb else '{"truncated":true}'::jsonb end,
              @now, @expires
            from integrations.run r join integrations.connection c on c.id = r.connection_id
            where r.id = @runId and c.capture_until > @now
            on conflict (tenant_id, run_id) do update set input = excluded.input, output = excluded.output, captured_at = excluded.captured_at,
              expires_at = excluded.expires_at
            """, new { runId, output = text, now, expires = now + Retention, max = MaxBytes }, ct);
    }

    /// <summary>Hourly with the run purge: captures past their 24 hours are deleted.</summary>
    public Task PurgeAsync(DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync("delete from integrations.run_capture where expires_at <= @now", new { now }, ct);
}
