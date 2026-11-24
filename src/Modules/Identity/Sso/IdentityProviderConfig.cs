using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Sso;

/// <summary>Entra group (object ID) to built-in role at a scope (spec 003 US2 scenario 6).</summary>
public sealed record GroupRoleMapping(string GroupId, string RoleKey, string ScopeType, Guid? ScopeId);

/// <summary>
/// A tenant's identity provider (spec 003 FR-002). The client secret is a secret-manager reference only. A Google
/// provider names the hosted domains of this tenant; an AD FS provider names the AD DS directory connection whose users
/// it signs in (US4, US5). <see cref="LastProblem"/> is the reason code of the last refused token, for the admin.
/// </summary>
public sealed record IdentityProviderConfig(
    Guid Id,
    string Kind,
    string Issuer,
    string ClientId,
    string SecretRef,
    string? DisplayName,
    bool JitEnabled,
    IReadOnlyList<string> JitDomains,
    IReadOnlyList<GroupRoleMapping> GroupRoleMap,
    bool Enabled,
    IReadOnlyList<string>? HostedDomains = null,
    Guid? DirectoryConnectionId = null,
    string? LastProblem = null,
    DateTimeOffset? LastProblemAt = null,
    string Protocol = "oidc",
    string? SsoUrl = null,
    IReadOnlyList<string>? Certificates = null);

public sealed record IdentityProviderRequest(
    string? Kind,
    string? Issuer,
    string? ClientId,
    string? SecretRef,
    string? DisplayName,
    bool? JitEnabled,
    IReadOnlyList<string>? JitDomains,
    IReadOnlyList<GroupRoleMapping>? GroupRoleMap,
    bool? Enabled,
    IReadOnlyList<string>? HostedDomains = null,
    Guid? DirectoryConnectionId = null,
    string? Protocol = null,
    string? SsoUrl = null,
    IReadOnlyList<string>? Certificates = null);

/// <summary>Reads and changes the tenant's identity provider. Admin access is checked by the endpoint.</summary>
public sealed partial class IdentityProviderStore(IDbSession session, ITenantContext context, IAuditWriter audit, IAuthorizationProvisioning roles)
{
    public const string KindEntra = "entra";
    public const string KindGoogle = "google";
    public const string KindAdfs = "adfs";

    /// <summary>The one issuer of Google sign-in. It is shared by every Google account, so hosted domains pin the tenant.</summary>
    public const string GoogleIssuer = "https://accounts.google.com";

    public const string KindGeneric = "generic";

    public static readonly IReadOnlyList<string> Kinds = [KindEntra, KindGoogle, KindAdfs];

    /// <summary>
    /// SAML 2.0 kinds. Google's SAML app sends the primary email as name ID, which is not immutable (FR-003), so Google
    /// Workspace signs in over OIDC only.
    /// </summary>
    public static readonly IReadOnlyList<string> SamlKinds = [KindGeneric, KindAdfs];

    private const string Columns =
        "id, kind, issuer, client_id, secret_ref, display_name, jit_enabled, jit_domains, group_role_map::text, enabled, hosted_domains, directory_connection_id, last_problem, last_problem_at, protocol, sso_url, saml_certificates";

    private static readonly string[] ScopeTypes = ["tenant", "org_unit", "org_subtree"];

    public Task<IdentityProviderConfig?> GetEnabledAsync(CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            $"select {Columns} from identity.idp_config where enabled order by updated_at desc limit 1", null, Map, ct);

    public Task<IdentityProviderConfig?> GetAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from identity.idp_config where id = @id", new { id }, Map, ct);

    public Task<IdentityProviderConfig?> GetCurrentAsync(CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            $"select {Columns} from identity.idp_config order by enabled desc, updated_at desc limit 1", null, Map, ct);

    /// <summary>Keeps why the provider's last token was refused, so the admin can fix the claim rule (US5 scenario 2).</summary>
    public Task RecordProblemAsync(Guid id, string reason, CancellationToken ct) =>
        session.ExecAsync("update identity.idp_config set last_problem = @reason, last_problem_at = now() where id = @id",
            new { id, reason = reason.Length > 64 ? reason[..64] : reason }, ct);

    /// <summary>A sign-in got through, so the last problem is fixed.</summary>
    public Task ClearProblemAsync(Guid id, CancellationToken ct) =>
        session.ExecAsync("update identity.idp_config set last_problem = null, last_problem_at = null where id = @id and last_problem is not null",
            new { id }, ct);

    public async Task<IdentityProviderConfig> SaveAsync(IdentityProviderRequest request, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var builtIn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in (request.GroupRoleMap ?? []).Select(m => m.RoleKey).OfType<string>().Distinct())
        {
            if (await roles.IsBuiltInRoleKeyAsync(key, ct))
            {
                builtIn.Add(key);
            }
        }
        var config = Validate(tenantId, request, builtIn);
        if (config.DirectoryConnectionId is { } connectionId && !await session.ScalarAsync<bool>(
                "select exists (select 1 from identity.directory_connection where connection_id = @connectionId and source = 'adds')", new { connectionId }, ct))
        {
            // AD FS signs in the users of one AD DS connection of this tenant; RLS keeps other tenants' connections out of sight.
            throw AppException.Validation("directoryConnectionId", "validation.idp.directory_connection_invalid");
        }
        var current = await GetCurrentAsync(ct);
        var id = current?.Id ?? Ids.New();
        var map = JsonSerializer.Serialize(config.GroupRoleMap, JsonDefaults.Options);
        if (current is null)
        {
            await session.ExecAsync(
                """
                insert into identity.idp_config (tenant_id, id, protocol, kind, issuer, client_id, secret_ref, display_name,
                  jit_enabled, jit_domains, group_role_map, enabled, hosted_domains, directory_connection_id, sso_url, saml_certificates)
                values (@t, @id, @protocol, @kind, @issuer, @client, @secret, @name, @jit, @domains, @map::jsonb, @enabled, @hosted, @connection, @sso, @certs)
                """,
                new { t = tenantId, id, kind = config.Kind, issuer = config.Issuer, client = config.ClientId, secret = config.SecretRef,
                    name = config.DisplayName, jit = config.JitEnabled, domains = config.JitDomains.ToArray(), map, enabled = config.Enabled,
                    hosted = (config.HostedDomains ?? []).ToArray(), connection = config.DirectoryConnectionId, protocol = config.Protocol,
                    sso = config.SsoUrl, certs = (config.Certificates ?? []).ToArray() }, ct);
        }
        else
        {
            await session.ExecAsync(
                """
                update identity.idp_config set kind = @kind, issuer = @issuer, client_id = @client, secret_ref = @secret,
                  display_name = @name, jit_enabled = @jit, jit_domains = @domains, group_role_map = @map::jsonb,
                  enabled = @enabled, hosted_domains = @hosted, directory_connection_id = @connection,
                  protocol = @protocol, sso_url = @sso, saml_certificates = @certs,
                  last_problem = null, last_problem_at = null, updated_at = now()
                where id = @id
                """,
                new { id, kind = config.Kind, issuer = config.Issuer, client = config.ClientId, secret = config.SecretRef,
                    name = config.DisplayName, jit = config.JitEnabled, domains = config.JitDomains.ToArray(), map, enabled = config.Enabled,
                    hosted = (config.HostedDomains ?? []).ToArray(), connection = config.DirectoryConnectionId, protocol = config.Protocol,
                    sso = config.SsoUrl, certs = (config.Certificates ?? []).ToArray() }, ct);
        }

        // No secret values are written: the reference is dropped by the audit writer and the value is never read here.
        await audit.WriteAsync(new AuditEntry(AuditActions.IdpConfigChanged, "idp_config", id.ToString(),
            Changes: new
            {
                provider = config.Kind,
                protocol = config.Protocol,
                config.SsoUrl,
                certificates = (config.Certificates ?? []).Select(CertificateThumbprint).ToList(),
                config.Issuer,
                config.ClientId,
                config.JitEnabled,
                jitDomains = config.JitDomains,
                hostedDomains = config.HostedDomains,
                directoryConnection = config.DirectoryConnectionId,
                groupMappings = config.GroupRoleMap.Count,
                config.Enabled,
                created = current is null,
            }), ct);
        return config with { Id = id };
    }

    /// <summary>Schema validation of tenant configuration (constitution XXIX). Errors are resource keys.</summary>
    public static IdentityProviderConfig Validate(Guid tenantId, IdentityProviderRequest request, IReadOnlySet<string> builtInRoleKeys)
    {
        var errors = new ValidationErrors();
        var protocol = request.Protocol?.Trim().ToLowerInvariant() ?? "oidc";
        if (protocol is not ("oidc" or SamlService.Protocol))
        {
            errors.Add("protocol", "validation.idp.protocol_unsupported");
        }
        var saml = protocol == SamlService.Protocol;
        var kind = request.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!(saml ? SamlKinds : Kinds).Contains(kind))
        {
            errors.Add("kind", "validation.idp.kind_unsupported");
        }
        var issuer = request.Issuer?.Trim() ?? string.Empty;
        var issuerValid = saml ? IsEntityId(issuer) : kind switch
        {
            // Pinned to one directory: the multi-tenant "common" and "organizations" endpoints are refused (plan: Entra ID).
            KindEntra => EntraIssuer().IsMatch(issuer),
            KindGoogle => issuer == GoogleIssuer,
            // The customer's own AD FS farm, which may be reachable only inside their network (US5 scenario 3).
            KindAdfs => AdfsIssuer().IsMatch(issuer),
            _ => true,
        };
        if (!issuerValid)
        {
            errors.Add("issuer", "validation.idp.issuer_invalid");
        }
        var clientId = saml ? string.Empty : request.ClientId?.Trim() ?? string.Empty;
        var clientValid = saml || kind switch
        {
            KindEntra => Guid.TryParse(clientId, out _),
            KindGoogle => GoogleClientId().IsMatch(clientId),
            KindAdfs => clientId.Length is > 0 and <= 200 && !clientId.Any(char.IsWhiteSpace),
            _ => true,
        };
        if (!clientValid)
        {
            errors.Add("clientId", "validation.idp.client_id_invalid");
        }

        string? ssoUrl = null;
        var certificates = new List<string>();
        if (saml)
        {
            ssoUrl = request.SsoUrl?.Trim();
            if (ssoUrl is null || ssoUrl.Length > 2000 || !Uri.TryCreate(ssoUrl, UriKind.Absolute, out var sso) || sso.Scheme != Uri.UriSchemeHttps
                || sso.UserInfo.Length > 0 || sso.Fragment.Length > 0)
            {
                errors.Add("ssoUrl", "validation.idp.sso_url_invalid");
            }
            var given = (request.Certificates ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (given.Count is < 1 or > 3)
            {
                errors.Add("certificates", "validation.idp.certificates_count");
            }
            foreach (var text in given)
            {
                switch (ReadCertificate(text))
                {
                    case (null, var problem):
                        errors.Add("certificates", problem!);
                        break;
                    case ({ } der, _):
                        certificates.Add(der);
                        break;
                }
            }
            certificates = certificates.Distinct(StringComparer.Ordinal).ToList();
        }

        var hosted = (request.HostedDomains ?? []).Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();
        if (kind == KindGoogle && !saml)
        {
            if (hosted.Count == 0)
            {
                errors.Add("hostedDomains", "validation.idp.hosted_domains_required");
            }
            else if (hosted.Count > 50 || hosted.Any(d => !DomainName().IsMatch(d)))
            {
                errors.Add("hostedDomains", "validation.idp.domain_invalid");
            }
        }
        else
        {
            hosted = [];
        }
        Guid? connection = kind == KindAdfs ? request.DirectoryConnectionId : null;
        if (kind == KindAdfs && connection is null)
        {
            errors.Add("directoryConnectionId", "validation.idp.directory_connection_required");
        }
        // SAML has no client secret: trust is the IdP's public signing certificate.
        var secretRef = saml ? string.Empty : request.SecretRef?.Trim() ?? string.Empty;
        if (!saml && SecretReferences.NameFor(tenantId, secretRef) is null)
        {
            errors.Add("secretRef", "validation.idp.secret_ref_invalid");
        }
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        if (displayName is { Length: > 100 })
        {
            errors.Add("displayName", "validation.idp.display_name_too_long");
        }

        var domains = (request.JitDomains ?? []).Select(d => d.Trim().ToLowerInvariant()).Distinct().ToList();
        if (domains.Any(d => !DomainName().IsMatch(d)))
        {
            errors.Add("jitDomains", "validation.idp.domain_invalid");
        }
        var jit = request.JitEnabled ?? false;
        if (jit && domains.Count == 0)
        {
            errors.Add("jitDomains", "validation.idp.jit_domains_required");
        }

        // Only Entra ID tokens carry group object IDs; other providers get roles from synced directory groups.
        var map = kind == KindEntra && !saml ? (request.GroupRoleMap ?? []).ToList() : [];
        if (map.Count > 200)
        {
            errors.Add("groupRoleMap", "validation.idp.too_many_mappings");
        }
        var cleaned = new List<GroupRoleMapping>();
        foreach (var m in map)
        {
            var scope = m.ScopeType?.Trim().ToLowerInvariant() ?? string.Empty;
            var valid = Guid.TryParse(m.GroupId, out var groupId)
                && builtInRoleKeys.Contains(m.RoleKey ?? string.Empty)
                && ScopeTypes.Contains(scope)
                && (scope == "tenant") == (m.ScopeId is null);
            if (!valid)
            {
                errors.Add("groupRoleMap", "validation.idp.mapping_invalid");
                break;
            }
            cleaned.Add(new GroupRoleMapping(groupId.ToString("D"), m.RoleKey!, scope, m.ScopeId));
        }

        errors.ThrowIfAny();
        return new IdentityProviderConfig(Guid.Empty, kind, issuer, kind == KindEntra ? clientId.ToLowerInvariant() : clientId, secretRef, displayName,
            jit, domains, cleaned, request.Enabled ?? true, hosted, connection, Protocol: protocol, SsoUrl: ssoUrl, Certificates: certificates);
    }

    /// <summary>A SAML entity ID: an absolute http, https or urn identifier. It names the IdP and is never fetched.</summary>
    private static bool IsEntityId(string value) =>
        value.Length is > 0 and <= 500 && !value.Any(char.IsWhiteSpace)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "urn";

    /// <summary>An IdP signing certificate as PEM or base64 DER: the base64 DER of a current RSA certificate, or the problem key.</summary>
    public static (string? Der, string? Problem) ReadCertificate(string text)
    {
        var body = string.Concat(text.Replace("-----BEGIN CERTIFICATE-----", string.Empty).Replace("-----END CERTIFICATE-----", string.Empty)
            .Where(c => !char.IsWhiteSpace(c)));
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(Convert.FromBase64String(body));
            using var key = certificate.GetRSAPublicKey();
            if (key is null || key.KeySize < 2048)
            {
                return (null, "validation.idp.certificate_invalid");
            }
            return certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow
                ? (null, "validation.idp.certificate_expired")
                : (Convert.ToBase64String(certificate.RawData), null);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return (null, "validation.idp.certificate_invalid");
        }
    }

    /// <summary>The SHA-256 thumbprint that audit records instead of the certificate.</summary>
    public static string CertificateThumbprint(string der) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(der)));

    private static IdentityProviderConfig Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Get<string>("display_name"), r.GetBoolean(6),
            r.GetFieldValue<string[]>(7), JsonSerializer.Deserialize<List<GroupRoleMapping>>(r.GetString(8), JsonDefaults.Options) ?? [],
            r.GetBoolean(9), r.GetFieldValue<string[]>(10), r.Get<Guid?>("directory_connection_id"), r.Get<string>("last_problem"),
            r.Get<DateTimeOffset?>("last_problem_at"), r.GetString(14), r.Get<string>("sso_url"), r.GetFieldValue<string[]>(16));

    [GeneratedRegex("^https://login\\.microsoftonline\\.com/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/v2\\.0$")]
    private static partial Regex EntraIssuer();

    // https only, a host name and the AD FS path; no query, fragment or user info.
    [GeneratedRegex("^https://(?=.{1,253}/)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\\.)+[A-Za-z]{2,63}(:[0-9]{1,5})?/adfs$")]
    private static partial Regex AdfsIssuer();

    [GeneratedRegex("^[0-9]{1,30}-[a-z0-9]{1,64}\\.apps\\.googleusercontent\\.com$")]
    private static partial Regex GoogleClientId();

    [GeneratedRegex("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$")]
    private static partial Regex DomainName();
}
