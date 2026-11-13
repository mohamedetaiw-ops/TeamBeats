using System.Security.Cryptography;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi;

/// <summary>What a public call with an <c>Idempotency-Key</c> does: run, replay the first answer, or be refused.</summary>
public abstract record IdempotencyStart
{
    public sealed record Run : IdempotencyStart;

    public sealed record Replay(int Status, string? ContentType, string Body) : IdempotencyStart;

    /// <summary>The key was used for another request (<c>reused</c>) or its first call still runs (<c>in_progress</c>).</summary>
    public sealed record Refused(string Reason) : IdempotencyStart;
}

/// <summary>
/// The idempotency store of the public API (spec 035 FR-015, plan "shared IdempotencyStore in PublicApi"), keyed by
/// tenant (row security), app and key, with a hash of the method, route and body. Each step commits on its own, so a
/// second call with the same key sees the first one at once.
/// </summary>
public sealed class PublicIdempotency(IDbSession session, IClock clock)
{
    public const string Header = "Idempotency-Key";
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(5);
    public const int MaxBody = 64 * 1024;

    public static bool ValidKey(string key) => key.Length is >= 1 and <= 255 && key.All(c => c is >= '!' and <= '~');

    public static string Hash(string method, string path, ReadOnlySpan<byte> body)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes(method + " " + path + "\n"));
        sha.AppendData(body);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public async Task<IdempotencyStart> BeginAsync(Guid appId, string key, string hash, CancellationToken ct)
    {
        var now = clock.UtcNow;
        // A key past its window, or a hold whose call never finished, no longer counts.
        await session.ExecAsync(
            "delete from publicapi.idempotency where app_id = @appId and key = @key and (created_at < @expired or (status is null and created_at < @abandoned))",
            new { appId, key, expired = now - Window, abandoned = now - HoldFor }, ct);
        var held = await session.ExecAsync(
            """
            insert into publicapi.idempotency (tenant_id, app_id, key, request_hash, created_at)
            values (platform.current_tenant_id(), @appId, @key, @hash, @now) on conflict do nothing
            """, new { appId, key, hash, now }, ct);
        if (held == 1)
        {
            return new IdempotencyStart.Run();
        }
        var row = await session.QuerySingleOrDefaultAsync(
            "select request_hash, status, content_type, body from publicapi.idempotency where app_id = @appId and key = @key", new { appId, key },
            r => (Hash: r.GetString(0), Status: r.IsDBNull(1) ? (int?)null : r.GetInt32(1), Type: r.IsDBNull(2) ? null : r.GetString(2),
                Body: r.IsDBNull(3) ? "" : r.GetString(3)), ct);
        if (row == default)
        {
            return new IdempotencyStart.Refused("in_progress");
        }
        if (row.Hash != hash)
        {
            return new IdempotencyStart.Refused("reused");
        }
        return row.Status is { } status ? new IdempotencyStart.Replay(status, row.Type, row.Body) : new IdempotencyStart.Refused("in_progress");
    }

    /// <summary>Keeps the answer for replay, or lets the key go when the answer is not one to keep.</summary>
    public Task CompleteAsync(Guid appId, string key, int status, string? contentType, string? body, CancellationToken ct) =>
        status >= 500 || body is null || body.Length > MaxBody
            ? AbandonAsync(appId, key, ct)
            : session.ExecAsync("update publicapi.idempotency set status = @status, content_type = @contentType, body = @body where app_id = @appId and key = @key",
                new { appId, key, status, contentType, body }, ct);

    public Task AbandonAsync(Guid appId, string key, CancellationToken ct) =>
        session.ExecAsync("delete from publicapi.idempotency where app_id = @appId and key = @key and status is null", new { appId, key }, ct);

    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync("delete from publicapi.idempotency where created_at < @before", new { before = now - Window }, ct);
}
