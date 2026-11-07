using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

public sealed record RelaySettings(string Host, int Port, string? Username, string? SecretReference, bool StartTls);

/// <summary>The tenant's notification settings as delivery uses them.</summary>
public sealed record EffectiveNotificationSettings(
    string? SenderName,
    RelaySettings? Relay,
    IReadOnlyDictionary<string, bool> DefaultEmail,
    IReadOnlySet<string> ExtraMandatory)
{
    public bool IsMandatory(string category) =>
        NotificationCategories.Find(category)?.Mandatory == true || ExtraMandatory.Contains(category);

    public bool EmailByDefault(NotificationCategory category) =>
        DefaultEmail.TryGetValue(category.Key, out var on) ? on : category.EmailByDefault;
}

public sealed record NotificationSettingsView(
    string? SenderName,
    RelaySettings? Relay,
    IReadOnlyDictionary<string, bool> DefaultEmail,
    IReadOnlyList<string> ExtraMandatory,
    IReadOnlyList<string> PlatformMandatory,
    bool TenantRelayInPlan,
    int Version);

public sealed record UpdateNotificationSettingsRequest(
    string? SenderName,
    RelaySettings? Relay,
    IReadOnlyDictionary<string, bool>? DefaultEmail,
    IReadOnlyList<string>? ExtraMandatory,
    int Version);

/// <summary>Tenant notification settings (spec 015 US4): sender name, SMTP relay, default preferences, extra mandatory categories.</summary>
public sealed class NotificationSettingsService(IDbSession session, ITenantContext context, IClock clock, IAuditWriter audit, IEntitlementService entitlements)
{
    public const int MaxSenderNameLength = 80;
    private EffectiveNotificationSettings? _cached;

    public async Task<EffectiveNotificationSettings> GetEffectiveAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached;
        }
        var row = await ReadAsync(ct);
        var relay = row?.Relay is { } r && await entitlements.IsEnabledAsync(Capabilities.NotificationsTenantSmtp, ct) ? r : null;
        _cached = new EffectiveNotificationSettings(
            row?.SenderName, relay,
            row?.DefaultEmail ?? new Dictionary<string, bool>(),
            (row?.ExtraMandatory ?? []).ToHashSet(StringComparer.Ordinal));
        return _cached;
    }

    public async Task<NotificationSettingsView> GetViewAsync(CancellationToken ct)
    {
        var row = await ReadAsync(ct);
        // Every optional category with its effective default, so screens never repeat the platform defaults.
        var defaults = NotificationCategories.All.Where(c => !c.Mandatory)
            .ToDictionary(c => c.Key, c => row?.DefaultEmail.TryGetValue(c.Key, out var on) == true ? on : c.EmailByDefault, StringComparer.Ordinal);
        return new NotificationSettingsView(
            row?.SenderName, row?.Relay, defaults, row?.ExtraMandatory ?? [],
            NotificationCategories.All.Where(c => c.Mandatory).Select(c => c.Key).ToList(),
            await entitlements.IsEnabledAsync(Capabilities.NotificationsTenantSmtp, ct),
            row?.Version ?? 0);
    }

    public async Task<NotificationSettingsView> UpdateAsync(UpdateNotificationSettingsRequest request, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var errors = new ValidationErrors();
        var sender = string.IsNullOrWhiteSpace(request.SenderName) ? null : request.SenderName.Trim();
        if (sender is { Length: > MaxSenderNameLength } || (sender is not null && sender.Any(c => char.IsControl(c) || c is '<' or '>' or '"')))
        {
            errors.Add("senderName", "validation.notification.sender_name_invalid");
        }
        if (request.Relay is { } relay)
        {
            if (string.IsNullOrWhiteSpace(relay.Host) || relay.Host.Contains("://", StringComparison.Ordinal) || Uri.CheckHostName(relay.Host.Trim()) == UriHostNameType.Unknown)
            {
                errors.Add("relay.host", "validation.notification.relay_host_invalid");
            }
            if (relay.Port is < 1 or > 65535)
            {
                errors.Add("relay.port", "validation.notification.relay_port_invalid");
            }
            if (relay.SecretReference is not null && SecretReferences.NameFor(tenantId, relay.SecretReference) is null)
            {
                errors.Add("relay.secretReference", "validation.notification.secret_reference_invalid");
            }
            if (relay.Username is not null && relay.SecretReference is null)
            {
                errors.Add("relay.secretReference", "validation.notification.secret_reference_required");
            }
        }
        foreach (var key in (request.DefaultEmail?.Keys ?? []).Concat(request.ExtraMandatory ?? []))
        {
            if (NotificationCategories.Find(key) is null)
            {
                errors.Add("categories", "validation.notification.category_unknown");
            }
        }
        errors.ThrowIfAny();
        if (request.Relay is not null)
        {
            await entitlements.DemandAsync(Capabilities.NotificationsTenantSmtp, ct);
        }

        var current = await ReadAsync(ct);
        if ((current?.Version ?? 0) != request.Version)
        {
            throw AppException.Conflict("conflict.stale_version");
        }
        var extra = (request.ExtraMandatory ?? []).Where(k => !NotificationCategories.Find(k)!.Mandatory).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var defaults = request.DefaultEmail ?? new Dictionary<string, bool>();
        var relaySettings = request.Relay is { } rs ? rs with { Host = rs.Host.Trim() } : null;
        await session.ExecAsync(
            """
            insert into notifications.tenant_settings
              (tenant_id, sender_name, relay_host, relay_port, relay_username, relay_secret_ref, relay_starttls, default_email, extra_mandatory, version, updated_at, updated_by)
            values (platform.current_tenant_id(), @sender, @host, @port, @username, @secretRef, @startTls, @defaults, @extra, 1, @now, @actor)
            on conflict (tenant_id) do update set
              sender_name = excluded.sender_name, relay_host = excluded.relay_host, relay_port = excluded.relay_port,
              relay_username = excluded.relay_username, relay_secret_ref = excluded.relay_secret_ref, relay_starttls = excluded.relay_starttls,
              default_email = excluded.default_email, extra_mandatory = excluded.extra_mandatory,
              version = notifications.tenant_settings.version + 1, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """,
            new
            {
                sender, host = relaySettings?.Host, port = relaySettings?.Port, username = relaySettings?.Username,
                secretRef = relaySettings?.SecretReference, startTls = relaySettings?.StartTls ?? true,
                defaults = Jsonb.From(defaults), extra, now = clock.UtcNow, actor = context.ActorId,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.NotificationSettingsChanged, "notification_settings", tenantId.ToString(), Changes: new
        {
            senderName = sender,
            relayHost = relaySettings?.Host,
            relayPort = relaySettings?.Port,
            relayAuthenticated = relaySettings?.SecretReference is not null,
            defaultEmail = defaults,
            extraMandatory = extra,
        }), ct);
        _cached = null;
        return await GetViewAsync(ct);
    }

    private sealed record Row(string? SenderName, RelaySettings? Relay, Dictionary<string, bool> DefaultEmail, string[] ExtraMandatory, int Version);

    private Task<Row?> ReadAsync(CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            """
            select sender_name, relay_host, relay_port, relay_username, relay_secret_ref, relay_starttls, default_email::text, extra_mandatory, version
            from notifications.tenant_settings
            """,
            null,
            r => new Row(
                r.Get<string>("sender_name"),
                r.IsDBNull(1) ? null : new RelaySettings(r.GetString(1), r.GetInt32(2), r.Get<string>("relay_username"), r.Get<string>("relay_secret_ref"), r.GetBoolean(5)),
                JsonSerializer.Deserialize<Dictionary<string, bool>>(r.GetString(6), JsonDefaults.Options) ?? [],
                r.GetFieldValue<string[]>(7),
                r.GetInt32(8)),
            ct);
}
