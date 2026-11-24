using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// Bot Framework for the TeamBeats Teams bot (spec 015 US5 scenario 2, FR-018). The bot is the platform's Microsoft 365
/// application (<c>Integrations:Microsoft365:ClientId</c> and <c>ClientSecret</c>) registered as an Azure bot, turned on
/// with <c>Integrations:Microsoft365:TeamsBot:Enabled</c>. Incoming calls carry a Bot Framework token, checked against
/// the keys Bot Framework publishes, for this application, from the service address the activity names. Cards go out
/// through the Bot Connector service (<c>Integrations:Microsoft365:TeamsBot:ServiceUrl</c>, the Teams global address by
/// default) into the person's chat with the bot, which exists only once the bot is installed for them.
/// </summary>
public sealed class BotFramework
{
    public const string ClientName = "teamsbot";
    public const string EnabledSetting = "Integrations:Microsoft365:TeamsBot:Enabled";
    public const string ServiceUrlSetting = "Integrations:Microsoft365:TeamsBot:ServiceUrl";
    public const string DefaultServiceUrl = "https://smba.trafficmanager.net/teams/";
    public const string OpenIdMetadata = "https://login.botframework.com/v1/.well-known/openidconfiguration";
    public const string Issuer = "https://api.botframework.com";
    public const string TokenUrl = "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token";
    public const string Scope = "https://api.botframework.com/.default";
    public const string CardType = "application/vnd.microsoft.card.adaptive";

    private readonly IHttpClientFactory httpClients;
    private readonly IConfiguration configuration;
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> keys;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private (string Token, DateTimeOffset Expires)? token;

    public BotFramework(IHttpClientFactory httpClients, IConfiguration configuration)
    {
        this.httpClients = httpClients;
        this.configuration = configuration;
        keys = new Lazy<ConfigurationManager<OpenIdConnectConfiguration>>(() => new ConfigurationManager<OpenIdConnectConfiguration>(
            OpenIdMetadata, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(httpClients.CreateClient(ClientName)) { RequireHttps = true }));
    }

    private string AppId => configuration["Integrations:Microsoft365:ClientId"] ?? string.Empty;

    /// <summary>True when this installation runs the bot: it is turned on and the application has credentials.</summary>
    public bool Enabled =>
        string.Equals(configuration[EnabledSetting], "true", StringComparison.OrdinalIgnoreCase)
        && AppId.Length > 0 && !string.IsNullOrEmpty(configuration["Integrations:Microsoft365:ClientSecret"]);

    private string ServiceUrl
    {
        get
        {
            var url = configuration[ServiceUrlSetting] is { Length: > 0 } configured ? configured : DefaultServiceUrl;
            return url.EndsWith('/') ? url : url + "/";
        }
    }

    public async Task<TeamsBotCall> ReadAsync(string? authorization, string body, CancellationToken ct)
    {
        if (!Enabled || authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return new TeamsBotCall(false, null);
        }
        JsonElement activity;
        try
        {
            activity = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            return new TeamsBotCall(false, null);
        }
        if (activity.ValueKind != JsonValueKind.Object || Text(activity, "serviceUrl") is not { } serviceUrl)
        {
            return new TeamsBotCall(false, null);
        }
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(authorization["Bearer ".Length..].Trim(), new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = AppId,
            ConfigurationManager = keys.Value,
            ValidateLifetime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(5),
        });
        if (!result.IsValid || result.Claims.TryGetValue("serviceurl", out var claimed) is false
            || !string.Equals(claimed?.ToString()?.TrimEnd('/'), serviceUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return new TeamsBotCall(false, null);
        }
        if (Text(activity, "type") != "invoke" || Text(activity, "name") != "adaptiveCard/action"
            || !activity.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object
            || Text(action, "verb") is not { } verb
            || !action.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || Text(data, "tb") is not { } tb)
        {
            return new TeamsBotCall(true, null);
        }
        var from = activity.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object ? Text(f, "aadObjectId") : null;
        var tenant = activity.TryGetProperty("channelData", out var channel) && channel.ValueKind == JsonValueKind.Object
            && channel.TryGetProperty("tenant", out var t) && t.ValueKind == JsonValueKind.Object ? Text(t, "id") : null;
        tenant ??= activity.TryGetProperty("conversation", out var conversation) && conversation.ValueKind == JsonValueKind.Object ? Text(conversation, "tenantId") : null;
        if (from is null || tenant is null)
        {
            return new TeamsBotCall(true, null);
        }
        var reason = Text(data, "reason") is { } r && r.Trim().Length > 0 ? r.Trim() : null;
        return new TeamsBotCall(true, new TeamsBotAction(tenant, from, verb, tb, reason));
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 2000 } s ? s : null;

    /// <summary>Sends the card into the person's chat with the bot, creating the chat reference first.</summary>
    public async Task<TeamsSendOutcome> SendAsync(string entraTenantId, string objectId, object card, string summary, CancellationToken ct)
    {
        if (!Enabled)
        {
            return TeamsSendOutcome.Unavailable;
        }
        string bearer;
        try
        {
            bearer = await TokenAsync(ct);
        }
        catch (HttpRequestException)
        {
            return TeamsSendOutcome.Transient;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return TeamsSendOutcome.Transient;
        }
        catch (BotCredentialsRefusedException)
        {
            return TeamsSendOutcome.Unavailable;
        }
        using var client = httpClients.CreateClient(ClientName);
        try
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, ServiceUrl + "v3/conversations")
            {
                Content = JsonContent.Create(new
                {
                    isGroup = false,
                    bot = new { id = "28:" + AppId },
                    members = new[] { new { id = objectId } },
                    tenantId = entraTenantId,
                    channelData = new { tenant = new { id = entraTenantId } },
                }),
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var created = await client.SendAsync(create, ct);
            if (Outcome(created.StatusCode) is { } failed)
            {
                return failed;
            }
            var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetString() ?? string.Empty;
            using var send = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl}v3/conversations/{Uri.EscapeDataString(conversation)}/activities")
            {
                Content = JsonContent.Create(new { type = "message", summary, attachments = new[] { new { contentType = CardType, content = card } } }),
            };
            send.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var sent = await client.SendAsync(send, ct);
            return Outcome(sent.StatusCode) ?? TeamsSendOutcome.Sent;
        }
        catch (HttpRequestException)
        {
            return TeamsSendOutcome.Transient;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return TeamsSendOutcome.Transient;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return TeamsSendOutcome.Transient;
        }
    }

    /// <summary>Teams answers 403 or 404 when the bot is not installed for the person or they blocked it.</summary>
    private static TeamsSendOutcome? Outcome(HttpStatusCode status) => status switch
    {
        _ when (int)status is >= 200 and < 300 => null,
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound => TeamsSendOutcome.NotInstalled,
        HttpStatusCode.Unauthorized => TeamsSendOutcome.Unavailable,
        _ => TeamsSendOutcome.Transient,
    };

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await tokenLock.WaitAsync(ct);
        try
        {
            if (token is { } cached && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return cached.Token;
            }
            using var client = httpClients.CreateClient(ClientName);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = AppId,
                ["client_secret"] = configuration["Integrations:Microsoft365:ClientSecret"] ?? string.Empty,
                ["scope"] = Scope,
            });
            using var response = await client.PostAsync(TokenUrl, form, ct);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                throw new BotCredentialsRefusedException();
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Bot Framework token endpoint answered " + (int)response.StatusCode);
            }
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var value = body.GetProperty("access_token").GetString() ?? throw new HttpRequestException("No access token.");
            var seconds = body.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            token = (value, DateTimeOffset.UtcNow.AddSeconds(seconds));
            return value;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private sealed class BotCredentialsRefusedException : Exception;

    /// <summary>The Adaptive Card of a message with quick actions (Universal Actions, so the answer replaces the card).</summary>
    public static object Card(TeamsCard card, string link, bool rtl)
    {
        var body = new List<object>
        {
            new { type = "TextBlock", text = card.Title, weight = "Bolder", size = "Medium", wrap = true },
            new { type = "TextBlock", text = card.Text, wrap = true },
        };
        if (card.Actions.Any(a => a.NeedsReason) && card.ReasonLabel is { } label)
        {
            body.Add(new { type = "Input.Text", id = "reason", label, isMultiline = true, maxLength = 1000 });
        }
        var actions = card.Actions.Select(a => (object)new { type = "Action.Execute", title = a.Label, verb = a.Verb, data = new { tb = a.Token } }).ToList();
        actions.Add(new { type = "Action.OpenUrl", title = card.OpenLabel, url = link });
        return new Dictionary<string, object>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["rtl"] = rtl,
            ["body"] = body,
            ["actions"] = actions,
        };
    }

    /// <summary>The card that replaces the pressed one: the outcome, and the TeamBeats link when there is one.</summary>
    public static object ReplyCard(TeamsCardReply reply, string? link, bool rtl)
    {
        var actions = link is not null && reply.OpenLabel is { } open ? new object[] { new { type = "Action.OpenUrl", title = open, url = link } } : [];
        return new Dictionary<string, object>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["rtl"] = rtl,
            ["body"] = new object[] { new { type = "TextBlock", text = reply.Text, wrap = true } },
            ["actions"] = actions,
        };
    }
}

/// <summary>The bot for the current request: replies resolve their link on the tenant's own host.</summary>
public sealed class TeamsBot(BotFramework framework, TenantLinks links) : ITeamsBot
{
    public Task<TeamsBotCall> ReadAsync(string? authorization, string body, CancellationToken ct = default) => framework.ReadAsync(authorization, body, ct);

    public async Task<object> ReplyAsync(TeamsCardReply reply, CancellationToken ct = default)
    {
        var link = reply.Link is { } path ? await links.AbsoluteAsync(path, ct) : null;
        return new { statusCode = 200, type = BotFramework.CardType, value = BotFramework.ReplyCard(reply, link, reply.Rtl) };
    }
}
