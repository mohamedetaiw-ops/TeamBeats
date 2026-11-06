using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Primitives;

namespace TeamBeats.Testing;

/// <summary>What the fake Entra ID puts in (or leaves out of) the ID token for one authorization code.</summary>
public sealed record FakeUser(string ObjectId, string Email, string Name)
{
    public IReadOnlyList<string> Groups { get; init; } = [];
    public bool GroupOverage { get; init; }
    public string? IssuerOverride { get; init; }
    public string? AudienceOverride { get; init; }
    public bool OmitNonce { get; init; }
    public string? NonceOverride { get; init; }
    public bool SignWithForeignKey { get; init; }
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan IssuedAgo { get; init; } = TimeSpan.Zero;
    public bool OmitObjectId { get; init; }
}

/// <summary>
/// In-memory Microsoft Entra ID for tests: discovery, signing keys, and a token endpoint that checks the client
/// secret, redirect URI and PKCE verifier like the real one. Any directory ID works. All data is fictional.
/// </summary>
public sealed class FakeIdentityProvider : HttpMessageHandler
{
    public const string Authority = "https://login.microsoftonline.com";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "fake-key-1" };
    private readonly RsaSecurityKey _foreignKey = new(RSA.Create(2048)) { KeyId = "fake-key-1" };
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new(StringComparer.Ordinal);

    public bool Unavailable { get; set; }

    public int TokenRequests;

    public static string IssuerFor(Guid directoryId) => $"{Authority}/{directoryId:D}/v2.0";

    /// <summary>The user "signs in" at the provider for the authorization request in <paramref name="authorizeUrl"/>.</summary>
    public string Authenticate(string authorizeUrl, FakeUser user, string clientSecret)
    {
        var uri = new Uri(authorizeUrl);
        var query = ParseQuery(uri.Query);
        var directory = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new IssuedCode(user, $"{Authority}/{directory}/v2.0", query["client_id"], clientSecret, query["redirect_uri"],
            query["code_challenge"], query["nonce"]);
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        var path = request.RequestUri!.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var directory = segments.Length > 0 ? segments[0] : string.Empty;
        var issuer = $"{Authority}/{directory}/v2.0";

        if (path.EndsWith("/v2.0/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            return Json(new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = $"{Authority}/{directory}/oauth2/v2.0/authorize",
                ["token_endpoint"] = $"{Authority}/{directory}/oauth2/v2.0/token",
                ["jwks_uri"] = $"{Authority}/{directory}/discovery/v2.0/keys",
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "pairwise" },
            });
        }
        if (path.EndsWith("/discovery/v2.0/keys", StringComparison.Ordinal))
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
            return Json(new { keys = new[] { new { kty = "RSA", use = "sig", kid = jwk.Kid, n = jwk.N, e = jwk.E } } });
        }
        if (path.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref TokenRequests);
            var form = ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!_codes.TryRemove(form.GetValueOrDefault("code") ?? string.Empty, out var issued)
                || issued.ClientId != form.GetValueOrDefault("client_id")
                || issued.ClientSecret != form.GetValueOrDefault("client_secret")
                || issued.RedirectUri != form.GetValueOrDefault("redirect_uri")
                || issued.CodeChallenge != Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? string.Empty))))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
            }
            return Json(new { id_token = IdToken(issued), token_type = "Bearer" });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private string IdToken(IssuedCode issued)
    {
        var user = issued.User;
        var claims = new Dictionary<string, object>
        {
            ["email"] = user.Email,
            ["preferred_username"] = user.Email,
            ["name"] = user.Name,
            ["tid"] = issued.Issuer.Split('/')[3],
            ["sub"] = "pairwise-" + user.ObjectId,
        };
        if (!user.OmitObjectId)
        {
            claims["oid"] = user.ObjectId;
        }
        if (!user.OmitNonce)
        {
            claims["nonce"] = user.NonceOverride ?? issued.Nonce;
        }
        if (user.GroupOverage)
        {
            claims["_claim_names"] = new Dictionary<string, object> { ["groups"] = "src1" };
        }
        else if (user.Groups.Count > 0)
        {
            claims["groups"] = user.Groups.ToArray();
        }
        var now = DateTime.UtcNow - user.IssuedAgo;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = user.IssuerOverride ?? issued.Issuer,
            Audience = user.AudienceOverride ?? issued.ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + user.Lifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(user.SignWithForeignKey ? _foreignKey : _key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty);

    private sealed record IssuedCode(FakeUser User, string Issuer, string ClientId, string ClientSecret, string RedirectUri, string CodeChallenge, string Nonce);
}

/// <summary>In-memory secret store for tests. Values are fictional.</summary>
public sealed class TestSecrets : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Set(string reference, string value) => _values[reference] = value;

    public Task<string?> GetTenantSecretAsync(Guid tenantId, string reference, CancellationToken ct = default) =>
        Task.FromResult(SecretReferences.NameFor(tenantId, reference) is null ? null : _values.GetValueOrDefault(reference));
}
