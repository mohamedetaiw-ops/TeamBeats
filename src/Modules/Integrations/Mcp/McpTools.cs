using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Mcp;

public static class McpToolStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Unsupported = "unsupported";
}

/// <summary>
/// A tool of an MCP server as the integration administrator sees it. <paramref name="Changed"/> is true when an approved
/// tool's description or schema changed and it waits to be approved again. <paramref name="Name"/> is the agent tool name.
/// </summary>
public sealed record McpToolView(Guid Id, Guid ConnectionId, string RemoteName, string Description, JsonElement InputSchema, bool ReadOnlyHint, string Status,
    bool Listed, bool Changed, string? Name, string? SideEffect, IReadOnlyDictionary<string, string>? Title, DateTimeOffset? DecidedAt, DateTimeOffset SeenAt);

public sealed record McpRefreshResult(int Listed, int New, int Changed, int Gone, IReadOnlyList<McpToolView> Tools);

public sealed record McpToolApproval(string? Name, string? SideEffect, IReadOnlyDictionary<string, string>? Title);

/// <summary>
/// The tools of the tenant's MCP servers (spec 027 R3, FR-006 and the edge case on changed tool lists). A refresh reads
/// the server's tools through the connector runtime. A new tool waits as pending, and a tool whose input schema does not
/// fit the tool schema subset is unsupported. Only the integration administrator's approval, with a name, a side effect
/// and titles, makes a tool usable. A changed description or schema sends it back to pending, and a tool the server stopped
/// listing is unavailable. The description reaches the model only as approved.
/// </summary>
public sealed partial class McpTools(IDbSession session, ITenantContext context, IEntitlementService entitlements, ConnectorTools connections, IConnectorRuntime runtime,
    IAuditWriter audit, IClock clock)
{
    public const string Prefix = "mcp.";
    public const int MaxPages = 10;
    public const int MaxPerServer = 200;

    [GeneratedRegex(@"^[a-z][a-z0-9_]{2,40}$")]
    private static partial Regex ToolName();

    [GeneratedRegex(@"^[a-z]{2}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex Locale();

    public async Task<IReadOnlyList<McpToolView>> ListAsync(Guid connectionId, CancellationToken ct)
    {
        await ServerAsync(connectionId, ct);
        return await RowsAsync(connectionId, ct);
    }

    /// <summary>Reads the server's tool list now. Unreachable servers leave the stored list as it was.</summary>
    public async Task<McpRefreshResult> RefreshAsync(Guid connectionId, CancellationToken ct)
    {
        await ServerAsync(connectionId, ct);
        var now = clock.UtcNow;
        var found = new List<JsonElement>();
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var input = JsonSerializer.SerializeToElement(cursor is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["cursor"] = cursor });
            var result = await runtime.InvokeAsync(new ConnectorCall(connectionId, McpServerPack.ListTools, input, $"mcp-list:{connectionId:N}:{now.UtcTicks}:{page}",
                "mcp-refresh"), ct);
            if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output is not { ValueKind: JsonValueKind.Object } output
                || !output.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            {
                throw AppException.Conflict("conflict.integrations.mcp_unreachable");
            }
            found.AddRange(tools.EnumerateArray().Select(t => t.Clone()));
            cursor = output.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (cursor is null || found.Count >= MaxPerServer)
            {
                break;
            }
        }

        var existing = (await RowsAsync(connectionId, ct)).ToDictionary(r => r.RemoteName, StringComparer.Ordinal);
        int added = 0, changed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in found.Take(MaxPerServer))
        {
            var remote = tool.GetProperty("name").GetString()!;
            if (!seen.Add(remote))
            {
                continue;
            }
            var description = tool.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty;
            var schema = tool.GetProperty("inputSchema");
            var readOnly = tool.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True;
            var hash = Hash(description, schema);
            var fits = Fits(schema);
            if (!existing.TryGetValue(remote, out var row))
            {
                added++;
                await session.ExecAsync(
                    """
                    insert into integrations.mcp_tool (tenant_id, id, connection_id, remote_name, description, input_schema, schema_hash, read_only_hint, status, seen_at)
                    values (platform.current_tenant_id(), @id, @connectionId, @remote, @description, @schema::jsonb, @hash, @readOnly, @status, @now)
                    """, new
                    {
                        id = Ids.New(), connectionId, remote, description, schema = schema.GetRawText(), hash, readOnly,
                        status = fits ? McpToolStatuses.Pending : McpToolStatuses.Unsupported, now,
                    }, ct);
                continue;
            }
            var same = await session.ScalarAsync<string>("select schema_hash from integrations.mcp_tool where id = @id", new { id = row.Id }, ct) == hash;
            if (!same)
            {
                changed++;
            }
            // A changed tool waits for a new approval; a rejected one stays rejected.
            var status = same ? row.Status
                : row.Status == McpToolStatuses.Rejected ? McpToolStatuses.Rejected
                : fits ? McpToolStatuses.Pending : McpToolStatuses.Unsupported;
            await session.ExecAsync(
                """
                update integrations.mcp_tool set description = @description, input_schema = @schema::jsonb, schema_hash = @hash, read_only_hint = @readOnly,
                  status = @status, listed = true, seen_at = @now
                where id = @id
                """, new { id = row.Id, description, schema = schema.GetRawText(), hash, readOnly, status, now }, ct);
        }
        var gone = await session.ExecAsync(
            "update integrations.mcp_tool set listed = false where connection_id = @connectionId and listed and seen_at < @now", new { connectionId, now }, ct);
        if (added + changed + gone > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ActionChanged, "connection", connectionId.ToString(),
                Changes: new { mcpTools = seen.Count, added, changed, gone }), ct);
        }
        return new McpRefreshResult(seen.Count, added, changed, gone, await RowsAsync(connectionId, ct));
    }

    public async Task<McpToolView> ApproveAsync(Guid connectionId, Guid toolId, McpToolApproval request, CancellationToken ct)
    {
        var by = context.RequireUser();
        await ServerAsync(connectionId, ct);
        var row = (await RowsAsync(connectionId, ct)).FirstOrDefault(r => r.Id == toolId) ?? throw AppException.NotFound();
        if (row.Status == McpToolStatuses.Unsupported || !row.Listed)
        {
            throw AppException.Conflict("conflict.integrations.mcp_tool_unusable");
        }
        var errors = new ValidationErrors();
        var name = request.Name?.Trim() ?? string.Empty;
        if (!ToolName().IsMatch(name))
        {
            errors.Add("name", "validation.connector_tool.name");
        }
        if (request.SideEffect is not (ToolSideEffects.Read or ToolSideEffects.WriteExternal))
        {
            errors.Add("sideEffect", "validation.integrations.mcp_side_effect");
        }
        var title = (request.Title ?? new Dictionary<string, string>())
            .Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToDictionary(t => t.Key, t => t.Value.Trim(), StringComparer.Ordinal);
        if (title.Count is 0 or > 10 || title.Any(t => !Locale().IsMatch(t.Key) || t.Value.Length > 100))
        {
            errors.Add("title", "validation.connector_tool.title");
        }
        if (row.Description.Length < 10)
        {
            errors.Add("description", "validation.integrations.mcp_description");
        }
        errors.ThrowIfAny();
        if (await session.ScalarAsync<long>("select count(*) from integrations.mcp_tool where name = @name and id <> @toolId", new { name, toolId }, ct) > 0)
        {
            throw AppException.Conflict("conflict.integrations.tool_exists");
        }
        await session.ExecAsync(
            """
            update integrations.mcp_tool set status = 'approved', name = @name, side_effect = @sideEffect, title = @title, approved_hash = schema_hash,
              decided_by = @by, decided_at = @now
            where id = @toolId
            """, new { toolId, name, sideEffect = request.SideEffect, title = Jsonb.From(title), by, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.IntegrationMcpToolApproved, "connection", connectionId.ToString(),
            Changes: new { tool = Prefix + name, remote = row.RemoteName, sideEffect = request.SideEffect }), ct);
        return (await RowsAsync(connectionId, ct)).Single(r => r.Id == toolId);
    }

    public async Task<McpToolView> RejectAsync(Guid connectionId, Guid toolId, CancellationToken ct)
    {
        var by = context.RequireUser();
        await ServerAsync(connectionId, ct);
        var row = (await RowsAsync(connectionId, ct)).FirstOrDefault(r => r.Id == toolId) ?? throw AppException.NotFound();
        await session.ExecAsync(
            "update integrations.mcp_tool set status = 'rejected', name = null, approved_hash = null, decided_by = @by, decided_at = @now where id = @toolId",
            new { toolId, by, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.IntegrationMcpToolRejected, "connection", connectionId.ToString(),
            Changes: new { tool = row.Name is null ? null : Prefix + row.Name, remote = row.RemoteName }), ct);
        return (await RowsAsync(connectionId, ct)).Single(r => r.Id == toolId);
    }

    /// <summary>The enabled MCP server connections of the tenant, for the hourly refresh.</summary>
    internal Task<List<Guid>> ServersAsync(CancellationToken ct) =>
        session.QueryAsync("select id from integrations.connection where kind = @kind and enabled", new { kind = McpServerPack.KindName }, r => r.GetGuid(0), ct);

    private async Task<ConnectorTools.ConnectionRow> ServerAsync(Guid connectionId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsMcp, ct);
        var connection = await connections.ConnectionAsync(connectionId, ct);
        return connection is { Kind: McpServerPack.KindName } ? connection : throw AppException.NotFound();
    }

    internal async Task<List<McpToolView>> RowsAsync(Guid? connectionId, CancellationToken ct) =>
        await session.QueryAsync(
            """
            select id, connection_id, remote_name, description, input_schema::text, read_only_hint, status, listed, approved_hash is not null and approved_hash <> schema_hash,
              name, side_effect, title::text, decided_at, seen_at
            from integrations.mcp_tool where @connectionId::uuid is null or connection_id = @connectionId order by remote_name
            """, new { connectionId },
            r => new McpToolView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), JsonDocument.Parse(r.GetString(4)).RootElement.Clone(), r.GetBoolean(5),
                r.GetString(6), r.GetBoolean(7), r.GetBoolean(8), r.Get<string?>("name"), r.Get<string?>("side_effect"),
                r.Get<string?>("title") is { } t ? JsonSerializer.Deserialize<Dictionary<string, string>>(t) : null, r.Get<DateTimeOffset?>("decided_at"),
                r.GetFieldValue<DateTimeOffset>(13)), ct);

    public static string Hash(string description, JsonElement schema) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(description + "\n" + JsonNode.Parse(schema.GetRawText())!.ToJsonString())));

    private static readonly HashSet<string> Keywords =
    [
        "type", "properties", "required", "additionalProperties", "enum", "minLength", "maxLength", "minimum", "maximum", "items", "maxItems",
        "description", "title", "default", "examples", "format", "pattern", "$schema",
    ];

    /// <summary>
    /// Whether the schema fits the tool schema subset of spec 027 (objects, strings, numbers, booleans, arrays and enums,
    /// with their limits). Annotations are allowed; composition, references and conditionals are not, since the gateway
    /// could not check them.
    /// </summary>
    public static bool Fits(JsonElement schema, int depth = 0)
    {
        if (depth > 8 || schema.ValueKind != JsonValueKind.Object || (depth == 0 && (!schema.TryGetProperty("type", out var root) || root.GetString() != "object")))
        {
            return false;
        }
        foreach (var keyword in schema.EnumerateObject())
        {
            if (!Keywords.Contains(keyword.Name))
            {
                return false;
            }
        }
        var type = schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (type is null && !schema.TryGetProperty("enum", out _))
        {
            return false;
        }
        if (type is not (null or "object" or "string" or "integer" or "number" or "boolean" or "array"))
        {
            return false;
        }
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object || properties.EnumerateObject().Any(p => !Fits(p.Value, depth + 1)))
            {
                return false;
            }
        }
        if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            return false;
        }
        return !schema.TryGetProperty("items", out var items) || Fits(items, depth + 1);
    }
}

/// <summary>The approved tools of the tenant's MCP servers for the agent tool registry, loaded once per scope.</summary>
public sealed class McpToolSource(McpTools tools, ConnectorTools connections, IConnectorRuntime runtime, IEntitlementService entitlements) : IAgentToolSource
{
    public async Task<IReadOnlyList<IAgentTool>> ToolsAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiAgentsMcp, ct))
        {
            return [];
        }
        var rows = (await tools.RowsAsync(null, ct)).Where(r => r.Name is not null).ToList();
        var list = new List<IAgentTool>();
        foreach (var group in rows.GroupBy(r => r.ConnectionId))
        {
            var connection = await connections.ConnectionAsync(group.Key, ct);
            foreach (var row in group)
            {
                // Agents that hold a tool which is not usable now are refused the call and show a degraded notice.
                var unavailable = connection is null || !connection.Enabled ? ToolUnavailable.ConnectorDisabled
                    : row.Status != McpToolStatuses.Approved || !row.Listed ? ToolUnavailable.NotApproved
                    : null;
                list.Add(new McpTool(row, runtime, entitlements, unavailable));
            }
        }
        return list;
    }
}

/// <summary>
/// One approved MCP tool as an agent tool. The tool gateway has checked the agent, the policy, the package, autonomy, the
/// confirmation and the arguments against the approved schema; the tool sends only the schema's fields to the server
/// through the connector runtime. The answer goes back to the model as untrusted data.
/// </summary>
public sealed class McpTool : IAgentTool
{
    private readonly Guid _connectionId;
    private readonly string _remote;
    private readonly IConnectorRuntime _runtime;
    private readonly IEntitlementService _entitlements;

    internal McpTool(McpToolView row, IConnectorRuntime runtime, IEntitlementService entitlements, string? unavailable)
    {
        _connectionId = row.ConnectionId;
        _remote = row.RemoteName;
        _runtime = runtime;
        _entitlements = entitlements;
        var effect = row.SideEffect ?? ToolSideEffects.WriteExternal;
        Definition = new ToolDefinition(McpTools.Prefix + row.Name, "integrations", 1, effect, ConnectorTools.DefaultRule(effect), Parameters(row.InputSchema), "internal",
            false, TimeoutSeconds: 30, row.Description, effect == ToolSideEffects.Read ? Capabilities.AiAgentsMcp : Capabilities.AiAgentsWriteTools, row.Title,
            Unavailable: unavailable);
    }

    public ToolDefinition Definition { get; }

    /// <summary>The approved schema with <c>additionalProperties: false</c> on the top object, so only its fields reach the server.</summary>
    private static JsonElement Parameters(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        node["additionalProperties"] = false;
        node.Remove("$schema");
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await _entitlements.IsEnabledAsync(Capabilities.AiAgentsMcp, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        var input = new JsonObject();
        var properties = Definition.Parameters.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        if (arguments.ValueKind == JsonValueKind.Object && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in arguments.EnumerateObject())
            {
                if (properties.TryGetProperty(field.Name, out _) && field.Value.ValueKind != JsonValueKind.Null)
                {
                    input[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                }
            }
        }
        var call = JsonSerializer.SerializeToElement(new { name = _remote, arguments = input.ToJsonString() });
        var write = ToolSideEffects.IsWrite(Definition.SideEffect);
        var key = write ? $"agent:{context.RunId:N}:{context.IdempotencyKey ?? Ids.New().ToString("N")}" : $"agent:{context.RunId:N}:{Ids.New():N}";
        if (key.Length > 200)
        {
            key = key[..200];
        }
        var result = await _runtime.InvokeAsync(new ConnectorCall(_connectionId, McpServerPack.CallTool, call, key, $"agent-run:{context.RunId:D}"), ct);
        if (result.Outcome == ConnectorOutcomes.Succeeded && result.Output is { ValueKind: JsonValueKind.Object } output)
        {
            var isError = output.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
            var text = output.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return new ToolResult(true, JsonSerializer.Serialize(new { outcome = isError ? "tool_error" : "done", text }, JsonDefaults.Options), "internal", []);
        }
        return result.Outcome switch
        {
            ConnectorOutcomes.BusinessError => new ToolResult(true,
                JsonSerializer.Serialize(new { outcome = "refused_by_system", code = result.ErrorCode }, JsonDefaults.Options), "internal", []),
            ConnectorOutcomes.Unknown => ToolResult.Failed("tool.outcome_unknown"),
            ConnectorOutcomes.InProgress => ToolResult.Failed("tool.in_progress"),
            ConnectorOutcomes.Refused when result.ErrorClass is ConnectorErrorClasses.ConnectionDisabled or ConnectorErrorClasses.ConnectionMissing
                or ConnectorErrorClasses.NotInPackage or ConnectorErrorClasses.UnknownAction => ToolResult.Failed("tool.connector_unavailable"),
            _ => ToolResult.Failed("tool.connector_failed"),
        };
    }
}

/// <summary>
/// Every hour, for every active tenant with MCP tools in its package: reads the tool list of each enabled MCP server, so
/// a changed tool stops being usable until it is approved again. Hosted by the worker, and by the development host when
/// <c>Integrations:Mcp:InProcess</c> is true.
/// </summary>
public sealed class McpToolRefreshWorker(IServiceScopeFactory scopes, ILogger<McpToolRefreshWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Integrations:Mcp:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MCP tool refresh could not list tenants");
            return;
        }
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                if (!await scope.ServiceProvider.GetRequiredService<IEntitlementService>().IsEnabledAsync(Capabilities.AiAgentsMcp, ct))
                {
                    continue;
                }
                var tools = scope.ServiceProvider.GetRequiredService<McpTools>();
                foreach (var server in await tools.ServersAsync(ct))
                {
                    try
                    {
                        await tools.RefreshAsync(server, ct);
                    }
                    catch (AppException)
                    {
                        // An unreachable server keeps its list until the next run.
                    }
                }
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "MCP tool refresh failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
