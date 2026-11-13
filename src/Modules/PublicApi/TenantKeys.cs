using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// The signing and encryption keys of each tenant's authorization server (spec 035 FR-001, section 8). Keys are derived
/// from one master secret held in the secret manager (configuration <see cref="Setting"/>) together with the tenant ID and
/// a 90 day period, so every tenant has its own keys, every API instance derives the same ones, and nothing is stored.
/// After a rotation the previous period's keys still validate for 24 hours. Development and test hosts without the
/// setting use a new random secret per process, like the offline key (spec 030).
/// </summary>
public sealed class TenantKeys
{
    public const string Setting = "PublicApi:SigningKey";
    public static readonly TimeSpan Period = TimeSpan.FromDays(90);
    public static readonly TimeSpan Overlap = TimeSpan.FromHours(24);

    private readonly byte[]? secret;
    private readonly IClock clock;
    private readonly ConcurrentDictionary<(Guid, long), ECDsaSecurityKey> signing = new();

    public TenantKeys(IConfiguration configuration, IHostEnvironment environment, IClock clock)
    {
        this.clock = clock;
        var configured = configuration[Setting];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var bytes = Convert.FromBase64String(configured.Trim());
                secret = bytes.Length >= 32 ? bytes : null;
            }
            catch (FormatException)
            {
                secret = null;
            }
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
        {
            secret = RandomNumberGenerator.GetBytes(32);
        }
    }

    /// <summary>False when the install has no master secret: the authorization server then refuses every request.</summary>
    public bool Available => secret is not null;

    private long CurrentPeriod => clock.UtcNow.ToUnixTimeSeconds() / (long)Period.TotalSeconds;

    /// <summary>The key that signs new tokens of the tenant.</summary>
    public ECDsaSecurityKey SigningKey(Guid tenantId) => Signing(tenantId, CurrentPeriod);

    /// <summary>The key that encrypts the tenant's authorization codes and refresh tokens.</summary>
    public SymmetricSecurityKey EncryptionKey(Guid tenantId) => Encryption(tenantId, CurrentPeriod);

    /// <summary>The current signing key, and the previous one during the 24 hours after a rotation.</summary>
    public IReadOnlyList<ECDsaSecurityKey> ValidationKeys(Guid tenantId) =>
        Periods().Select(p => Signing(tenantId, p)).ToList();

    public IReadOnlyList<SymmetricSecurityKey> DecryptionKeys(Guid tenantId) =>
        Periods().Select(p => Encryption(tenantId, p)).ToList();

    private IEnumerable<long> Periods()
    {
        var current = CurrentPeriod;
        yield return current;
        var started = DateTimeOffset.FromUnixTimeSeconds(current * (long)Period.TotalSeconds);
        if (clock.UtcNow - started < Overlap)
        {
            yield return current - 1;
        }
    }

    private ECDsaSecurityKey Signing(Guid tenantId, long period) => signing.GetOrAdd((tenantId, period), k => DeriveSigning(k.Item1, k.Item2));

    private ECDsaSecurityKey DeriveSigning(Guid tenantId, long period)
    {
        var master = secret ?? throw new InvalidOperationException("The public API signing secret is not configured.");
        // A P-256 private key is any scalar between 1 and the group order; an HMAC output is outside it with a chance of
        // about 2^-32, and the counter then picks the next candidate.
        for (var attempt = 0; ; attempt++)
        {
            var d = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes($"sig|{tenantId:N}|{period}|{attempt}"));
            try
            {
                var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
                return new ECDsaSecurityKey(ecdsa) { KeyId = KeyId(tenantId, period) };
            }
            catch (CryptographicException) when (attempt < 8)
            {
            }
        }
    }

    private SymmetricSecurityKey Encryption(Guid tenantId, long period)
    {
        var master = secret ?? throw new InvalidOperationException("The public API signing secret is not configured.");
        return new SymmetricSecurityKey(HMACSHA256.HashData(master, Encoding.UTF8.GetBytes($"enc|{tenantId:N}|{period}")))
        {
            KeyId = "e" + KeyId(tenantId, period),
        };
    }

    /// <summary>A key ID that names neither the tenant nor the period in clear.</summary>
    private string KeyId(Guid tenantId, long period)
    {
        var master = secret!;
        return Base64UrlEncoder.Encode(HMACSHA256.HashData(master, Encoding.UTF8.GetBytes($"kid|{tenantId:N}|{period}"))[..12]);
    }

    /// <summary>The public part of a signing key as a JSON web key for the JWKS document.</summary>
    public static JsonWebKey PublicJwk(ECDsaSecurityKey key)
    {
        var p = key.ECDsa.ExportParameters(false);
        return new JsonWebKey
        {
            Kty = JsonWebAlgorithmsKeyTypes.EllipticCurve,
            Crv = JsonWebKeyECTypes.P256,
            Kid = key.KeyId,
            Use = JsonWebKeyUseNames.Sig,
            Alg = SecurityAlgorithms.EcdsaSha256,
            X = Base64UrlEncoder.Encode(p.Q.X),
            Y = Base64UrlEncoder.Encode(p.Q.Y),
        };
    }
}
