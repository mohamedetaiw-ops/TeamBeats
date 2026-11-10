using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Webhooks;

/// <summary>Accepted and refused inbound deliveries of one connection (spec 017 US5 scenario 2), and its inbound URL path.</summary>
public sealed record InboundCounts(string Path, long Accepted, long RefusedSignature, long RefusedWindow, long RefusedDuplicate, DateTimeOffset? LastReceivedAt);

/// <summary>Why an inbound delivery was refused, counted per connection.</summary>
public enum InboundRefusal
{
    Signature,
    Window,
    Duplicate,
}

/// <summary>
/// Inbound webhooks (spec 017 US5, FR-014). The URL is on the tenant's own host and names the connection, so it
/// resolves to exactly one tenant and one connection; the tenant comes from the host, never from the body. A delivery
/// must carry a valid signature over its timestamp and body with the current secret (or the previous one during its
/// overlap), a timestamp inside the connection's window, and a delivery ID not seen before. Only then is it mapped to
/// the configured product event or process message, carrying only the declared fields. Refusals are counted.
/// </summary>
public sealed class InboundWebhooks(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    WebhookPack pack,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxBodyBytes = 1024 * 1024;
    public static readonly TimeSpan ReceiptLifetime = TimeSpan.FromDays(1);

    public static string PathFor(Guid connectionId) => $"/api/v1/integrations/inbound/{connectionId:D}";

    /// <summary>The inbound settings of an enabled webhook connection in the package, or null when the URL is not in use.</summary>
    private async Task<WebhookSettings?> InboundAsync(Guid connectionId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select settings::text from integrations.connection where id = @connectionId and kind = @kind and enabled",
            new { connectionId, kind = WebhookPack.KindName }, r => r.GetString(0), ct);
        if (row is null || !await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct) || !await entitlements.IsEnabledAsync(Capabilities.IntegrationWebhooks, ct))
        {
            return null;
        }
        var settings = WebhookPack.Parse(JsonDocument.Parse(row).RootElement);
        return settings?.Inbound is null || settings.Auth is null ? null : settings;
    }

    /// <summary>Checks one delivery. Returns the refusal, or null when it was accepted and published.</summary>
    public async Task<InboundRefusal?> ReceiveAsync(Guid connectionId, string? timestampHeader, string? signature, string? deliveryId, string body,
        CancellationToken ct)
    {
        var settings = await InboundAsync(connectionId, ct) ?? throw AppException.NotFound();
        var inbound = settings.Inbound!;
        var tenantId = context.RequireTenant();
        var now = clock.UtcNow;
        if (string.IsNullOrEmpty(deliveryId) || deliveryId.Length > 200 || deliveryId.Any(char.IsControl))
        {
            throw AppException.Validation("delivery", "validation.webhook.delivery_id");
        }
        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || !WebhookSignature.Matches(signature, await pack.SecretsAsync(tenantId, settings.Auth!, ct), timestamp, body))
        {
            return await RefuseAsync(connectionId, InboundRefusal.Signature, now, ct);
        }
        var window = inbound.WindowSeconds ?? WebhookPack.DefaultWindowSeconds;
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > window)
        {
            return await RefuseAsync(connectionId, InboundRefusal.Window, now, ct);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw AppException.Validation("body", "validation.webhook.body");
        }
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var f in inbound.Fields ?? new Dictionary<string, string>())
        {
            fields[f.Key] = Text(RestDefinitions.Select(root, f.Value));
        }
        var correlation = inbound.CorrelationPath is null ? null : Text(RestDefinitions.Select(root, inbound.CorrelationPath));
        if (inbound.Target == "message" && string.IsNullOrEmpty(correlation))
        {
            throw AppException.Validation("body", "validation.webhook.correlation_missing");
        }

        var fresh = await session.ExecAsync(
            """
            insert into integrations.webhook_receipt (tenant_id, connection_id, delivery_id, received_at)
            values (platform.current_tenant_id(), @connectionId, @deliveryId, @now)
            on conflict do nothing
            """, new { connectionId, deliveryId, now }, ct);
        if (fresh == 0)
        {
            return await RefuseAsync(connectionId, InboundRefusal.Duplicate, now, ct);
        }
        await CountAsync(connectionId, "accepted", now, ct);
        await outbox.EnqueueAsync(new InboundWebhookReceived(connectionId, deliveryId, inbound.Target!, inbound.Name!, correlation, fields), ct);
        return null;
    }

    public async Task<InboundCounts?> CountsAsync(Guid connectionId, CancellationToken ct)
    {
        var exists = await session.ScalarAsync<bool>("select exists (select 1 from integrations.connection where id = @connectionId and kind = @kind)",
            new { connectionId, kind = WebhookPack.KindName }, ct);
        if (!exists)
        {
            return null;
        }
        return await session.QuerySingleOrDefaultAsync(
            """
            select accepted, refused_signature, refused_window, refused_duplicate, last_received_at
            from integrations.webhook_inbound_count where connection_id = @connectionId
            """, new { connectionId },
            r => new InboundCounts(PathFor(connectionId), r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.Get<DateTimeOffset?>("last_received_at")), ct)
            ?? new InboundCounts(PathFor(connectionId), 0, 0, 0, 0, null);
    }

    public Task PurgeAsync(DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync("delete from integrations.webhook_receipt where received_at < @before", new { before = now - ReceiptLifetime }, ct);

    private async Task<InboundRefusal> RefuseAsync(Guid connectionId, InboundRefusal refusal, DateTimeOffset now, CancellationToken ct)
    {
        await CountAsync(connectionId, refusal switch
        {
            InboundRefusal.Signature => "refused_signature",
            InboundRefusal.Window => "refused_window",
            _ => "refused_duplicate",
        }, now, ct);
        return refusal;
    }

    // The column comes from the fixed list above, never from input.
    private Task CountAsync(Guid connectionId, string column, DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync(
            $"""
            insert into integrations.webhook_inbound_count (tenant_id, connection_id, {column}, last_received_at)
            values (platform.current_tenant_id(), @connectionId, 1, @now)
            on conflict (tenant_id, connection_id) do update set {column} = webhook_inbound_count.{column} + 1, last_received_at = @now
            """, new { connectionId, now }, ct);

    private static string? Text(JsonElement? value) => value switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.String } s => Limit(s.GetString()),
        { } other => Limit(other.GetRawText()),
    };

    private static string? Limit(string? s) => s is { Length: > 2000 } ? s[..2000] : s;

    /// <summary>
    /// The inbound URL. No session: the caller is another system. The tenant comes from the host name, and the request
    /// must be on a tenant host of an active tenant; anything else is not found.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/integrations/inbound/{connectionId:guid}", async (Guid connectionId, HttpRequest request, ITenantContext context,
            TenantContextAccessor accessor, InboundWebhooks inbound, IDbSession session, CancellationToken ct) =>
        {
            if (context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { Status: TenantLifecycleStatus.Active } tenant)
            {
                throw AppException.NotFound();
            }
            accessor.SetTenant(tenant.Id);
            accessor.SetActor(ActorType.Anonymous, null);
            var body = await ReadAsync(request, ct);
            if (body is null)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var refusal = await inbound.ReceiveAsync(connectionId, request.Headers[WebhookSignature.TimestampHeader], request.Headers[WebhookSignature.SignatureHeader],
                request.Headers[WebhookSignature.DeliveryHeader], body, ct);
            await session.CommitAsync(ct);
            return refusal switch
            {
                null => Results.Accepted(),
                InboundRefusal.Duplicate => throw AppException.Conflict("conflict.integrations.webhook_duplicate"),
                InboundRefusal.Window => throw new AppException(ProblemCodes.Unauthenticated, 401, "integrations.webhook.timestamp_outside_window"),
                _ => throw new AppException(ProblemCodes.Unauthenticated, 401, "integrations.webhook.signature_invalid"),
            };
        }).WithTags("Integrations: webhooks").DisableAntiforgery();
    }

    private static async Task<string?> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > MaxBodyBytes)
        {
            return null;
        }
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total <= MaxBodyBytes && (read = await request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
        }
        if (total > MaxBodyBytes)
        {
            return null;
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(buffer, 0, total);
        }
        catch (DecoderFallbackException)
        {
            return string.Empty;
        }
    }
}
