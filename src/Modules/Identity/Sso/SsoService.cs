using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Sso;

/// <summary>Where the browser goes after a sign-in step. Problems are reason codes the web app turns into localized pages.</summary>
public sealed record SsoStep(string Location, string? BindingCookie = null, string? SessionToken = null);

public static class SsoProblems
{
    public const string NotConfigured = "not_configured";
    public const string IdpUnavailable = "idp_unavailable";
    public const string NoAccess = "no_access";
    public const string TenantSuspended = "tenant_suspended";
    public const string Failed = "failed";

    public static string PagePath(string reason) => "/signin/problem?reason=" + reason;
}

/// <summary>
/// Entra ID sign-in (spec 003 US2) with the OIDC authorization code flow and PKCE. The browser is bound to the pending
/// sign-in by an HTTP-only cookie (login CSRF), the state is single use (callback and code replay), and the nonce in
/// the ID token must match that state (token replay). Issuer is pinned to the tenant's configured directory.
/// </summary>
public sealed class SsoService(
    IDbSession session,
    TenantContextAccessor context,
    IdentityProviderStore providers,
    IOidcProvider oidc,
    ISecretStore secrets,
    IEntitlementService entitlements,
    SignInService signIn,
    Directory.DirectorySyncService directorySync,
    IAuditWriter audit,
    IClock clock)
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public async Task<SsoStep> StartAsync(TenantInfo tenant, string callbackUri, string? returnTo, CancellationToken ct)
    {
        context.SetTenant(tenant.Id);
        if (tenant.Status != TenantLifecycleStatus.Active)
        {
            return new SsoStep(SsoProblems.PagePath(SsoProblems.TenantSuspended));
        }
        var config = await providers.GetEnabledAsync(ct);
        if (config is null || !await entitlements.IsEnabledAsync(Capabilities.IdentityOidc, ct))
        {
            return new SsoStep(SsoProblems.PagePath(SsoProblems.NotConfigured));
        }

        OpenIdConnectConfiguration metadata;
        try
        {
            metadata = await oidc.GetConfigurationAsync(config.Issuer, ct);
        }
        catch (OidcProviderUnavailableException)
        {
            await FailAsync("idp_unavailable", config.Issuer, ct);
            return new SsoStep(SsoProblems.PagePath(SsoProblems.IdpUnavailable));
        }

        var state = Random();
        var nonce = Random();
        var verifier = Random();
        var binding = Random();
        var now = clock.UtcNow;
        await session.ExecAsync("delete from identity.oidc_login_state where expires_at < @cutoff", new { cutoff = now - TimeSpan.FromHours(1) }, ct);
        await session.ExecAsync(
            """
            insert into identity.oidc_login_state (tenant_id, id, state_hash, browser_binding_hash, idp_config_id, nonce, code_verifier, return_path, expires_at)
            values (@t, @id, @state, @binding, @idp, @nonce, @verifier, @ret, @exp)
            """,
            new { t = tenant.Id, id = Ids.New(), state = Hash(state), binding = Hash(binding), idp = config.Id, nonce, verifier,
                ret = ReturnPaths.Sanitize(returnTo), exp = now + StateLifetime }, ct);

        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["redirect_uri"] = callbackUri,
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var location = metadata.AuthorizationEndpoint + "?" + string.Join("&", query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        return new SsoStep(location, BindingCookie: binding);
    }

    public async Task<SsoStep> CallbackAsync(TenantInfo tenant, string callbackUri, string? code, string? state, string? error,
        string? bindingCookie, CancellationToken ct)
    {
        context.SetTenant(tenant.Id);
        if (string.IsNullOrEmpty(state))
        {
            await FailAsync("state_missing", null, ct);
            return Problem(SsoProblems.Failed);
        }

        // Consume first: whatever happens next, this state can never be used again.
        var pending = await session.QuerySingleOrDefaultAsync(
            """
            update identity.oidc_login_state set consumed_at = now()
            where state_hash = @h and consumed_at is null and expires_at > @now
            returning idp_config_id, browser_binding_hash, nonce, code_verifier, return_path
            """, new { h = Hash(state), now = clock.UtcNow },
            r => new PendingSignIn(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ct);
        if (pending is null)
        {
            await FailAsync("state_invalid", null, ct);
            return Problem(SsoProblems.Failed);
        }
        if (string.IsNullOrEmpty(bindingCookie) || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Hash(bindingCookie)), Encoding.ASCII.GetBytes(pending.BindingHash)))
        {
            await FailAsync("browser_binding_mismatch", null, ct);
            return Problem(SsoProblems.Failed);
        }

        var config = await providers.GetAsync(pending.IdpConfigId, ct);
        if (config is not { Enabled: true })
        {
            await FailAsync("provider_disabled", null, ct);
            return Problem(SsoProblems.NotConfigured);
        }
        if (!string.IsNullOrEmpty(error))
        {
            await FailAsync(error == "temporarily_unavailable" ? "idp_unavailable" : "idp_error", config.Issuer, ct);
            return Problem(error == "temporarily_unavailable" ? SsoProblems.IdpUnavailable : SsoProblems.Failed);
        }
        if (string.IsNullOrEmpty(code))
        {
            await FailAsync("code_missing", config.Issuer, ct);
            return Problem(SsoProblems.Failed);
        }

        var secret = await secrets.GetTenantSecretAsync(tenant.Id, config.SecretRef, ct);
        if (secret is null)
        {
            await FailAsync("secret_unavailable", config.Issuer, ct);
            return Problem(SsoProblems.IdpUnavailable);
        }

        JsonWebToken token;
        OpenIdConnectConfiguration metadata;
        try
        {
            metadata = await oidc.GetConfigurationAsync(config.Issuer, ct);
            var raw = await oidc.RedeemCodeAsync(metadata, new CodeRedemption(config.ClientId, secret, code, callbackUri, pending.CodeVerifier), ct);
            token = await ValidateAsync(raw, config, metadata, pending.Nonce);
        }
        catch (OidcProviderUnavailableException)
        {
            await FailAsync("idp_unavailable", config.Issuer, ct);
            return Problem(SsoProblems.IdpUnavailable);
        }
        catch (OidcTokenRejectedException rejected)
        {
            await FailAsync(rejected.Reason, config.Issuer, ct);
            return Problem(SsoProblems.Failed);
        }

        // Entra ID: the link key is the object ID, which directory sync also sees; sub differs per app registration (FR-003).
        if (!token.TryGetPayloadValue<string>("oid", out var subject) || string.IsNullOrEmpty(subject))
        {
            await FailAsync("subject_missing", config.Issuer, ct);
            return Problem(SsoProblems.Failed);
        }
        var email = Claim(token, "email") ?? Claim(token, "preferred_username") ?? string.Empty;
        var name = Claim(token, "name") ?? email;
        var overage = token.TryGetPayloadValue<Dictionary<string, object>>("_claim_names", out var names) && names.ContainsKey("groups");
        IReadOnlyList<string>? groups = overage ? null : token.Claims.Where(c => c.Type == "groups").Select(c => c.Value).ToList();

        // While the directory is synced, it decides who has an account: JIT is off and synced users link by object ID (US6 scenario 3).
        var jit = config.JitEnabled && !await directorySync.IsActiveAsync(ct);
        var policy = new ProviderSignInPolicy(config.Id, jit, config.JitDomains, groups, config.GroupRoleMap);
        var result = await signIn.SignInAsync(tenant, config.Issuer, subject.ToLowerInvariant(), email, name, "entra", "admin", ct, policy);
        return result.Outcome switch
        {
            SignInOutcome.Succeeded => new SsoStep(pending.ReturnPath, SessionToken: result.SessionToken),
            SignInOutcome.RefusedTenantInactive => Problem(SsoProblems.TenantSuspended),
            _ => Problem(SsoProblems.NoAccess),
        };
    }

    private async Task<JsonWebToken> ValidateAsync(string raw, IdentityProviderConfig config, OpenIdConnectConfiguration metadata, string nonce)
    {
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidAudience = config.ClientId,
            IssuerSigningKeys = metadata.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = ClockSkew,
            LifetimeValidator = (notBefore, expires, _, p) =>
            {
                var now = clock.UtcNow.UtcDateTime;
                return expires is { } e && e.ToUniversalTime() + p.ClockSkew >= now
                    && (notBefore is not { } nb || nb.ToUniversalTime() - p.ClockSkew <= now);
            },
        };
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(raw, parameters);
        if (!result.IsValid)
        {
            throw new OidcTokenRejectedException(result.Exception switch
            {
                SecurityTokenInvalidIssuerException => "wrong_issuer",
                SecurityTokenInvalidAudienceException => "wrong_audience",
                SecurityTokenInvalidLifetimeException or SecurityTokenExpiredException => "expired",
                SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException => "bad_signature",
                SecurityTokenInvalidAlgorithmException => "bad_algorithm",
                _ => "invalid_token",
            });
        }
        var token = (JsonWebToken)result.SecurityToken;
        if (!token.TryGetPayloadValue<string>("nonce", out var tokenNonce) || string.IsNullOrEmpty(tokenNonce))
        {
            throw new OidcTokenRejectedException("nonce_missing");
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tokenNonce), Encoding.UTF8.GetBytes(nonce)))
        {
            throw new OidcTokenRejectedException("nonce_mismatch");
        }
        return token;
    }

    private static string? Claim(JsonWebToken token, string type) =>
        token.TryGetPayloadValue<string>(type, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static SsoStep Problem(string reason) => new(SsoProblems.PagePath(reason));

    private async Task FailAsync(string reason, string? issuer, CancellationToken ct)
    {
        context.SetActor(ActorType.Anonymous, null);
        await audit.WriteAsync(new AuditEntry(AuditActions.SignInFailed, "user", null, AuditOutcome.Failure,
            Changes: new { reason, issuer, method = "entra" }), ct);
    }

    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record PendingSignIn(Guid IdpConfigId, string BindingHash, string Nonce, string CodeVerifier, string ReturnPath);
}

/// <summary>Return paths after sign-in are same-origin relative paths only (spec 003 T017, open redirect).</summary>
public static class ReturnPaths
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512 || value[0] != '/' || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("/\\", StringComparison.Ordinal) || value.Any(c => char.IsControl(c) || c == '\\')
            || value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }
        return value;
    }
}
