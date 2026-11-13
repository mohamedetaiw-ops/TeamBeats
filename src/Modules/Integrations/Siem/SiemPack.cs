using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Integrations.Webhooks;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Siem;

/// <summary>Stored settings of a SIEM destination. Secrets stay references.</summary>
public sealed record SiemSettings(string? Transport, string? Url, WebhookAuth? Auth, string? Host, int? Port);

/// <summary>Sends syslog messages over TLS (RFC 5425). An interface so tests can stand in for the network.</summary>
public interface ISyslogSender
{
    /// <summary>Opens one TLS connection, sends every message with octet counting framing, and closes it.</summary>
    Task SendAsync(string host, int port, IReadOnlyList<string> messages, CancellationToken ct);
}

/// <summary>
/// Syslog over TLS with the same address guard as connector HTTP calls: the host is resolved once, private and other
/// non-public addresses are refused unless the installation allows them, and the socket connects to the checked address.
/// The server certificate is validated against the host name as usual.
/// </summary>
public sealed class TlsSyslogSender : ISyslogSender
{
    private readonly bool allowPrivate;
    private readonly RemoteCertificateValidationCallback? validate;

    public TlsSyslogSender(IConfiguration configuration)
        : this(configuration.GetValue<bool>(SafeHttp.AllowPrivateSetting), null)
    {
    }

    /// <summary>For tests: a listener on this machine with its own certificate.</summary>
    public TlsSyslogSender(bool allowPrivate, RemoteCertificateValidationCallback? validate)
    {
        this.allowPrivate = allowPrivate;
        this.validate = validate;
    }

    public async Task SendAsync(string host, int port, IReadOnlyList<string> messages, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }
        if (!allowPrivate && addresses.Any(a => !SafeHttp.IsPublic(a)))
        {
            throw new BlockedAddressException(host);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(addresses, port, timeout.Token);
        await using var network = new NetworkStream(socket, ownsSocket: false);
        await using var tls = new SslStream(network, leaveInnerStreamOpen: false, validate);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, timeout.Token);
        foreach (var message in messages)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await tls.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + " "), timeout.Token);
            await tls.WriteAsync(bytes, timeout.Token);
        }
        await tls.FlushAsync(timeout.Token);
        // Close cleanly (TLS close_notify, then FIN) so the receiver reads every frame before the connection ends.
        await tls.ShutdownAsync();
        socket.Shutdown(SocketShutdown.Send);
    }
}

/// <summary>
/// The SIEM pack (spec 026 US4): one action, <c>send</c>, that sends a batch of stream events in order. HTTPS posts
/// <c>{"events": [...]}</c> signed like webhooks (spec 017 FR-013); syslog sends one RFC 5424 message per event with
/// the event as JSON. Not listed on the Connections page: destinations are managed on the security pages.
/// </summary>
public sealed class SiemPack(ITenantContext context, HostAllowlist allowlist, WebhookPack webhooks, ISyslogSender syslog, IHttpClientFactory httpClients, IClock clock)
    : IConnectorPack
{
    public const string KindName = "siem";
    public const string SendAction = "send";
    public const int DefaultSyslogPort = 6514;
    public const string SyslogApp = "teambeats";

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.SecuritySiem;

    public bool Listed => false;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } = [new(SendAction, ConnectorSideEffects.Write, true, [])];

    public static SiemSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object ? settings.Deserialize<SiemSettings>(JsonDefaults.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (call.Action != SendAction)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        if (call.Input.ValueKind != JsonValueKind.Object || !call.Input.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
        }
        var settings = Parse(call.Connection.Settings);
        return settings?.Transport switch
        {
            SiemTransports.Https => await PostAsync(call, settings, events, ct),
            SiemTransports.Syslog => await SyslogAsync(settings, events, ct),
            _ => PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused),
        };
    }

    private async Task<PackCallResult> PostAsync(PackCall call, SiemSettings settings, JsonElement events, CancellationToken ct)
    {
        if (settings.Url is null || settings.Auth is null)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        var uri = new Uri(settings.Url);
        if (!await allowlist.IsAllowedAsync(uri, ct))
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed);
        }
        var keys = await webhooks.SecretsAsync(call.Connection.TenantId, settings.Auth, ct);
        if (keys.Count == 0)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.CredentialMissing);
        }
        var body = JsonSerializer.Serialize(new { batch = call.IdempotencyKey, events }, JsonDefaults.Options);
        var timestamp = clock.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(WebhookSignature.DeliveryHeader, call.IdempotencyKey);
        request.Headers.TryAddWithoutValidation(WebhookSignature.EventHeader, "security.events");
        request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader, string.Join(",", keys.Select(k => WebhookSignature.Compute(k, timestamp, body))));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
        {
            return PackCallResult.Ok(recordCount: events.GetArrayLength());
        }
        if (status is 401 or 403)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused);
        }
        var retryAfter = response.Headers.RetryAfter?.Delta is { } delay ? (delay > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delay) : (TimeSpan?)null;
        return status switch
        {
            429 => PackCallResult.Technical(ConnectorErrorClasses.Throttled, retryAfter: retryAfter),
            408 or >= 500 => PackCallResult.Technical(ConnectorErrorClasses.ServerError, mayHaveActed: true, retryAfter: retryAfter),
            _ => PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused),
        };
    }

    private async Task<PackCallResult> SyslogAsync(SiemSettings settings, JsonElement events, CancellationToken ct)
    {
        if (settings.Host is null || settings.Port is not { } port)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        if (!await allowlist.IsAllowedAsync(new Uri("https://" + settings.Host), ct))
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed);
        }
        var messages = events.EnumerateArray().Select(SyslogMessage).ToList();
        try
        {
            await syslog.SendAsync(settings.Host, port, messages, ct);
            return PackCallResult.Ok(recordCount: messages.Count);
        }
        catch (AuthenticationException)
        {
            // The server's certificate was not trusted: sending again does not help until it is fixed.
            return PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // A broken connection may have delivered part of the batch; receivers drop repeats by event ID.
            return PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: ex is IOException);
        }
    }

    /// <summary>
    /// RFC 5424: facility 13 (log audit), severity warning for denied and failed outcomes and informational otherwise;
    /// the action as message ID (printable ASCII, at most 32 characters); the event as JSON.
    /// </summary>
    public static string SyslogMessage(JsonElement e)
    {
        var outcome = e.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
        var severity = outcome is "denied" or "failure" ? 4 : 6;
        var time = e.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "-";
        var action = e.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "-";
        var msgId = new string(action.Where(c => c is > ' ' and < (char)127).Take(32).ToArray());
        return $"<{13 * 8 + severity}>1 {time} {SyslogApp} {SyslogApp} - {(msgId.Length == 0 ? "-" : msgId)} - {e.GetRawText()}";
    }

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("transport", "validation.siem.transport");
        var errors = new ValidationErrors();
        object stored;
        if (s.Transport == SiemTransports.Https)
        {
            string? url = null;
            if (s.Url is null || !Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
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
            if (s.Auth?.Type != WebhookPack.SignatureType || SecretReferences.NameFor(tenantId, s.Auth.SecretRef) is null)
            {
                errors.Add("secretRef", "validation.custom_rest.secret_ref");
            }
            stored = new { transport = SiemTransports.Https, url, auth = new { type = WebhookPack.SignatureType, secretRef = s.Auth?.SecretRef } };
        }
        else if (s.Transport == SiemTransports.Syslog)
        {
            var host = s.Host?.Trim().ToLowerInvariant();
            if (host is null || host.Length is 0 or > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                errors.Add("host", "validation.siem.host");
            }
            else if (!await allowlist.IsAllowedAsync(new Uri("https://" + host), ct))
            {
                errors.Add("host", "validation.custom_rest.host_not_allowed");
            }
            var port = s.Port ?? DefaultSyslogPort;
            if (port is < 1 or > 65535)
            {
                errors.Add("port", "validation.siem.port");
            }
            stored = new { transport = SiemTransports.Syslog, host, port };
        }
        else
        {
            throw AppException.Validation("transport", "validation.siem.transport");
        }
        errors.ThrowIfAny();
        return JsonSerializer.SerializeToElement(stored, JsonDefaults.Options);
    }

    public Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PackCheck>>([]);
}
