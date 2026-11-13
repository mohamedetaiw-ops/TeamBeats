using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Webhooks;

/// <summary>
/// The pack of endpoints that another module owns (spec 017 US5 for spec 035 FR-018). It sends exactly as the webhook
/// pack does, signed, to an allowlisted HTTPS URL, but its connections are made only through <see cref="IWebhookDelivery"/>:
/// the Connections page neither offers nor lists them, and the package needs the public API's event capability, not the
/// connector framework, since the tenant configures the subscription and not a connection.
/// </summary>
public sealed class ApiEventPack(WebhookPack webhooks) : IConnectorPack
{
    public const string KindName = "api_events";

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.ApiEventSubscriptions;

    public bool Listed => false;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } = [new(WebhookPack.DeliverAction, ConnectorSideEffects.Write, true, [])];

    public Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct) => webhooks.CallAsync(call, ct);

    public Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct) => webhooks.ValidateSettingsAsync(settings, ct);

    public Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct) => webhooks.TestAsync(connection, ct);
}

/// <summary>The connection kinds the Connections page and connector catalogues leave out.</summary>
internal static class HiddenKinds
{
    /// <summary>
    /// Microsoft 365 has its own page (spec 006); API event endpoints belong to the public API (spec 035); SIEM
    /// destinations belong to the security pages (spec 026).
    /// </summary>
    public static readonly string[] All = [ConnectionKinds.Microsoft365, ApiEventPack.KindName, Siem.SiemPack.KindName];
}

/// <summary>
/// <see cref="IWebhookDelivery"/> over the connector runtime: an endpoint is an <c>api_events</c> connection, a delivery
/// is a durable <c>deliver</c> job of it, so signing, retries, the allowlist, the circuit breaker and the delivery log
/// (run history) are those of every webhook. Validation errors name the request's fields.
/// </summary>
public sealed class WebhookDelivery(IDbSession session, IEntitlementService entitlements, ApiEventPack pack, IConnectorRuntime runtime, IClock clock)
    : IWebhookDelivery
{
    public const int MaxDeliveries = 100;

    public async Task<Guid> CreateEndpointAsync(WebhookEndpointRequest request, CancellationToken ct = default)
    {
        await entitlements.DemandAsync(pack.CapabilityKey, ct);
        var name = request.Name.Trim();
        name = name.Length > 120 ? name[..120] : name;
        JsonElement settings;
        try
        {
            settings = await pack.ValidateSettingsAsync(JsonSerializer.SerializeToElement(new
            {
                url = request.Url,
                auth = new { type = WebhookPack.SignatureType, secretRef = request.SecretRef },
            }, JsonDefaults.Options), ct);
        }
        catch (AppException e) when (e.Errors is { } errors)
        {
            // Name the fields of the request, not the stored settings.
            var renamed = new ValidationErrors();
            foreach (var (field, keys) in errors)
            {
                foreach (var key in keys)
                {
                    renamed.Add(field == "auth.secretRef" ? "secretRef" : field, key);
                }
            }
            renamed.ThrowIfAny();
            throw;
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into integrations.connection (tenant_id, id, kind, pack_version, display_name, status, enabled, settings, rate_limit_per_minute)
            values (platform.current_tenant_id(), @id, @kind, @version, @name, 'healthy', true, @settings, 600)
            """, new { id, kind = ApiEventPack.KindName, version = pack.Version, name, settings = new Jsonb(settings.GetRawText()) }, ct);
        return id;
    }

    public async Task SetEndpointEnabledAsync(Guid endpointId, bool enabled, CancellationToken ct = default) =>
        await session.ExecAsync(
            """
            update integrations.connection set enabled = @enabled, status = case when @enabled then 'healthy' else 'disabled' end, updated_at = @now
            where id = @endpointId and kind = @kind
            """, new { endpointId, enabled, kind = ApiEventPack.KindName, now = clock.UtcNow }, ct);

    public async Task DeleteEndpointAsync(Guid endpointId, CancellationToken ct = default) =>
        await session.ExecAsync("delete from integrations.connection where id = @endpointId and kind = @kind", new { endpointId, kind = ApiEventPack.KindName }, ct);

    public async Task<ConnectorCallResult> DeliverAsync(Guid endpointId, string eventType, DateTimeOffset occurredAt, JsonElement data, CancellationToken ct = default)
    {
        var input = JsonSerializer.SerializeToElement(new { type = eventType, occurredAt, data }, JsonDefaults.Options);
        return await runtime.EnqueueAsync(new ConnectorCall(endpointId, WebhookPack.DeliverAction, input, "evt_" + Ids.New().ToString("N"), eventType), ct);
    }

    public async Task<IReadOnlyList<WebhookDeliveryRecord>> DeliveriesAsync(Guid endpointId, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select r.id, r.correlation_id, r.status, r.attempts, r.error_class, r.created_at, r.finished_at
            from integrations.run r join integrations.connection c on c.id = r.connection_id
            where r.connection_id = @endpointId and c.kind = @kind
            order by r.created_at desc, r.id desc limit @limit
            """, new { endpointId, kind = ApiEventPack.KindName, limit = Math.Clamp(limit, 1, MaxDeliveries) },
            r => new WebhookDeliveryRecord(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6)), ct);
}
