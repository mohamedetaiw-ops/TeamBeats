using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

public sealed record ConnectorToolRequest(string? Action, string? Name, string? Description, IReadOnlyDictionary<string, string>? Title);

/// <summary>
/// A connector action registered as an agent tool. <paramref name="Name"/> is the tool name agents hold, such as
/// <c>connector.parking_permit</c>; <paramref name="SideEffect"/> is the tool's side effect in spec 027 terms.
/// </summary>
public sealed record ConnectorToolView(Guid Id, Guid ConnectionId, string Action, string Name, string SideEffect, string ApprovalRule, string Description,
    IReadOnlyDictionary<string, string> Title, DateTimeOffset CreatedAt);

/// <summary>
/// Connector actions as agent tools (spec 017 T009, spec 027 FR-006, R2). The integration administrator registers an
/// action of a connection as a tool with a description for the model and a title per language for people; the AI policy
/// then decides which agents may hold it, as for any tool. Only actions that declare their inputs can be registered,
/// because the inputs are the tool's parameters and nothing else is ever sent. Writes are external writes for spec 027:
/// they need the write tools capability, confirmation at least, and the L3 autonomy that R2 adds.
/// </summary>
public sealed partial class ConnectorTools(IDbSession session, ITenantContext context, IEnumerable<IConnectorPack> packs, IAuditWriter audit)
{
    public const string Prefix = "connector.";
    public const int MaxPerTenant = 200;

    [GeneratedRegex(@"^[a-z][a-z0-9_]{2,40}$")]
    private static partial Regex ToolName();

    [GeneratedRegex(@"^[a-z]{2}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex Locale();

    public static string ToolSideEffect(string connectorSideEffect) =>
        connectorSideEffect == ConnectorSideEffects.Read ? ToolSideEffects.Read : ToolSideEffects.WriteExternal;

    /// <summary>The platform baseline of spec 027 US4: reads run, anything that changes another system waits for the person's confirmation.</summary>
    public static string DefaultRule(string toolSideEffect) => toolSideEffect == ToolSideEffects.Read ? ToolApprovalRules.Execute : ToolApprovalRules.Confirm;

    public async Task<IReadOnlyList<ConnectorToolView>> ListAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await ConnectionAsync(connectionId, ct) ?? throw AppException.NotFound();
        var actions = await ActionsAsync(connection, ct);
        return (await RowsAsync(connectionId, ct)).Select(r => View(r, actions.FirstOrDefault(a => a.Name == r.Action))).ToList();
    }

    public async Task<ConnectorToolView> RegisterAsync(Guid connectionId, ConnectorToolRequest request, CancellationToken ct)
    {
        var by = context.RequireUser();
        var connection = await ConnectionAsync(connectionId, ct) ?? throw AppException.NotFound();
        var errors = new ValidationErrors();
        var action = (await ActionsAsync(connection, ct)).FirstOrDefault(a => a.Name == request.Action);
        if (action is null)
        {
            errors.Add("action", "validation.connector_tool.action");
        }
        else if (action.Inputs is null)
        {
            errors.Add("action", "validation.connector_tool.no_inputs");
        }
        var name = request.Name?.Trim() ?? string.Empty;
        if (!ToolName().IsMatch(name))
        {
            errors.Add("name", "validation.connector_tool.name");
        }
        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length is < 10 or > 500)
        {
            errors.Add("description", "validation.connector_tool.description");
        }
        var title = (request.Title ?? new Dictionary<string, string>())
            .Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToDictionary(t => t.Key, t => t.Value.Trim(), StringComparer.Ordinal);
        if (title.Count is 0 or > 10 || title.Any(t => !Locale().IsMatch(t.Key) || t.Value.Length > 100))
        {
            errors.Add("title", "validation.connector_tool.title");
        }
        errors.ThrowIfAny();
        if (await session.ScalarAsync<long>("select count(*) from integrations.connector_tool", null, ct) >= MaxPerTenant)
        {
            throw AppException.Conflict("conflict.integrations.tool_limit");
        }
        var id = Ids.New();
        var inserted = await session.ExecAsync(
            """
            insert into integrations.connector_tool (tenant_id, id, connection_id, action, name, description, title, created_by)
            values (platform.current_tenant_id(), @id, @connectionId, @action, @name, @description, @title, @by)
            on conflict do nothing
            """, new { id, connectionId, action = action!.Name, name, description, title = Jsonb.From(title), by }, ct);
        if (inserted == 0)
        {
            throw AppException.Conflict("conflict.integrations.tool_exists");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ActionChanged, "connection", connectionId.ToString(),
            Changes: new { action = action.Name, tool = Prefix + name, sideEffect = action.SideEffect, registered = true }), ct);
        return View(new Row(id, connectionId, action.Name, name, description, title, DateTimeOffset.UtcNow), action);
    }

    public async Task DeleteAsync(Guid connectionId, Guid toolId, CancellationToken ct)
    {
        context.RequireUser();
        var row = (await RowsAsync(connectionId, ct)).FirstOrDefault(r => r.Id == toolId) ?? throw AppException.NotFound();
        await session.ExecAsync("delete from integrations.connector_tool where id = @toolId", new { toolId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ActionChanged, "connection", connectionId.ToString(),
            Changes: new { action = row.Action, tool = Prefix + row.Name, registered = false }), ct);
    }

    internal sealed record Row(Guid Id, Guid ConnectionId, string Action, string Name, string Description, IReadOnlyDictionary<string, string> Title,
        DateTimeOffset CreatedAt);

    internal sealed record ConnectionRow(Guid Id, string Kind, int PackVersion, string? ExternalId, string Settings);

    internal async Task<List<Row>> RowsAsync(Guid? connectionId, CancellationToken ct) =>
        await session.QueryAsync(
            """
            select id, connection_id, action, name, description, title::text, created_at from integrations.connector_tool
            where @connectionId::uuid is null or connection_id = @connectionId order by name
            """, new { connectionId },
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(5)) ?? [], r.GetFieldValue<DateTimeOffset>(6)), ct);

    internal Task<ConnectionRow?> ConnectionAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            "select id, kind, pack_version, external_tenant_id, settings::text from integrations.connection where id = @id and kind <> all(@hidden)",
            new { id, hidden = Webhooks.HiddenKinds.All },
            r => new ConnectionRow(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.Get<string>("external_tenant_id"), r.GetString(4)), ct);

    internal async Task<IReadOnlyList<ConnectorActionDefinition>> ActionsAsync(ConnectionRow c, CancellationToken ct) =>
        packs.FirstOrDefault(p => p.Kind == c.Kind) is { } pack
            ? await pack.ActionsForAsync(new PackConnection(context.RequireTenant(), c.Id, c.Kind, c.PackVersion, c.ExternalId, JsonDocument.Parse(c.Settings).RootElement), ct)
            : [];

    private static ConnectorToolView View(Row r, ConnectorActionDefinition? action)
    {
        var effect = ToolSideEffect(action?.SideEffect ?? ConnectorSideEffects.Write);
        return new ConnectorToolView(r.Id, r.ConnectionId, r.Action, Prefix + r.Name, effect, DefaultRule(effect), r.Description, r.Title, r.CreatedAt);
    }

    /// <summary>The JSON schema of the tool's parameters: exactly the action's declared inputs, nothing more.</summary>
    public static JsonElement Schema(IReadOnlyList<ConnectorInput>? inputs)
    {
        var properties = new JsonObject();
        foreach (var i in inputs ?? [])
        {
            var p = new JsonObject { ["type"] = i.Type };
            if (i.Type == "string")
            {
                p["maxLength"] = 4000;
            }
            properties[i.Name] = p;
        }
        var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties };
        var required = (inputs ?? []).Where(i => i.Required).Select(i => (JsonNode)JsonValue.Create(i.Name)!).ToArray();
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required);
        }
        using var doc = JsonDocument.Parse(schema.ToJsonString());
        return doc.RootElement.Clone();
    }
}

/// <summary>The tenant's connector tools for the agent tool registry, loaded once per scope.</summary>
public sealed class ConnectorToolSource(ConnectorTools tools, IConnectorRuntime runtime, IEntitlementService entitlements) : IAgentToolSource
{
    public async Task<IReadOnlyList<IAgentTool>> ToolsAsync(CancellationToken ct)
    {
        var rows = await tools.RowsAsync(null, ct);
        if (rows.Count == 0)
        {
            return [];
        }
        var list = new List<IAgentTool>();
        foreach (var group in rows.GroupBy(r => r.ConnectionId))
        {
            var connection = await tools.ConnectionAsync(group.Key, ct);
            var actions = connection is null ? [] : await tools.ActionsAsync(connection, ct);
            foreach (var row in group)
            {
                list.Add(new ConnectorActionTool(row, actions.FirstOrDefault(a => a.Name == row.Action), runtime, entitlements));
            }
        }
        return list;
    }
}

/// <summary>
/// One connector action as an agent tool. The tool gateway of spec 027 has already checked the agent, the policy, the
/// package, the autonomy level, the person's confirmation and the parameters against the schema; the tool sends only the
/// declared inputs through the connector runtime, which checks the connection, the package and the fields again. The
/// answer goes back to the model as untrusted data. A repeated write with the same parameters in a run is never sent twice.
/// </summary>
public sealed class ConnectorActionTool : IAgentTool
{
    private readonly Guid _connectionId;
    private readonly string _action;
    private readonly IReadOnlyList<ConnectorInput> _inputs;
    private readonly IConnectorRuntime _runtime;
    private readonly IEntitlementService _entitlements;

    internal ConnectorActionTool(ConnectorTools.Row row, ConnectorActionDefinition? action, IConnectorRuntime runtime, IEntitlementService entitlements)
    {
        _connectionId = row.ConnectionId;
        _action = row.Action;
        _inputs = action?.Inputs ?? [];
        _runtime = runtime;
        _entitlements = entitlements;
        var effect = ConnectorTools.ToolSideEffect(action?.SideEffect ?? ConnectorSideEffects.Write);
        Definition = new ToolDefinition(
            ConnectorTools.Prefix + row.Name, "integrations", 1, effect, ConnectorTools.DefaultRule(effect), ConnectorTools.Schema(_inputs), "internal",
            action?.Idempotent ?? false, TimeoutSeconds: 15, row.Description,
            effect == ToolSideEffects.Read ? Capabilities.IntegrationConnectors : Capabilities.AiAgentsWriteTools, row.Title);
    }

    public ToolDefinition Definition { get; }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await _entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        // Only the declared inputs are passed on, whatever else the arguments hold.
        var input = new JsonObject();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var i in _inputs)
            {
                if (arguments.TryGetProperty(i.Name, out var v) && v.ValueKind != JsonValueKind.Null)
                {
                    input[i.Name] = JsonNode.Parse(v.GetRawText());
                }
            }
        }
        using var doc = JsonDocument.Parse(input.ToJsonString());
        var write = ToolSideEffects.IsWrite(Definition.SideEffect);
        // A read may run again for fresh data; a write keeps one key per run and parameters, so a retry never acts twice.
        var key = write ? $"agent:{context.RunId:N}:{context.IdempotencyKey ?? Ids.New().ToString("N")}" : $"agent:{context.RunId:N}:{Ids.New():N}";
        if (key.Length > 200)
        {
            key = key[..200];
        }
        var result = await _runtime.InvokeAsync(new ConnectorCall(_connectionId, _action, doc.RootElement.Clone(), key, $"agent-run:{context.RunId:D}"), ct);
        return result.Outcome switch
        {
            ConnectorOutcomes.Succeeded => new ToolResult(true,
                JsonSerializer.Serialize(new { outcome = "done", reference = result.ExternalReference, output = result.Output }, JsonDefaults.Options), "internal", []),
            ConnectorOutcomes.BusinessError => new ToolResult(true,
                JsonSerializer.Serialize(new { outcome = "refused_by_system", code = result.ErrorCode }, JsonDefaults.Options), "internal", []),
            ConnectorOutcomes.Unknown => ToolResult.Failed("tool.outcome_unknown"),
            ConnectorOutcomes.InProgress => ToolResult.Failed("tool.in_progress"),
            ConnectorOutcomes.Refused when result.ErrorClass is ConnectorErrorClasses.ConnectionDisabled or ConnectorErrorClasses.ConnectionMissing
                or ConnectorErrorClasses.NotInPackage or ConnectorErrorClasses.UnknownAction => ToolResult.Failed("tool.connector_unavailable"),
            ConnectorOutcomes.Refused when result.ErrorClass == ConnectorErrorClasses.InvalidInput => ToolResult.Failed("tool.invalid_arguments"),
            _ => ToolResult.Failed("tool.connector_failed"),
        };
    }
}

/// <summary>The tenant's connector actions for route steps (spec 017 T009 for specs 016 and 024).</summary>
public sealed class ConnectorActionCatalogue(IDbSession session, ConnectorTools tools) : IConnectorActions
{
    public async Task<IReadOnlyList<ConnectorActionInfo>> ListAsync(CancellationToken ct = default)
    {
        var connections = await session.QueryAsync(
            "select id, display_name, enabled from integrations.connection where kind <> all(@hidden) order by display_name, id",
            new { hidden = Webhooks.HiddenKinds.All }, r => (Id: r.GetGuid(0), Name: r.GetString(1), Enabled: r.GetBoolean(2)), ct);
        var list = new List<ConnectorActionInfo>();
        foreach (var c in connections)
        {
            if (await tools.ConnectionAsync(c.Id, ct) is not { } row)
            {
                continue;
            }
            foreach (var a in await tools.ActionsAsync(row, ct))
            {
                list.Add(new ConnectorActionInfo(c.Id, c.Name, row.Kind, a.Name, a.SideEffect, a.Idempotent, a.BusinessErrors, a.Inputs, c.Enabled));
            }
        }
        return list;
    }

    public async Task<ConnectorActionInfo?> FindAsync(Guid connectionId, string action, CancellationToken ct = default)
    {
        var c = await session.QuerySingleOrDefaultAsync(
            "select display_name, enabled from integrations.connection where id = @connectionId and kind <> all(@hidden)",
            new { connectionId, hidden = Webhooks.HiddenKinds.All }, r => (Name: r.GetString(0), Enabled: r.GetBoolean(1)), ct);
        if (c.Name is null || await tools.ConnectionAsync(connectionId, ct) is not { } row)
        {
            return null;
        }
        var a = (await tools.ActionsAsync(row, ct)).FirstOrDefault(x => x.Name == action);
        return a is null ? null : new ConnectorActionInfo(connectionId, c.Name, row.Kind, a.Name, a.SideEffect, a.Idempotent, a.BusinessErrors, a.Inputs, c.Enabled);
    }
}
