using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// DPoP proofs (spec 035 R3, T021, RFC 9449). A client that holds a key pair signs a short proof for each request: the
/// public key in the header, the method and URL of the request, the time, a one-time ID and, on a resource call, the
/// hash of the access token. Tokens issued with a proof carry the key's thumbprint (<c>cnf.jkt</c>) and are only
/// accepted with a fresh proof signed by the same key, so a stolen token is useless without the key. Keys follow the
/// rules of client keys: EC P-256 for ES256, or RSA of at least 2048 bits for RS256 or PS256, never with private parts.
/// </summary>
public sealed class DpopProofs(IDbSession session, IClock clock)
{
    public const string Header = "DPoP";
    public const string TokenType = "DPoP";
    public const string ProofType = "dpop+jwt";
    public const string InvalidProof = "invalid_dpop_proof";

    /// <summary>The private claim that keeps the key thumbprint inside a refresh token, so a refresh needs the same key.</summary>
    public const string ThumbprintClaim = "tb_jkt";

    /// <summary>How old a proof may be, and how far ahead of the server's clock it may be.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxAhead = TimeSpan.FromSeconds(60);

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    /// <summary>The single DPoP header of the request, null when there is none, or empty text when there are several.</summary>
    public static string? HeaderOf(HttpRequest request) =>
        request.Headers[Header] switch
        {
            { Count: 0 } => null,
            { Count: 1 } one => one.ToString(),
            _ => "",
        };

    /// <summary>The URL a proof names: the origin and path of the request, without query or fragment (RFC 9449 section 4.3).</summary>
    public static string TargetOf(HttpRequest request) => OAuthServer.Origin(request) + request.PathBase + request.Path;

    /// <summary>The <c>ath</c> value of an access token: base64url of its SHA-256.</summary>
    public static string HashOf(string accessToken) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));

    /// <summary>
    /// Checks a proof and records its ID so it cannot be used again. Returns the thumbprint of the proof's key, or null
    /// when the proof is not valid for this request. <paramref name="accessToken"/> is given on resource calls only.
    /// </summary>
    public async Task<string?> VerifyAsync(string? proof, string method, string target, string? accessToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(proof) || proof.Length > 8192 || Check(proof, method, target, accessToken, clock.UtcNow) is not { } valid)
        {
            return null;
        }
        var now = clock.UtcNow;
        var fresh = await session.ExecAsync(
            "insert into publicapi.dpop_proof (tenant_id, jti_hash, expires_at) values (platform.current_tenant_id(), @hash, @expires) on conflict do nothing",
            new
            {
                hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valid.Thumbprint + "|" + valid.Jti))),
                expires = (valid.IssuedAt > now ? valid.IssuedAt : now).Add(MaxAge + MaxAhead),
            }, ct);
        return fresh == 0 ? null : valid.Thumbprint;
    }

    /// <summary>The checks of a proof that need no storage: format, key, signature, method, URL, time and token hash.</summary>
    internal static (string Thumbprint, string Jti, DateTimeOffset IssuedAt)? Check(string proof, string method, string target, string? accessToken, DateTimeOffset now)
    {
        var parts = proof.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }
        JsonElement header;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]));
            header = document.RootElement.Clone();
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
        if (header.ValueKind != JsonValueKind.Object
            || !header.TryGetProperty("typ", out var typ) || typ.ValueKind != JsonValueKind.String || typ.GetString() != ProofType
            || !header.TryGetProperty("alg", out var alg) || alg.ValueKind != JsonValueKind.String || !ClientKeys.Algorithms.Contains(alg.GetString())
            || !header.TryGetProperty("jwk", out var jwk) || ClientKeys.Parse(jwk, out _) is not { } key)
        {
            return null;
        }
        var signingKey = new JsonWebKey(key.Json);
        if ((signingKey.Kty == "EC") != (alg.GetString() == SecurityAlgorithms.EcdsaSha256))
        {
            return null;
        }
        var result = Handler.ValidateTokenAsync(proof, new TokenValidationParameters
        {
            IssuerSigningKey = signingKey,
            ValidAlgorithms = ClientKeys.Algorithms,
            ValidTypes = [ProofType],
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            RequireExpirationTime = false,
            RequireSignedTokens = true,
        }).GetAwaiter().GetResult();
        if (!result.IsValid)
        {
            return null;
        }
        var token = (JsonWebToken)result.SecurityToken;
        string? Text(string name) => token.TryGetPayloadValue<string>(name, out var value) ? value : null;
        if (Text("jti") is not { Length: > 0 and <= 200 } jti || Text("htm") != method
            || !string.Equals(Text("htu")?.Split('?', '#')[0], target, StringComparison.Ordinal)
            || !token.TryGetPayloadValue<long>("iat", out var iat))
        {
            return null;
        }
        var issued = DateTimeOffset.FromUnixTimeSeconds(iat);
        if (issued < now - MaxAge || issued > now + MaxAhead)
        {
            return null;
        }
        if (accessToken is not null ? Text("ath") != HashOf(accessToken) : Text("ath") is not null)
        {
            return null;
        }
        return (key.Thumbprint, jti, issued);
    }
}
