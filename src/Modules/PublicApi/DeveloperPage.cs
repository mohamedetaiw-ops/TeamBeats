using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;

namespace TeamBeats.Modules.PublicApi;

/// <summary>A public operation as the developer page lists it, with its scope and, when it is on its way out, its replacement and date.</summary>
public sealed record DeveloperOperation(string Method, string Path, string Scope, PublicDeprecationMetadata? Deprecation);

/// <summary>A scope the tenant's package includes.</summary>
public sealed record DeveloperScope(string Key, string DescriptionKey, string Holder, bool UserConsent);

/// <summary>An MCP tool or resource the tenant's package includes, with the scope that allows it.</summary>
public sealed record DeveloperMcpItem(string Name, string Scope, string Kind, string? Description);

/// <summary>
/// The tenant's developer page (spec 035 US7, FR-014): where the public OpenAPI document, the authorization server
/// metadata and the MCP server are, and the operations, scopes, events, MCP tools and resources, with deprecations, as
/// far as the tenant's package includes them. Addresses are on the tenant's own host.
/// </summary>
public sealed record DeveloperPageView(
    string OpenApiUrl,
    string AuthorizationServerMetadataUrl,
    string McpUrl,
    string ProtectedResourceMetadataUrl,
    IReadOnlyList<DeveloperOperation> Operations,
    IReadOnlyList<DeveloperScope> Scopes,
    IReadOnlyList<EventCatalogueEntry> Events,
    IReadOnlyList<DeveloperMcpItem> Mcp,
    IReadOnlyList<DeveloperOperation> Deprecations);

public sealed class DeveloperPage(
    IAuthorizationService authz,
    IEntitlementService entitlements,
    EndpointDataSource endpoints,
    EventSubscriptions events,
    IEnumerable<Agents.Contracts.IAgentTool> tools)
{
    public async Task<DeveloperPageView> GetAsync(string origin, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        await authz.DemandAsync(Permissions.ApiView, AuthzTarget.Tenant, ct);

        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in ScopeCatalogue.All)
        {
            var all = true;
            foreach (var capability in scope.Capabilities)
            {
                all = all && await entitlements.IsEnabledAsync(capability, ct);
            }
            if (all)
            {
                available.Add(scope.Key);
            }
        }

        var operations = endpoints.Endpoints.OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Scope: e.Metadata.GetMetadata<PublicScopeMetadata>()?.Scope))
            .Where(e => e.Scope is not null && available.Contains(e.Scope) && e.Endpoint.RoutePattern.RawText is not null)
            .SelectMany(e => (e.Endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m =>
                new DeveloperOperation(m, e.Endpoint.RoutePattern.RawText!, e.Scope!, e.Endpoint.Metadata.GetMetadata<PublicDeprecationMetadata>())))
            .OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Method, StringComparer.Ordinal)
            .ToList();

        var scopes = ScopeCatalogue.All.Where(s => available.Contains(s.Key))
            .Select(s => new DeveloperScope(s.Key, s.DescriptionKey, s.Holder.ToString().ToLowerInvariant(), s.UserConsent)).ToList();

        var mcp = new List<DeveloperMcpItem>();
        if (await entitlements.IsEnabledAsync(Capabilities.ApiMcp, ct))
        {
            var writeTools = await entitlements.IsEnabledAsync(Capabilities.ApiMcpWriteTools, ct);
            var definitions = tools.Select(t => t.Definition).GroupBy(d => d.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var scope in ScopeCatalogue.All.Where(s => available.Contains(s.Key)))
            {
                foreach (var name in scope.Tools ?? [])
                {
                    var definition = definitions.GetValueOrDefault(name);
                    var write = definition is not null && definition.SideEffect != Agents.Contracts.ToolSideEffects.Read;
                    if (!write || writeTools)
                    {
                        mcp.Add(new DeveloperMcpItem(name, scope.Key, write ? "write_tool" : "tool", definition?.Description));
                    }
                }
                foreach (var resource in scope.Resources ?? [])
                {
                    mcp.Add(new DeveloperMcpItem($"{McpResources.Scheme}://{resource}/{{id}}", scope.Key, "resource", null));
                }
            }
        }

        var catalogue = (await events.AvailableAsync(ct)).Where(e => e.Available).ToList();
        return new DeveloperPageView(
            origin + "/openapi/" + PublicEndpoints.DocumentName + ".json",
            origin + "/.well-known/oauth-authorization-server/oauth",
            origin + BearerTokens.McpPath,
            origin + "/.well-known/oauth-protected-resource" + BearerTokens.McpPath,
            operations,
            scopes,
            catalogue,
            mcp,
            operations.Where(o => o.Deprecation is not null).ToList());
    }
}
