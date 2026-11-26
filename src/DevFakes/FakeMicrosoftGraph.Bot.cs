using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TeamBeats.DevFakes;

/// <summary>
/// The Bot Framework side of the fake (spec 015 US5 scenario 2): the published signing keys, bot tokens for the
/// TeamBeats application, and the Bot Connector service that opens a chat with a person who has the Teams app and
/// takes card messages. <see cref="IssueBotToken"/> signs what Bot Framework would send with a quick action.
/// </summary>
public sealed partial class FakeMicrosoftGraph
{
    public const string BotServiceUrl = "https://smba.trafficmanager.net/teams/";
    private const string BotKeyId = "fake-bot-key";

    private readonly RSA _botKey = RSA.Create(2048);
    private readonly string _botToken = "fake-bot-token-" + Guid.NewGuid().ToString("N");

    /// <summary>A card the fake Bot Connector accepted: the directory, the person's object ID, the card as sent and its order.</summary>
    public sealed record BotMessage(string Directory, string UserId, JsonElement Card, long Seq = 0);

    private long _botSeq;

    public ConcurrentBag<BotMessage> BotMessages { get; } = [];

    /// <summary>A card the bot posted as a new thread in a team channel: the directory, the channel ID and the card.</summary>
    public sealed record ChannelPost(string Directory, string ChannelId, JsonElement Card);

    public ConcurrentBag<ChannelPost> ChannelPosts { get; } = [];

    private readonly ConcurrentDictionary<string, bool> _botChannels = new();

    /// <summary>Installs the TeamBeats bot in the team of a channel, so it can post there.</summary>
    public void InstallBotInChannel(Guid directoryId, string channelId) => _botChannels[$"{directoryId:D}/{channelId}"] = true;

    /// <summary>The Bot Connector refuses the bot's credentials, as when the installation has no bot registered.</summary>
    public bool BotRefused { get; set; }

    /// <summary>A Bot Framework token for a call to the bot, as Bot Framework signs it.</summary>
    public string IssueBotToken(string serviceUrl = BotServiceUrl, string audience = ClientId, string issuer = "https://api.botframework.com", TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = now.AddMinutes(-1),
            IssuedAt = now.AddMinutes(-1),
            Expires = now + (lifetime ?? TimeSpan.FromMinutes(30)),
            Subject = new ClaimsIdentity([new Claim("serviceurl", serviceUrl)]),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_botKey) { KeyId = BotKeyId }, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>The Adaptive Card a Teams user would press: the invoke activity Teams posts to the bot.</summary>
    public static string BotInvoke(Guid directoryId, string objectId, string verb, string token, string? reason = null, string serviceUrl = BotServiceUrl) =>
        JsonSerializer.Serialize(new
        {
            type = "invoke",
            name = "adaptiveCard/action",
            serviceUrl,
            from = new { id = "29:fake", aadObjectId = objectId },
            conversation = new { id = "a:fake", tenantId = directoryId.ToString("D") },
            channelData = new { tenant = new { id = directoryId.ToString("D") } },
            value = new { action = new { type = "Action.Execute", verb, data = reason is null ? (object)new { tb = token } : new { tb = token, reason } } },
        });

    /// <summary>A chat message a Teams user writes to the bot, in their personal chat with it.</summary>
    public static string BotChat(Guid directoryId, string objectId, string text, string locale = "en-US", string serviceUrl = BotServiceUrl) =>
        JsonSerializer.Serialize(new
        {
            type = "message",
            serviceUrl,
            locale,
            text,
            from = new { id = "29:fake", aadObjectId = objectId },
            conversation = new { id = $"a:{directoryId:D}~{objectId}", tenantId = directoryId.ToString("D"), conversationType = "personal" },
            channelData = new { tenant = new { id = directoryId.ToString("D") } },
        });

    private async Task<HttpResponseMessage?> BotAsync(HttpRequestMessage request, Uri uri, CancellationToken ct)
    {
        if (uri.Host == "login.botframework.com" && uri.AbsolutePath == "/v1/.well-known/openidconfiguration")
        {
            return Json(new
            {
                issuer = "https://api.botframework.com",
                jwks_uri = "https://login.botframework.com/v1/.well-known/keys",
                id_token_signing_alg_values_supported = new[] { "RS256" },
            });
        }
        if (uri.Host == "login.botframework.com" && uri.AbsolutePath == "/v1/.well-known/keys")
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_botKey.ExportParameters(false)) { KeyId = BotKeyId });
            return Json(new { keys = new[] { new { kty = "RSA", use = "sig", kid = BotKeyId, n = jwk.N, e = jwk.E, endorsements = new[] { "msteams" } } } });
        }
        if (uri.Host == "login.microsoftonline.com" && uri.AbsolutePath == "/botframework.com/oauth2/v2.0/token")
        {
            var form = FakeIdentityProvider.ParseQuery("?" + await request.Content!.ReadAsStringAsync(ct));
            if (form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret
                || form.GetValueOrDefault("scope") != "https://api.botframework.com/.default")
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_client\"}") };
            }
            return Json(new { access_token = _botToken, token_type = "Bearer", expires_in = 3600 });
        }
        if (!uri.ToString().StartsWith(BotServiceUrl + "v3/conversations", StringComparison.Ordinal))
        {
            return null;
        }
        if (BotRefused || request.Headers.Authorization?.Parameter != _botToken)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
        var rest = uri.AbsolutePath["/teams/v3/conversations".Length..];
        if (rest.Length == 0 && body.TryGetProperty("isGroup", out var group) && group.ValueKind == JsonValueKind.True)
        {
            var team = body.GetProperty("tenantId").GetString() ?? string.Empty;
            var channel = body.GetProperty("channelData").GetProperty("channel").GetProperty("id").GetString() ?? string.Empty;
            if (!_botChannels.ContainsKey($"{team}/{channel}"))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"BotNotInConversationRoster\"}}") };
            }
            ChannelPosts.Add(new ChannelPost(team, channel, body.GetProperty("activity").GetProperty("attachments")[0].GetProperty("content").Clone()));
            return Json(new { id = $"{channel};messageid=1", activityId = "1" });
        }
        if (rest.Length == 0)
        {
            var directory = body.GetProperty("tenantId").GetString() ?? string.Empty;
            var member = body.GetProperty("members")[0].GetProperty("id").GetString() ?? string.Empty;
            if (!_teamsInstalls.ContainsKey($"{directory}/{member}"))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"BotNotInConversationRoster\"}}") };
            }
            return Json(new { id = $"a:{directory}~{member}" });
        }
        var parts = Uri.UnescapeDataString(rest.Trim('/')).Split('/');
        if (parts.Length != 2 || parts[1] != "activities" || !parts[0].StartsWith("a:", StringComparison.Ordinal) || parts[0][2..].Split('~') is not [var dir, var user])
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var card = body.GetProperty("attachments")[0].GetProperty("content").Clone();
        BotMessages.Add(new BotMessage(dir, user, card, Interlocked.Increment(ref _botSeq)));
        return Json(new { id = "msg-" + Guid.NewGuid().ToString("N")[..8] });
    }
}
