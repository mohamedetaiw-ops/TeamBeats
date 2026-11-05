using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeamBeats.Persistence;

public sealed record StoredResponse(int StatusCode, JsonElement Body);

/// <summary>Stores the first response for an idempotency key inside the same transaction as the operation.</summary>
public sealed class IdempotencyStore(IDbSession session)
{
    public const string HeaderName = "Idempotency-Key";

    public static string Hash(object request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonDefaults.Options))));

    /// <summary>Returns the stored response, or null when the key is new. A reused key with a different request is a conflict.</summary>
    public async Task<StoredResponse?> FindAsync(string scope, string key, string requestHash, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select request_hash, status_code, response::text from platform.idempotency_key where scope = @scope and key = @key",
            new { scope, key }, r => (Hash: r.GetString(0), Status: r.GetInt32(1), Body: r.GetString(2)), ct);
        if (row == default)
        {
            return null;
        }
        if (row.Hash != requestHash)
        {
            throw Primitives.AppException.Conflict("conflict.idempotency_key_reused");
        }
        return new StoredResponse(row.Status, JsonDocument.Parse(row.Body).RootElement.Clone());
    }

    public Task SaveAsync(string scope, string key, string requestHash, int statusCode, object response, CancellationToken ct) =>
        session.ExecAsync(
            "insert into platform.idempotency_key (scope, key, request_hash, status_code, response) values (@scope, @key, @hash, @status, @response)",
            new { scope, key, hash = requestHash, status = statusCode, response = Jsonb.From(response) }, ct);
}
