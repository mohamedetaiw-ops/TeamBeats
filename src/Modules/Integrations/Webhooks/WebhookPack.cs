using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Webhooks;

/// <summary>
/// The signing secret of a webhook connection (spec 017 FR-013, FR-015): a secret manager reference of this tenant.
/// <paramref name="OverlapHours"/> is input only: when the reference changes, the old one is still accepted (and still
/// signs alongside the new one) for that many hours, at most 24; with 0 or none it stops working at once.
/// <paramref name="PreviousSecretRef"/> and <paramref name="PreviousValidUntil"/> are kept by TeamBeats, never taken from input.
/// </summary>
public sealed record WebhookAuth(string? Type, string? SecretRef, int? OverlapHours = null, string? PreviousSecretRef = null, DateTimeOffset? PreviousValidUntil = null);

/// <summary>A product event sent to the URL, with the fields of the event the receiver gets (FR-013).</summary>
public sealed record WebhookEvent(string? Type, IReadOnlyList<string>? Fields);

/// <summary>
/// How an inbound delivery is mapped (FR-014): to a product event or a process message (spec 024 FR-009) named
/// <paramref name="Name"/>, with the declared <paramref name="Fields"/> read from the body by path. A message needs a
/// <paramref name="CorrelationPath"/>. <paramref name="WindowSeconds"/> is the timestamp window, 60 to 300 seconds.
/// </summary>
public sealed record WebhookInbound(string? Target, string? Name, string? CorrelationPath, IReadOnlyDictionary<string, string>? Fields, int? WindowSeconds);

/// <summary>
/// Settings of a webhook connection: <paramref name="Url"/> receives the selected <paramref name="Events"/>, and
/// <paramref name="Inbound"/> turns on the connection's inbound URL. A connection has at least one of the two.
/// </summary>
public sealed record WebhookSettings(string? Url, WebhookAuth? Auth, IReadOnlyList<WebhookEvent>? Events, WebhookInbound? Inbound);

/// <summary>A secret change seen on save: how long the previous secret stays valid, if at all.</summary>
public sealed record WebhookRotation(int OverlapHours, DateTimeOffset? PreviousValidUntil);

/// <summary>
/// Signatures of webhook deliveries (FR-013): <c>v1=</c> and the lowercase hex HMAC SHA-256 of
/// <c>{timestamp}.{body}</c> with the signing secret. During a rotation overlap a delivery carries one signature per
/// valid secret, separated by commas, so the receiver can move to the new secret at its own pace.
/// </summary>
public static class WebhookSignature
{
    public const string SignatureHeader = "X-TeamBeats-Signature";
    public const string TimestampHeader = "X-TeamBeats-Timestamp";
    public const string DeliveryHeader = "X-TeamBeats-Delivery";
    public const string EventHeader = "X-TeamBeats-Event";

    public static string Compute(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + body));
        return "v1=" + Convert.ToHexStringLower(mac);
    }

    /// <summary>True when any <c>v1=</c> signature in the header matches any of the secrets. Compared in constant time.</summary>
    public static bool Matches(string? header, IEnumerable<string> secrets, long timestamp, string body)
    {
        if (string.IsNullOrEmpty(header) || header.Length > 1000)
        {
            return false;
        }
        var given = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.StartsWith("v1=", StringComparison.Ordinal))
            .Select(s => Encoding.ASCII.GetBytes(s))
            .ToList();
        var matched = false;
        foreach (var secret in secrets)
        {
            var expected = Encoding.ASCII.GetBytes(Compute(secret, timestamp, body));
            foreach (var g in given)
            {
                matched |= g.Length == expected.Length && CryptographicOperations.FixedTimeEquals(g, expected);
            }
        }
        return matched;
    }
}

/// <summary>
/// The webhook pack (spec 017 US5, FR-013 to FR-015). A webhook connection is one external endpoint: outbound, the
/// runtime delivers the selected product events to its URL with the action <c>deliver</c>, so retries, the delivery
/// log (run history), dead letters, replay, rate limits, the circuit breaker and turning it off all work as for any
/// connector. Inbound, the connection's own URL on the tenant host accepts signed deliveries (see
/// <see cref="InboundWebhooks"/>). The receiver deduplicates by the delivery ID, which is the idempotency key, so a
/// delivery may be retried after a timeout.
/// </summary>
public sealed partial class WebhookPack(
    ITenantContext context,
    HostAllowlist allowlist,
    ISecretStore secrets,
    IHttpClientFactory httpClients,
    IClock clock) : IConnectorPack
{
    public const string KindName = "webhook";
    public const string DeliverAction = WebhookActions.Deliver;
    public const string SignatureType = "hmac_sha256";
    public const int MaxOverlapHours = 24;
    public const int DefaultWindowSeconds = 300;

    private static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationWebhooks;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } = [new(DeliverAction, ConnectorSideEffects.Write, true, [])];

    [GeneratedRegex(@"^[a-z][a-z0-9_.]{0,63}$")]
    private static partial Regex TargetName();

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9_]{0,40}$")]
    private static partial Regex FieldName();

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]*|\[\d{1,4}\])*$")]
    private static partial Regex JsonPath();

    public static WebhookSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object ? settings.Deserialize<WebhookSettings>(JsonDefaults.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The secrets that sign and verify now: the current one, and the previous one while its overlap lasts.</summary>
    public async Task<IReadOnlyList<string>> SecretsAsync(Guid tenantId, WebhookAuth auth, CancellationToken ct)
    {
        var list = new List<string>();
        if (auth.SecretRef is not null && await secrets.GetTenantSecretAsync(tenantId, auth.SecretRef, ct) is { Length: > 0 } current)
        {
            list.Add(current);
        }
        if (auth.PreviousSecretRef is not null && auth.PreviousValidUntil is { } until && until > clock.UtcNow
            && await secrets.GetTenantSecretAsync(tenantId, auth.PreviousSecretRef, ct) is { Length: > 0 } previous)
        {
            list.Add(previous);
        }
        return list;
    }

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (call.Action != DeliverAction)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        var settings = Parse(call.Connection.Settings);
        if (settings?.Url is null || settings.Auth is null)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        var uri = new Uri(settings.Url);
        if (!await allowlist.IsAllowedAsync(uri, ct))
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed);
        }
        var keys = await SecretsAsync(call.Connection.TenantId, settings.Auth, ct);
        if (keys.Count == 0)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.CredentialMissing);
        }
        if (call.Input.ValueKind != JsonValueKind.Object || !call.Input.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String
            || !call.Input.TryGetProperty("occurredAt", out var at) || !call.Input.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
        }
        var type = t.GetString()!;
        var body = JsonSerializer.Serialize(new { id = call.IdempotencyKey, type, occurredAt = at, data = d }, JsonDefaults.Options);
        var timestamp = clock.UtcNow.ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(WebhookSignature.DeliveryHeader, call.IdempotencyKey);
        request.Headers.TryAddWithoutValidation(WebhookSignature.EventHeader, type);
        request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader, string.Join(",", keys.Select(k => WebhookSignature.Compute(k, timestamp, body))));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
        {
            return PackCallResult.Ok();
        }
        if (status is 401 or 403)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused);
        }
        var retryAfter = response.Headers.RetryAfter?.Delta is { } delay ? (delay > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : delay) : (TimeSpan?)null;
        if (status is 429)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.Throttled, retryAfter: retryAfter);
        }
        if (status is 408 or >= 500)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.ServerError, mayHaveActed: true, retryAfter: retryAfter);
        }
        return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
    }

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.webhook.settings");
        var errors = new ValidationErrors();
        string? url = null;
        if (s.Url is not null)
        {
            if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment) || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out _) || s.Url.Length > 2000)
            {
                errors.Add("url", "validation.webhook.url");
            }
            else if (!await allowlist.IsAllowedAsync(uri, ct))
            {
                errors.Add("url", "validation.custom_rest.host_not_allowed");
            }
            else
            {
                url = uri.AbsoluteUri;
            }
        }
        if (s.Auth?.Type != SignatureType || SecretReferences.NameFor(tenantId, s.Auth.SecretRef) is null)
        {
            errors.Add("auth.secretRef", "validation.custom_rest.secret_ref");
        }
        if (s.Auth?.OverlapHours is { } overlap && overlap is < 0 or > MaxOverlapHours)
        {
            errors.Add("auth.overlapHours", "validation.webhook.overlap");
        }

        var events = new List<WebhookEvent>();
        var given = s.Events ?? [];
        if (given.Count > 0 && s.Url is null)
        {
            errors.Add("events", "validation.webhook.events_need_url");
        }
        if (given.Count > 20 || given.Select(e => e.Type).Distinct(StringComparer.Ordinal).Count() != given.Count)
        {
            errors.Add("events", "validation.webhook.events");
        }
        foreach (var e in given)
        {
            var allowed = e.Type is null ? null : OutboundEvents.FieldsOf(e.Type);
            var fields = (e.Fields ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (allowed is null)
            {
                errors.Add("events", "validation.webhook.event_unknown");
            }
            else if (fields.Count == 0 || fields.Any(f => !allowed.Contains(f)))
            {
                errors.Add("events", "validation.webhook.event_fields");
            }
            else
            {
                events.Add(new WebhookEvent(e.Type, fields));
            }
        }

        WebhookInbound? inbound = null;
        if (s.Inbound is { } i)
        {
            var fields = i.Fields ?? new Dictionary<string, string>();
            if (i.Target is not ("event" or "message" or "kpi"))
            {
                errors.Add("inbound.target", "validation.webhook.target");
            }
            if (i.Target == "kpi" && (KpiFeed.RequiredFields.Any(f => !fields.ContainsKey(f))
                || fields.Keys.Any(k => !KpiFeed.RequiredFields.Contains(k) && k != KpiFeed.LocationField)))
            {
                errors.Add("inbound.fields", "validation.webhook.kpi_fields");
            }
            if (i.Name is null || !TargetName().IsMatch(i.Name))
            {
                errors.Add("inbound.name", "validation.webhook.target_name");
            }
            if (i.Target == "message" ? i.CorrelationPath is null || !JsonPath().IsMatch(i.CorrelationPath) : i.CorrelationPath is not null && !JsonPath().IsMatch(i.CorrelationPath))
            {
                errors.Add("inbound.correlationPath", "validation.webhook.correlation_path");
            }
            if (fields.Count > 20 || fields.Any(f => !FieldName().IsMatch(f.Key) || f.Value is null || !JsonPath().IsMatch(f.Value)))
            {
                errors.Add("inbound.fields", "validation.webhook.inbound_fields");
            }
            if (i.WindowSeconds is { } w && w is < 60 or > 300)
            {
                errors.Add("inbound.windowSeconds", "validation.webhook.window");
            }
            inbound = new WebhookInbound(i.Target, i.Name, i.CorrelationPath, fields, i.WindowSeconds ?? DefaultWindowSeconds);
        }
        if (s.Url is null && s.Inbound is null)
        {
            errors.Add("url", "validation.webhook.direction");
        }
        errors.ThrowIfAny();
        var stored = new WebhookSettings(url, new WebhookAuth(SignatureType, s.Auth!.SecretRef, s.Auth.OverlapHours), events, inbound);
        return JsonSerializer.SerializeToElement(stored, Stored);
    }

    /// <summary>
    /// Keeps the rotation state (FR-015): a new secret reference makes the old one the previous secret for the overlap the
    /// administrator set, or drops it at once; an unchanged reference keeps a running overlap. The overlap input is not stored.
    /// </summary>
    public JsonElement CarryOver(JsonElement previous, JsonElement next, DateTimeOffset now)
    {
        var n = Parse(next);
        if (n?.Auth is null)
        {
            return next;
        }
        var p = Parse(previous)?.Auth;
        WebhookAuth auth;
        if (p?.SecretRef is null || p.SecretRef == n.Auth.SecretRef)
        {
            var keep = p is not null && p.SecretRef == n.Auth.SecretRef && p.PreviousValidUntil > now;
            auth = new WebhookAuth(SignatureType, n.Auth.SecretRef, null, keep ? p!.PreviousSecretRef : null, keep ? p!.PreviousValidUntil : null);
        }
        else
        {
            var hours = n.Auth.OverlapHours ?? 0;
            auth = hours > 0
                ? new WebhookAuth(SignatureType, n.Auth.SecretRef, null, p.SecretRef, now.AddHours(hours))
                : new WebhookAuth(SignatureType, n.Auth.SecretRef);
        }
        return JsonSerializer.SerializeToElement(n with { Auth = auth }, Stored);
    }

    /// <summary>The rotation between two stored settings, or null when the secret reference did not change.</summary>
    public static WebhookRotation? Rotation(JsonElement previous, JsonElement next, DateTimeOffset now)
    {
        var p = Parse(previous)?.Auth;
        var n = Parse(next)?.Auth;
        if (p?.SecretRef is null || n?.SecretRef is null || p.SecretRef == n.SecretRef)
        {
            return null;
        }
        var hours = n.PreviousSecretRef == p.SecretRef && n.PreviousValidUntil is { } until ? (int)Math.Round((until - now).TotalHours) : 0;
        return new WebhookRotation(Math.Clamp(hours, 0, MaxOverlapHours), n.PreviousSecretRef == p.SecretRef ? n.PreviousValidUntil : null);
    }

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        var s = Parse(connection.Settings);
        if (s?.Auth is null)
        {
            return [new PackCheck("settings", false, "integrations.test.settings_invalid", ConnectorErrorClasses.RequestRefused)];
        }
        var checks = new List<PackCheck>();
        if (s.Url is not null)
        {
            var hostOk = await allowlist.IsAllowedAsync(new Uri(s.Url), ct);
            checks.Add(new PackCheck("url", hostOk, hostOk ? "integrations.test.host_allowed" : "integrations.test.host_not_allowed",
                hostOk ? null : ConnectorErrorClasses.HostNotAllowed));
        }
        var found = s.Auth.SecretRef is not null && await secrets.GetTenantSecretAsync(connection.TenantId, s.Auth.SecretRef, ct) is { Length: > 0 };
        checks.Add(new PackCheck("credential", found, found ? "integrations.test.credential_found" : "integrations.test.credential_missing",
            found ? null : ConnectorErrorClasses.CredentialMissing));
        return checks;
    }
}
