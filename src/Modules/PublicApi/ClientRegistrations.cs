using System.Text.Json.Serialization;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>An RFC 7591 client registration request, as an MCP client sends it. Only the fields TeamBeats uses.</summary>
public sealed record ClientRegistrationRequest(
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris,
    [property: JsonPropertyName("grant_types")] IReadOnlyList<string>? GrantTypes,
    [property: JsonPropertyName("response_types")] IReadOnlyList<string>? ResponseTypes,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod,
    [property: JsonPropertyName("scope")] string? Scope);

/// <summary>The RFC 7591 answer: the new client ID and what was registered. The client is held until an administrator approves it.</summary>
public sealed record ClientRegistrationResponse(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt,
    [property: JsonPropertyName("client_name")] string ClientName,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("grant_types")] IReadOnlyList<string> GrantTypes,
    [property: JsonPropertyName("response_types")] IReadOnlyList<string> ResponseTypes,
    [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>Why a registration was refused, as RFC 7591 section 3.2.2 names it.</summary>
public sealed record ClientRegistrationError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string Description);

/// <summary>
/// Dynamic client registration for MCP clients (spec 035 FR-023, RFC 7591), only where the tenant turned it on. Open
/// registration stays off: a client that registers itself becomes a pending public MCP client app, which the
/// authorization server does not know until an API administrator approves it, and the external AI client policy must
/// still allow it. Public clients only (PKCE, no secret), at most <see cref="MaxPending"/> waiting at a time.
/// </summary>
public sealed class ClientRegistrations(IDbSession session, IEntitlementService entitlements, IAuditWriter audit, IOutbox outbox, IClock clock)
{
    public const int MaxPending = 20;

    /// <summary>Registrations a tenant accepts in a minute, across all clients.</summary>
    public const int PerMinute = 5;

    private static readonly string[] Grants = ["authorization_code", "refresh_token"];

    /// <summary>Registers the client as pending, or answers why not. The caller has checked that registration is on.</summary>
    public async Task<(ClientRegistrationResponse? Registered, ClientRegistrationError? Refused)> RegisterAsync(ClientRegistrationRequest request, CancellationToken ct)
    {
        var name = request.ClientName?.Trim() ?? "";
        if (name.Length is < 1 or > 80)
        {
            return (null, Metadata("client_name must be between 1 and 80 characters."));
        }
        if (request.TokenEndpointAuthMethod is not (null or "none"))
        {
            return (null, Metadata("Only public clients (token_endpoint_auth_method none) may register."));
        }
        if ((request.GrantTypes ?? []).Any(g => !Grants.Contains(g, StringComparer.Ordinal)) || (request.ResponseTypes ?? []).Any(r => r != "code"))
        {
            return (null, Metadata("Only the authorization code grant with refresh tokens is supported."));
        }
        var redirects = new ValidationErrors();
        ApiRules.CheckRedirectUris(request.RedirectUris, AppKinds.McpClient, redirects);
        if (redirects.Any)
        {
            return (null, new ClientRegistrationError("invalid_redirect_uri", "Use exact HTTPS redirect URIs, or loopback HTTP ones, without wildcards or fragments."));
        }
        var scopes = (request.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        if (scopes.Count == 0)
        {
            scopes = await DefaultScopesAsync(ct);
        }
        var errors = new ValidationErrors();
        ApiRules.CheckScopes(scopes, AppKinds.McpClient, errors);
        if (errors.Any || !await InPackageAsync(scopes, ct))
        {
            return (null, Metadata("A requested scope is unknown or not available."));
        }

        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':publicapi.apps', 0))", null, ct);
        var pending = await session.ScalarAsync<long>("select count(*) from publicapi.app where status = 'pending'", null, ct);
        var count = await session.ScalarAsync<long>("select count(*) from publicapi.app", null, ct);
        if (pending >= MaxPending || (await entitlements.GetLimitAsync(Capabilities.ApiMaxApps, ct) is { } max && count >= max))
        {
            return (null, Metadata("The organization cannot take more registrations now."));
        }

        var id = Ids.New();
        var clientId = ApiRules.NewClientId();
        var uris = request.RedirectUris!.Distinct().ToArray();
        await session.ExecAsync(
            """
            insert into publicapi.app (tenant_id, id, client_id, kind, name, scopes, redirect_uris, status, self_registered)
            values (platform.current_tenant_id(), @id, @clientId, 'mcp_client', @name, @scopes, @uris, 'pending', true)
            """, new { id, clientId, name, scopes = scopes.ToArray(), uris }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiAppRegistered, "api_app", id.ToString(),
            Changes: new { Name = name, Kind = AppKinds.McpClient, Scopes = scopes, RedirectUris = uris, status = "pending", selfRegistered = true }), ct);
        await outbox.EnqueueAsync(new AppRegistered(id, AppKinds.McpClient), ct);
        return (new ClientRegistrationResponse(clientId, clock.UtcNow.ToUnixTimeSeconds(), name, uris, Grants, ["code"], "none", string.Join(' ', scopes)), null);
    }

    /// <summary>Without a scope, the read scopes an employee may allow alone that the MCP server serves.</summary>
    private async Task<List<string>> DefaultScopesAsync(CancellationToken ct)
    {
        var list = new List<string>();
        foreach (var scope in ScopeCatalogue.All.Where(s => s.AllowsDelegated && s.UserConsent && ((s.Tools?.Count ?? 0) > 0 || (s.Resources?.Count ?? 0) > 0)))
        {
            if (await InPackageAsync([scope.Key], ct))
            {
                list.Add(scope.Key);
            }
        }
        return list;
    }

    private async Task<bool> InPackageAsync(IEnumerable<string> scopes, CancellationToken ct)
    {
        foreach (var capability in scopes.Select(ScopeCatalogue.Find).OfType<ScopeDefinition>().SelectMany(s => s.Capabilities).Distinct())
        {
            if (!await entitlements.IsEnabledAsync(capability, ct))
            {
                return false;
            }
        }
        return true;
    }

    private static ClientRegistrationError Metadata(string description) => new("invalid_client_metadata", description);
}
