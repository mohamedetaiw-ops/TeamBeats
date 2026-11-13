using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// Public keys for private key JWT client authentication (spec 035 FR-005, RFC 7523). An app registers a public JWK,
/// an EC P-256 key for ES256 or an RSA key of at least 2048 bits for RS256 or PS256, and signs a short-lived assertion
/// for each token request. TeamBeats keeps only the public part; a key with private parameters is refused.
/// </summary>
internal static class ClientKeys
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>The longest an assertion may live: a signed request is good for minutes, not hours.</summary>
    public static readonly TimeSpan MaxAssertionLifetime = TimeSpan.FromMinutes(10);

    public static readonly string[] Algorithms = [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256];

    private static readonly string[] PrivateParameters = ["d", "p", "q", "dp", "dq", "qi", "k", "oth"];

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    /// <summary>The public key in the form TeamBeats stores it, and its RFC 7638 thumbprint; or the validation key that refuses it.</summary>
    public static (string Json, string Thumbprint)? Parse(JsonElement? value, out string? error)
    {
        error = "validation.api.public_key_invalid";
        if (value is not { ValueKind: JsonValueKind.Object } json)
        {
            return null;
        }
        if (PrivateParameters.Any(p => json.TryGetProperty(p, out _)))
        {
            error = "validation.api.public_key_private";
            return null;
        }
        string? Text(string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var kty = Text("kty");
        var alg = Text("alg");
        Dictionary<string, string> stored;
        try
        {
            switch (kty)
            {
                case "EC" when Text("crv") == "P-256" && Text("x") is { } x && Text("y") is { } y
                               && Base64UrlEncoder.DecodeBytes(x).Length == 32 && Base64UrlEncoder.DecodeBytes(y).Length == 32
                               && alg is null or SecurityAlgorithms.EcdsaSha256:
                    stored = new() { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = x, ["y"] = y };
                    break;
                case "RSA" when Text("n") is { } n && Text("e") is { } e && Base64UrlEncoder.DecodeBytes(n).Length * 8 >= 2048
                                && alg is null or SecurityAlgorithms.RsaSha256 or SecurityAlgorithms.RsaSsaPssSha256:
                    stored = new() { ["kty"] = "RSA", ["n"] = n, ["e"] = e };
                    break;
                default:
                    return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }
        if (alg is not null)
        {
            stored["alg"] = alg;
        }
        var text = JsonSerializer.Serialize(stored);
        var key = new JsonWebKey(text);
        error = null;
        return (text, Base64UrlEncoder.Encode(key.ComputeJwkThumbprint()));
    }

    /// <summary>
    /// Checks a client assertion against the app's live keys: signed by one of them with an allowed algorithm, issued by
    /// and about the client, addressed to this tenant's issuer or token endpoint, with an expiry in the next ten minutes
    /// and a JWT ID. The caller records the JWT ID so the assertion cannot be used twice.
    /// </summary>
    public static async Task<(Guid Credential, string Jti, DateTimeOffset Expires)?> ValidateAsync(string assertion, string clientId, string issuer,
        IReadOnlyList<(Guid Id, string Jwk)> keys, DateTimeOffset now)
    {
        if (keys.Count == 0 || !Handler.CanReadToken(assertion))
        {
            return null;
        }
        foreach (var (id, jwk) in keys)
        {
            var result = await Handler.ValidateTokenAsync(assertion, new TokenValidationParameters
            {
                ValidIssuer = clientId,
                ValidAudiences = [issuer, issuer + "/token"],
                IssuerSigningKey = new JsonWebKey(jwk),
                ValidAlgorithms = Algorithms,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                LifetimeValidator = (_, expires, _, _) => expires is { } e && e > now.UtcDateTime.AddSeconds(-30) && e <= now.Add(MaxAssertionLifetime).UtcDateTime,
            });
            if (!result.IsValid)
            {
                continue;
            }
            var token = (JsonWebToken)result.SecurityToken;
            if (token.Subject != clientId || string.IsNullOrEmpty(token.Id) || token.Id.Length > 200)
            {
                return null;
            }
            return (id, token.Id, new DateTimeOffset(token.ValidTo, TimeSpan.Zero));
        }
        return null;
    }
}
