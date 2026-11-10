using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Slack;

/// <summary>
/// Settings of a Slack connection: the Web API address (https://slack.com/api unless the workspace uses another, on the
/// tenant allowlist) and the bot token as a secret manager reference. <paramref name="ExpiresOn"/> is the token's expiry date.
/// </summary>
public sealed record SlackSettings(string BaseUrl, string? BotTokenRef, string? ExpiresOn = null)
{
    public const string DefaultBaseUrl = "https://slack.com/api";

    public RestSettings Rest => new(BaseUrl, new RestAuth("bearer", SecretRef: BotTokenRef, ExpiresOn: ExpiresOn));
}

/// <summary>
/// The Slack pack (spec 017 US8, spec 015 FR-019): direct messages for notifications and links shared to a channel.
/// Its actions declare no inputs, so they are never route steps or agent tools: only the notification and sharing
/// features call them, through the runtime, so every call is in run history with rate limits and the circuit breaker.
/// Slack answers 200 with <c>ok: false</c> for most errors; those are mapped to the plain classes here.
/// </summary>
public sealed class SlackPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : IConnectorPack
{
    public const string KindName = "slack";
    public const string LookupUser = "lookup_user";
    public const string SendDirectMessage = "send_direct_message";
    public const string ListChannels = "list_channels";
    public const string ShareLink = "share_link";

    /// <summary>The Slack user is not in the workspace any more, or never was: delivery falls back to other channels (US8 scenario 2).</summary>
    public const string UserGone = "user_gone";
    public const string ChannelUnavailable = "channel_unavailable";

    public const int MaxText = 3000;

    private static readonly string[] CredentialErrors = ["invalid_auth", "not_authed", "token_revoked", "token_expired", "account_inactive", "missing_scope", "no_permission"];
    private static readonly string[] ServerErrors = ["internal_error", "fatal_error", "service_unavailable", "request_timeout"];
    private static readonly string[] GoneErrors = ["users_not_found", "user_not_found", "user_disabled", "cannot_dm_bot"];
    private static readonly string[] ChannelErrors = ["channel_not_found", "not_in_channel", "is_archived", "restricted_action"];

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationConnectors;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new(LookupUser, ConnectorSideEffects.Read, true, [UserGone]),
        new(SendDirectMessage, ConnectorSideEffects.ExternalEffect, false, [UserGone]),
        new(ListChannels, ConnectorSideEffects.Read, true, []),
        new(ShareLink, ConnectorSideEffects.ExternalEffect, false, [ChannelUnavailable]),
    ];

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (Parse(call.Connection.Settings) is not { BotTokenRef: not null } settings)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        var input = call.Input;
        switch (call.Action)
        {
            case LookupUser:
            {
                if (Text(input, "email") is not { Length: > 0 and <= 320 } email)
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var (failure, body) = await SendAsync(call.Connection, settings, HttpMethod.Get, "users.lookupByEmail?email=" + Uri.EscapeDataString(email), null, GoneErrors,
                    UserGone, false, ct);
                if (failure is not null)
                {
                    return failure;
                }
                var user = body.GetProperty("user");
                var id = Text(user, "id");
                if (id is null || user.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
                {
                    return id is null ? PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse) : PackCallResult.Business(UserGone);
                }
                return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { slackUserId = id, teamId = Text(user, "team_id") }, JsonDefaults.Options), id);
            }
            case SendDirectMessage:
            {
                if (Text(input, "slack_user_id") is not { } user || !SlackIds.IsMember(user) || Message(input) is not { } message)
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var (openFailure, opened) = await SendAsync(call.Connection, settings, HttpMethod.Post, "conversations.open", new { users = user }, GoneErrors, UserGone, false, ct);
                if (openFailure is not null)
                {
                    return openFailure;
                }
                if (opened.TryGetProperty("channel", out var channel) && Text(channel, "id") is { } dm)
                {
                    return await PostAsync(call.Connection, settings, dm, message, GoneErrors, UserGone, ct);
                }
                return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse);
            }
            case ListChannels:
            {
                var (failure, body) = await SendAsync(call.Connection, settings, HttpMethod.Get,
                    "conversations.list?types=public_channel&exclude_archived=true&limit=200", null, [], null, false, ct);
                if (failure is not null)
                {
                    return failure;
                }
                var channels = body.TryGetProperty("channels", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray()
                        .Where(c => Text(c, "id") is { } id && SlackIds.IsChannel(id) && Text(c, "name") is not null)
                        .Select(c => new { id = Text(c, "id"), name = Cut(Text(c, "name"), 80), member = c.TryGetProperty("is_member", out var m) && m.ValueKind == JsonValueKind.True })
                        .ToList()
                    : [];
                return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { channels }, JsonDefaults.Options), recordCount: channels.Count);
            }
            case ShareLink:
            {
                if (Text(input, "channel_id") is not { } channel || !SlackIds.IsChannel(channel) || Message(input) is not { } message)
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                return await PostAsync(call.Connection, settings, channel, message, ChannelErrors, ChannelUnavailable, ct);
            }
            default:
                return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
    }

    /// <summary>The message text with its link on its own line; Slack shows no preview, so nothing is read from the link.</summary>
    private static string? Message(JsonElement input)
    {
        var text = Text(input, "text");
        var link = Text(input, "link");
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxText || link is not null && !(Uri.TryCreate(link, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps))
        {
            return null;
        }
        // Slack reads <, > and & as markup: escape them so text never becomes a mention or a hidden link.
        var safe = text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        return link is null ? safe : $"{safe}\n<{link}>";
    }

    private async Task<PackCallResult> PostAsync(PackConnection connection, SlackSettings settings, string channel, string text, string[] business, string code, CancellationToken ct)
    {
        var (failure, body) = await SendAsync(connection, settings, HttpMethod.Post, "chat.postMessage",
            new { channel, text, unfurl_links = false, unfurl_media = false }, business, code, true, ct);
        return failure ?? PackCallResult.Ok(JsonSerializer.SerializeToElement(new { channel }, JsonDefaults.Options), Cut(Text(body, "ts"), 64));
    }

    /// <summary>One Web API call. Slack errors in <paramref name="business"/> become <paramref name="code"/>.</summary>
    private async Task<(PackCallResult? Failure, JsonElement Body)> SendAsync(PackConnection connection, SlackSettings settings, HttpMethod method, string path,
        object? body, string[] business, string? code, bool writes, CancellationToken ct)
    {
        var baseUri = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        var uri = new Uri(baseUri, path);
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || !await signIn.IsAllowedAsync(baseUri, ct))
        {
            return (PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed), default);
        }
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        if (await signIn.AuthorizeAsync(connection, settings.Rest, request, ct) is { } refused)
        {
            return (refused, default);
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (signIn.MapStatus(connection, settings.Rest, response) is { } failure)
        {
            return (failure.ErrorClass == ConnectorErrorClasses.ServerError ? failure with { MayHaveActed = writes } : failure, default);
        }
        if (await RestSignIn.ReadJsonAsync(response, ct) is not { ValueKind: JsonValueKind.Object } json || !json.TryGetProperty("ok", out var ok))
        {
            return (PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: writes), default);
        }
        if (ok.ValueKind == JsonValueKind.True)
        {
            return (null, json);
        }
        var error = Text(json, "error") ?? "";
        PackCallResult mapped = error switch
        {
            _ when CredentialErrors.Contains(error) => PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused),
            "ratelimited" => PackCallResult.Technical(ConnectorErrorClasses.Throttled, retryAfter: RestSignIn.RetryAfter(response)),
            _ when ServerErrors.Contains(error) => PackCallResult.Technical(ConnectorErrorClasses.ServerError, mayHaveActed: writes),
            _ when code is not null && business.Contains(error) => PackCallResult.Business(code),
            _ => PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused),
        };
        return (mapped, default);
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Cut(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;

    public static SlackSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object && settings.Deserialize<SlackSettings>(JsonDefaults.Options) is { BaseUrl: not null } s ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DateOnly? CredentialExpiry(JsonElement settings) => RestSignIn.ExpiresOn(Parse(settings)?.Rest.Auth);

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.custom_rest.settings");
        var errors = new ValidationErrors();
        var rest = await signIn.ValidateAsync(tenantId, s.BaseUrl, new RestAuth("bearer", SecretRef: s.BotTokenRef, ExpiresOn: s.ExpiresOn), ["bearer"], errors, ct);
        if (errors.Entries.Any(e => e.Field == "auth.secretRef"))
        {
            errors = Rename(errors, "auth.secretRef", "botTokenRef");
        }
        errors.ThrowIfAny();
        return JsonSerializer.SerializeToElement(new SlackSettings(rest!.BaseUrl, s.BotTokenRef, s.ExpiresOn), JsonDefaults.Options);
    }

    private static ValidationErrors Rename(ValidationErrors errors, string from, string to)
    {
        var renamed = new ValidationErrors();
        foreach (var e in errors.Entries)
        {
            renamed.Add(e.Field == from ? to : e.Field, e.Key);
        }
        return renamed;
    }

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { BotTokenRef: not null } settings)
        {
            return [new PackCheck("settings", false, "integrations.test.settings_invalid", ConnectorErrorClasses.RequestRefused)];
        }
        var checks = await signIn.TestAsync(connection, settings.Rest, ct);
        if (checks.Any(c => !c.Ok))
        {
            return checks;
        }
        PackCheck check;
        try
        {
            var (failure, _) = await SendAsync(connection, settings, HttpMethod.Post, "auth.test", new { }, [], null, false, ct);
            check = failure is null
                ? new PackCheck("system", true, "integrations.test.system_ok")
                : new PackCheck("system", false, failure.ErrorClass == ConnectorErrorClasses.CredentialRefused ? "integrations.test.system_refused" : "integrations.test.system_failed",
                    failure.ErrorClass);
        }
        catch (HttpRequestException)
        {
            check = new PackCheck("system", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            check = new PackCheck("system", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
        }
        checks.Add(check);
        return checks;
    }
}

/// <summary>Slack IDs as the Web API returns them: members start with U or W, public channels with C.</summary>
public static class SlackIds
{
    private static bool Shape(string id, params char[] first) =>
        id.Length is >= 9 and <= 20 && first.Contains(id[0]) && id.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c));

    public static bool IsMember(string id) => Shape(id, 'U', 'W');

    public static bool IsChannel(string id) => Shape(id, 'C');
}
