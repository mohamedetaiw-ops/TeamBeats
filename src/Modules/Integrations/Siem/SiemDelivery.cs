using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Siem;

/// <summary><see cref="ISiemDelivery"/> over the connector runtime: a destination is a hidden <c>siem</c> connection.</summary>
public sealed class SiemDelivery(IDbSession session, IEntitlementService entitlements, SiemPack pack, IConnectorRuntime runtime, IClock clock) : ISiemDelivery
{
    private async Task<JsonElement> SettingsAsync(SiemDestinationRequest request, CancellationToken ct) =>
        await pack.ValidateSettingsAsync(JsonSerializer.SerializeToElement(new
        {
            transport = request.Transport,
            url = request.Url,
            auth = request.Transport == SiemTransports.Https ? new { type = Webhooks.WebhookPack.SignatureType, secretRef = request.SecretRef } : null,
            host = request.Host,
            port = request.Port,
        }, JsonDefaults.Options), ct);

    private static string Name(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }

    public async Task<Guid> CreateAsync(SiemDestinationRequest request, CancellationToken ct = default)
    {
        await entitlements.DemandAsync(pack.CapabilityKey, ct);
        var settings = await SettingsAsync(request, ct);
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into integrations.connection (tenant_id, id, kind, pack_version, display_name, status, enabled, settings, rate_limit_per_minute)
            values (platform.current_tenant_id(), @id, @kind, @version, @name, 'healthy', true, @settings, 600)
            """, new { id, kind = SiemPack.KindName, version = pack.Version, name = Name(request.Name), settings = new Jsonb(settings.GetRawText()) }, ct);
        return id;
    }

    public async Task UpdateAsync(Guid id, SiemDestinationRequest request, CancellationToken ct = default)
    {
        await entitlements.DemandAsync(pack.CapabilityKey, ct);
        var settings = await SettingsAsync(request, ct);
        await session.ExecAsync(
            "update integrations.connection set display_name = @name, settings = @settings, updated_at = @now where id = @id and kind = @kind",
            new { id, name = Name(request.Name), settings = new Jsonb(settings.GetRawText()), now = clock.UtcNow, kind = SiemPack.KindName }, ct);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default) =>
        await session.ExecAsync(
            """
            update integrations.connection set enabled = @enabled, status = case when @enabled then 'healthy' else 'disabled' end, updated_at = @now
            where id = @id and kind = @kind
            """, new { id, enabled, kind = SiemPack.KindName, now = clock.UtcNow }, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        await session.ExecAsync("delete from integrations.connection where id = @id and kind = @kind", new { id, kind = SiemPack.KindName }, ct);

    public async Task<ConnectorCallResult> SendAsync(Guid id, IReadOnlyList<JsonElement> events, string batchKey, CancellationToken ct = default) =>
        await runtime.InvokeAsync(new ConnectorCall(id, SiemPack.SendAction, JsonSerializer.SerializeToElement(new { events }, JsonDefaults.Options), batchKey,
            "security.siem"), ct);
}
