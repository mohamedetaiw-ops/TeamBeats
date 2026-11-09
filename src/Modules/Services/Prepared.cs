using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

/// <summary>A request an agent prepared for the person, as the form page loads it.</summary>
public sealed record PreparedRequestView(Guid Id, Guid ServiceId, IReadOnlyDictionary<string, JsonElement> Data, Guid AgentId, Guid RunId, string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// Requests prepared by agents (spec 020 T013, US8 scenario 1, FR-013). The <c>requests.prepare</c> tool keeps a draft for
/// the person; it appears in their task inbox marked "prepared by AI" and opens the service form filled in. Nothing is
/// submitted until the person submits the form, and they can discard it. Only the person who asked sees it.
/// </summary>
public sealed class PreparedRequests(IDbSession session, ITenantContext context, IEntitlementService entitlements, IClock clock) : IPersonalTaskSource
{
    public const string TaskSource = "agent_prepared";
    public const int MaxData = 20_000;

    public static string Link(Guid serviceId, Guid preparedId) => $"/services/{serviceId}?prepared={preparedId}";

    internal async Task<(Guid Id, bool Created)> SaveAsync(Guid user, Guid serviceId, Dictionary<string, JsonElement> data, ToolCallContext call, CancellationToken ct)
    {
        var key = $"{call.RunId:N}:{call.IdempotencyKey}";
        if (await session.ScalarAsync<Guid?>("select id from services.prepared_request where user_id = @user and idempotency_key = @key", new { user, key }, ct) is { } existing)
        {
            return (existing, false);
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into services.prepared_request (tenant_id, id, user_id, service_id, data, agent_id, run_id, idempotency_key, status, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @user, @serviceId, cast(@data as jsonb), @agent, @run, @key, 'prepared', @now, @now)
            """, new { id, user, serviceId, data = JsonSerializer.Serialize(data, ServiceToolJson.Options), agent = call.AgentId, run = call.RunId, key, now }, ct);
        return (id, true);
    }

    public async Task<PreparedRequestView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            "select id, service_id, data::text, agent_id, run_id, status, created_at from services.prepared_request where id = @id and user_id = @user",
            new { id, user },
            r => new PreparedRequestView(r.GetGuid(0), r.GetGuid(1), JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.GetString(2), ServiceToolJson.Options) ?? [],
                r.GetGuid(3), r.GetGuid(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)), ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }

    public async Task DiscardAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        var changed = await session.ExecAsync(
            "update services.prepared_request set status = 'discarded', updated_at = @now where id = @id and user_id = @user and status = 'prepared'",
            new { id, user, now = clock.UtcNow }, ct);
        if (changed == 0)
        {
            throw AppException.NotFound();
        }
    }

    /// <summary>Called after the person submits the form from a prepared request: the draft leaves the inbox.</summary>
    internal async Task SubmittedAsync(Guid id, Guid serviceId, Guid requestId, CancellationToken ct)
    {
        var user = context.RequireUser();
        await session.ExecAsync(
            """
            update services.prepared_request set status = 'submitted', request_id = @requestId, updated_at = @now
            where id = @id and user_id = @user and service_id = @serviceId and status = 'prepared'
            """, new { id, user, serviceId, requestId, now = clock.UtcNow }, ct);
    }

    public async Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.WorkflowCatalogue, ct))
        {
            return [];
        }
        return await session.QueryAsync(
            """
            select p.id, p.service_id, s.names::text, p.created_at from services.prepared_request p
            join services.service s on s.id = p.service_id
            where p.user_id = @userId and p.status = 'prepared' and s.status = 'published'
            order by p.created_at limit 50
            """, new { userId },
            r => new PersonalTask(TaskSource, r.GetGuid(0), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? [],
                Link(r.GetGuid(1), r.GetGuid(0)), r.GetFieldValue<DateTimeOffset>(3), null), ct);
    }
}

/// <summary>
/// Prepares a service request for the person (spec 020 US8): the draft goes to their task inbox and nothing is
/// submitted. Values are kept only for the fields of the service's form; the form checks them again when the person
/// submits. The test panel never stores a draft.
/// </summary>
public sealed class RequestPrepareTool(Catalogue catalogue, PreparedRequests prepared, IEntitlementService entitlements) : IAgentTool
{
    public const string Name = "requests.prepare";

    public ToolDefinition Definition { get; } = new(
        Name, "services", 1, ToolSideEffects.Draft, ToolApprovalRules.Execute,
        ServiceToolJson.Schema(
            """
            {"type":"object","additionalProperties":false,"required":["serviceId"],
             "properties":{"serviceId":{"type":"string","minLength":36,"maxLength":36},
                           "fields":{"type":"array","maxItems":50,"items":{"type":"object","additionalProperties":false,"required":["key","value"],
                             "properties":{"key":{"type":"string","minLength":1,"maxLength":60},"value":{"type":"string","maxLength":2000}}}}}}
            """),
        "internal", Idempotent: true, TimeoutSeconds: 10,
        "Prepare a request for a form service from the catalogue for the employee to check and submit later. Give the service ID and the form fields you know as key and value pairs (dates as YYYY-MM-DD, several choices separated by commas). Nothing is submitted.",
        Capabilities.WorkflowCatalogue);

    /// <summary>
    /// Turns the model's key and value pairs into form values: only fields of the form, as the field's type expects.
    /// Fields the person fills themselves (attachments, amounts, ranges, notes) and values that do not parse are left out.
    /// </summary>
    public static Dictionary<string, JsonElement> Values(FormDefinition form, JsonElement arguments)
    {
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!arguments.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return data;
        }
        var byKey = form.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        foreach (var item in fields.EnumerateArray())
        {
            var key = item.GetProperty("key").GetString()!;
            var text = item.GetProperty("value").GetString()!.Trim();
            if (!byKey.TryGetValue(key, out var field) || text.Length == 0)
            {
                continue;
            }
            object? value = field.Type switch
            {
                FieldTypes.Number => double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null,
                FieldTypes.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", out _) ? text : null,
                FieldTypes.MultiChoice => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                FieldTypes.Text or FieldTypes.Choice or FieldTypes.Lookup or FieldTypes.Person or FieldTypes.OrgUnit => text,
                _ => null,
            };
            if (value is not null)
            {
                data[key] = JsonSerializer.SerializeToElement(value, ServiceToolJson.Options);
            }
        }
        return data;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.WorkflowCatalogue, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        if (!Guid.TryParse(arguments.TryGetProperty("serviceId", out var s) ? s.GetString() : null, out var serviceId))
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        ServiceDetail detail;
        try
        {
            detail = await catalogue.DetailAsync(serviceId, ct);
        }
        catch (AppException)
        {
            return ToolResult.Failed("tool.not_found");
        }
        if (detail.Service.Kind != ServiceKinds.Form || detail.Version is null)
        {
            return ToolResult.Failed("tool.not_found");
        }
        var data = Values(detail.Form, arguments);
        if (JsonSerializer.Serialize(data, ServiceToolJson.Options).Length > PreparedRequests.MaxData)
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var name = detail.Service.Names.GetValueOrDefault("en") ?? detail.Service.Names.Values.FirstOrDefault() ?? string.Empty;
        if (context.DryRun)
        {
            return new ToolResult(true, JsonSerializer.Serialize(new { prepared = false, test = true, service = name, fields = data.Keys }, ServiceToolJson.Options), "internal", []);
        }
        var (id, _) = await prepared.SaveAsync(context.UserId, serviceId, data, context, ct);
        var link = PreparedRequests.Link(serviceId, id);
        return new ToolResult(true, JsonSerializer.Serialize(new { prepared = true, submitted = false, service = name, fields = data.Keys, inInbox = true }, ServiceToolJson.Options),
            "internal", [new ToolSource("prepared_request", id, name, link, "internal")]);
    }
}
