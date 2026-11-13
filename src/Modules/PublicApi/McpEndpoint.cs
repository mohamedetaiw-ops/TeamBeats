using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// The tenant's MCP server (spec 035 FR-019, US3; ADR-0018) at <c>https://&lt;tenant host&gt;/mcp</c>, over Streamable
/// HTTP with the official SDK, stateless so no session outlives a request or crosses tenants. The bearer pipeline in
/// front has already checked the token (an MCP client app acting for an employee, bound to this resource), the tenant
/// switches, the network rules and the call limits. Here the token's scopes name the tools the client may see, and
/// every list and call goes through the Agents tool gateway with the caller type external AI client (FR-020), which
/// decides everything else. Tool results go back as data with each source's reference and classification (FR-024).
/// </summary>
public static class McpEndpoint
{
    /// <summary>The protected resource metadata (RFC 9728) of the MCP server.</summary>
    public const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";

    public const string ServerName = "teambeats";

    public static IServiceCollection AddMcpEndpoint(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };
                // The handlers use the request's own scope, where the bearer pipeline set the tenant and the employee.
                o.ScopeRequests = false;
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler((request, ct) => ListAsync(Http(request.Services), ct))
            .WithCallToolHandler((request, ct) => CallAsync(Http(request.Services), request.Params, ct));
        return services;
    }

    /// <summary>The HTTP request the handler runs in; with stateless HTTP each MCP request is exactly one of them.</summary>
    private static HttpContext Http(IServiceProvider? services) =>
        services?.GetRequiredService<IHttpContextAccessor>().HttpContext ?? throw new InvalidOperationException("The MCP server runs inside an HTTP request.");

    public static IEndpointRouteBuilder MapMcpEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(MetadataPath, async (HttpContext http, TenantContextAccessor tenant, IEntitlementService entitlements, CancellationToken ct) =>
        {
            // The metadata is public and read under the tenant of the host, signed in or not.
            if (tenant.Host.Tenant is not { } host)
            {
                return Results.NotFound();
            }
            if (tenant.TenantId is { } signedIn && signedIn != host.Id)
            {
                tenant.SetActor(ActorType.Anonymous, null);
            }
            tenant.SetTenant(host.Id);
            if (!await OAuthServer.OpenAsync(http.RequestServices, ct) || !await entitlements.IsEnabledAsync(Capabilities.ApiMcp, ct))
            {
                return Results.NotFound();
            }
            var origin = OAuthServer.Origin(http.Request);
            return Results.Json(new Dictionary<string, object>
            {
                ["resource"] = ApiRules.McpResource(origin),
                ["authorization_servers"] = new[] { origin + "/oauth" },
                ["scopes_supported"] = ScopeCatalogue.All.Where(s => s.AllowsDelegated && s.Tools is { Count: > 0 }).Select(s => s.Key).ToArray(),
                ["bearer_methods_supported"] = new[] { "header" },
                ["resource_name"] = "TeamBeats",
            });
        }).ExcludeFromDescription();
        app.MapMcp(BearerTokens.McpPath).ExcludeFromDescription();
        return app;
    }

    /// <summary>The employee and MCP client of the call, and the tools the token's scopes cover under the tenant's package.</summary>
    private static async Task<(ExternalCaller Caller, IReadOnlySet<string> Scoped)> CallerAsync(HttpContext http, CancellationToken ct)
    {
        var caller = http.Features.Get<PublicCaller>() ?? throw new InvalidOperationException("The MCP server runs behind the bearer pipeline.");
        var app = http.Features.Get<AppRecord>()!;
        var entitlements = http.RequestServices.GetRequiredService<IEntitlementService>();
        var scoped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in caller.Scopes.Select(ScopeCatalogue.Find).Where(s => s is not null))
        {
            var enabled = true;
            foreach (var capability in scope!.Capabilities)
            {
                enabled &= await entitlements.IsEnabledAsync(capability, ct);
            }
            if (enabled)
            {
                scoped.UnionWith(scope.Tools ?? []);
            }
        }
        return (new ExternalCaller(app.Id, app.ClientId, caller.UserId!.Value, app.Name), scoped);
    }

    internal static async ValueTask<ListToolsResult> ListAsync(HttpContext http, CancellationToken ct)
    {
        var (caller, scoped) = await CallerAsync(http, ct);
        var tools = await http.RequestServices.GetRequiredService<IExternalToolGateway>().ToolsAsync(caller, scoped, ct);
        return new ListToolsResult
        {
            Tools = tools.Select(t => new Tool
            {
                Name = t.Name, Description = t.Description, InputSchema = t.Parameters,
                Annotations = new ToolAnnotations { ReadOnlyHint = t.SideEffect == ToolSideEffects.Read },
            }).ToList(),
        };
    }

    internal static async ValueTask<CallToolResult> CallAsync(HttpContext http, CallToolRequestParams? request, CancellationToken ct)
    {
        var (caller, scoped) = await CallerAsync(http, ct);
        var arguments = JsonSerializer.SerializeToElement(request?.Arguments ?? new Dictionary<string, JsonElement>());
        var gateway = http.RequestServices.GetRequiredService<IExternalToolGateway>();
        var outcome = await gateway.CallAsync(caller, scoped, request?.Name ?? string.Empty, arguments, ct);
        // The execution record and anything the tool wrote are kept, whatever the client does with the answer.
        await http.RequestServices.GetRequiredService<IDbSession>().CommitAsync(ct);
        var origin = OAuthServer.Origin(http.Request);
        var data = new
        {
            ok = outcome.Ok,
            error = outcome.ErrorKey,
            classification = outcome.Ok ? outcome.Classification : null,
            // Content is data from TeamBeats for the client to present, never instructions to it (FR-024).
            content = outcome.Ok ? Parse(outcome.Content) : null,
            sources = outcome.Sources.Select(s => new
            {
                source = s.Source, id = s.ItemId, title = s.Title, link = s.Link.StartsWith('/') ? origin + s.Link : s.Link, classification = s.Classification,
            }),
        };
        var structured = JsonSerializer.SerializeToElement(data, ToolArgs.Options);
        return new CallToolResult
        {
            IsError = !outcome.Ok,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
        };
    }

    private static JsonElement? Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(content);
        }
    }
}
