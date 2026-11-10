using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Ticketing;

/// <summary>
/// Settings of a ticketing connection: the instance or site address (https, on the tenant allowlist) and how it signs
/// in, with a secret manager reference. Jira Service Management also names the service desk and request type that new
/// requests go to.
/// </summary>
public sealed record TicketingSettings(string BaseUrl, RestAuth Auth, string? ServiceDeskId = null, string? RequestTypeId = null)
{
    public RestSettings Rest => new(BaseUrl, Auth);
}

/// <summary>
/// What the ticketing packs share (spec 017 section 12 default: ServiceNow and Jira Service Management get first-party
/// packs in R2, other systems use custom REST). Each pack has fixed actions with declared inputs, sends only those
/// inputs, and returns canonical output (number or key, status); no external DTO leaves the pack (FR-001).
/// </summary>
public abstract partial class TicketingPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : IConnectorPack
{
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";

    public abstract string Kind { get; }

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationConnectors;

    public abstract IReadOnlyList<ConnectorActionDefinition> Actions { get; }

    /// <summary>The sign-in methods this system accepts.</summary>
    protected abstract IReadOnlyCollection<string> AuthTypes { get; }

    protected RestSignIn SignIn => signIn;

    protected abstract Task<PackCallResult> CallAsync(PackConnection connection, TicketingSettings settings, string action, JsonElement input, string idempotencyKey,
        CancellationToken ct);

    /// <summary>A cheap read that proves the address and credential work, for Test connection.</summary>
    protected abstract string ProbePath(TicketingSettings settings);

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (Actions.FirstOrDefault(a => a.Name == call.Action) is not { } action)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        if (Parse(call.Connection.Settings) is not { } settings)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        if (RestDefinitions.CheckInput(action.Inputs ?? [], call.Input) is not null)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
        }
        return await CallAsync(call.Connection, settings, call.Action, call.Input, call.IdempotencyKey, ct);
    }

    /// <summary>
    /// One HTTP exchange with the system. Returns the failure the status means, or the JSON answer. An answer that is
    /// not JSON or over 1 MB is an invalid response that may have acted when <paramref name="writes"/> is true.
    /// </summary>
    protected async Task<(PackCallResult? Failure, JsonElement Body)> SendAsync(PackConnection connection, TicketingSettings settings, HttpMethod method,
        string pathAndQuery, object? body, IReadOnlyDictionary<int, string>? business, bool writes, CancellationToken ct)
    {
        var baseUri = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        var uri = new Uri(settings.BaseUrl.TrimEnd('/') + pathAndQuery);
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || !await signIn.IsAllowedAsync(baseUri, ct))
        {
            return (PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed), default);
        }
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json");
        }
        if (await signIn.AuthorizeAsync(connection, settings.Rest, request, ct) is { } refused)
        {
            return (refused, default);
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (signIn.MapStatus(connection, settings.Rest, response, business) is { } failure)
        {
            return (failure, default);
        }
        return await RestSignIn.ReadJsonAsync(response, ct) is { ValueKind: JsonValueKind.Object } json
            ? (null, json)
            : (PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: writes), default);
    }

    protected static string? Text(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    protected static string? Text(JsonElement? parent, params string[] path)
    {
        if (parent is not { } e)
        {
            return null;
        }
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e))
            {
                return null;
            }
        }
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => null,
        };
    }

    protected static string? Cut(string? value, int max = 200) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;

    protected static PackCallResult Done(string reference, object output) =>
        PackCallResult.Ok(JsonSerializer.SerializeToElement(output, JsonDefaults.Options), Cut(reference));

    public static TicketingSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object && settings.Deserialize<TicketingSettings>(JsonDefaults.Options) is { BaseUrl: not null, Auth: not null } s
                ? s
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DateOnly? CredentialExpiry(JsonElement settings) => RestSignIn.ExpiresOn(Parse(settings)?.Auth);

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.custom_rest.settings");
        var errors = new ValidationErrors();
        var rest = await signIn.ValidateAsync(tenantId, s.BaseUrl, s.Auth, AuthTypes, errors, ct);
        var stored = Extra(s, errors);
        errors.ThrowIfAny();
        return JsonSerializer.SerializeToElement(stored with { BaseUrl = rest!.BaseUrl, Auth = rest.Auth }, JsonDefaults.Options);
    }

    /// <summary>Checks the settings beyond address and sign-in, and returns them with only the fields this kind keeps.</summary>
    protected virtual TicketingSettings Extra(TicketingSettings s, ValidationErrors errors) => s with { ServiceDeskId = null, RequestTypeId = null };

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { } settings)
        {
            return [new PackCheck("settings", false, "integrations.test.settings_invalid", ConnectorErrorClasses.RequestRefused)];
        }
        var checks = await signIn.TestAsync(connection, settings.Rest, ct);
        if (checks.Any(c => !c.Ok))
        {
            return checks;
        }
        PackCheck check;
        try
        {
            var (failure, _) = await SendAsync(connection, settings, HttpMethod.Get, ProbePath(settings), null, null, false, ct);
            check = failure is null
                ? new PackCheck("system", true, "integrations.test.system_ok")
                : new PackCheck("system", false, failure.ErrorClass == ConnectorErrorClasses.CredentialRefused ? "integrations.test.system_refused" : "integrations.test.system_failed",
                    failure.ErrorClass);
        }
        catch (HttpRequestException)
        {
            check = new PackCheck("system", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            check = new PackCheck("system", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
        }
        checks.Add(check);
        return checks;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    protected static partial Regex RecordNumber();

    [GeneratedRegex("^[0-9]{1,20}$")]
    protected static partial Regex NumericId();
}

/// <summary>
/// ServiceNow (spec 017 US1, US2): <c>create_incident</c> and <c>get_incident_status</c> over the Table API. Each new
/// incident carries a correlation ID derived from the idempotency key, and the pack looks for it before creating, so a
/// retry after a timeout finds the incident ServiceNow already made instead of making a second one (US2 scenario 2).
/// </summary>
public sealed class ServiceNowPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : TicketingPack(context, signIn, httpClients)
{
    public const string KindName = "servicenow";
    private const string Fields = "sysparm_fields=number,sys_id,state&sysparm_limit=1";

    public override string Kind => KindName;

    protected override IReadOnlyCollection<string> AuthTypes => ["basic", "oauth_client_credentials"];

    public override IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new("create_incident", ConnectorSideEffects.Write, true, [InvalidRequest],
        [
            new("short_description", "string", true),
            new("description", "string", false),
            new("caller", "string", false),
            new("category", "string", false),
            new("urgency", "integer", false),
        ]),
        new("get_incident_status", ConnectorSideEffects.Read, true, [NotFound], [new("number", "string", true)]),
    ];

    protected override string ProbePath(TicketingSettings settings) => "/api/now/table/incident?sysparm_fields=number&sysparm_limit=1";

    /// <summary>The correlation ID stored on the incident: a hash, so the key's own characters never reach a ServiceNow query.</summary>
    public static string CorrelationId(string idempotencyKey) =>
        "tb-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))[..40];

    protected override async Task<PackCallResult> CallAsync(PackConnection connection, TicketingSettings settings, string action, JsonElement input, string idempotencyKey,
        CancellationToken ct)
    {
        if (action == "get_incident_status")
        {
            var number = Text(input, "number")!;
            if (!RecordNumber().IsMatch(number))
            {
                return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
            }
            var (failure, body) = await SendAsync(connection, settings, HttpMethod.Get,
                $"/api/now/table/incident?sysparm_query={Uri.EscapeDataString("number=" + number)}&{Fields}", null, null, false, ct);
            return failure ?? (First(body) is { } found ? Incident(found) : PackCallResult.Business(NotFound));
        }

        var correlation = CorrelationId(idempotencyKey);
        var (lookupFailure, existing) = await SendAsync(connection, settings, HttpMethod.Get,
            $"/api/now/table/incident?sysparm_query={Uri.EscapeDataString("correlation_id=" + correlation)}&{Fields}", null, null, false, ct);
        if (lookupFailure is not null)
        {
            return lookupFailure;
        }
        if (First(existing) is { } earlier)
        {
            return Incident(earlier);
        }
        var fields = new Dictionary<string, string?>
        {
            ["short_description"] = Text(input, "short_description"),
            ["description"] = Text(input, "description"),
            ["caller_id"] = Text(input, "caller"),
            ["category"] = Text(input, "category"),
            ["urgency"] = input.TryGetProperty("urgency", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetRawText() : null,
            ["correlation_id"] = correlation,
            ["correlation_display"] = "TeamBeats",
        };
        var (createFailure, created) = await SendAsync(connection, settings, HttpMethod.Post, "/api/now/table/incident?sysparm_fields=number,sys_id,state",
            fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value), new Dictionary<int, string> { [400] = InvalidRequest }, true, ct);
        if (createFailure is not null)
        {
            return createFailure;
        }
        return created.TryGetProperty("result", out var result) && Text(result, "number") is not null
            ? Incident(result)
            : PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true);
    }

    private static JsonElement? First(JsonElement body) =>
        body.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0 ? r[0] : null;

    private static PackCallResult Incident(JsonElement incident)
    {
        var number = Text(incident, "number")!;
        return Done(number, new { number, sysId = Cut(Text(incident, "sys_id"), 64), state = Cut(Text(incident, "state"), 64) });
    }
}

/// <summary>
/// Jira Service Management (spec 017 section 12 default): <c>create_request</c> raises a customer request in the
/// configured service desk and request type, and <c>get_request_status</c> reads its status. The service desk API takes
/// no idempotency key, so <c>create_request</c> is not idempotent: a timeout after sending stops with "outcome unknown"
/// for the operator instead of risking a second request (US2 scenario 2).
/// </summary>
public sealed class JiraServiceManagementPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : TicketingPack(context, signIn, httpClients)
{
    public const string KindName = "jira_service_management";

    public override string Kind => KindName;

    protected override IReadOnlyCollection<string> AuthTypes => ["basic", "bearer"];

    public override IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new("create_request", ConnectorSideEffects.Write, false, [InvalidRequest],
        [
            new("summary", "string", true),
            new("description", "string", false),
            new("on_behalf_of", "string", false),
        ]),
        new("get_request_status", ConnectorSideEffects.Read, true, [NotFound], [new("issue_key", "string", true)]),
    ];

    protected override string ProbePath(TicketingSettings settings) => $"/rest/servicedeskapi/servicedesk/{settings.ServiceDeskId}";

    protected override TicketingSettings Extra(TicketingSettings s, ValidationErrors errors)
    {
        if (s.ServiceDeskId is null || !NumericId().IsMatch(s.ServiceDeskId))
        {
            errors.Add("serviceDeskId", "validation.ticketing.service_desk_id");
        }
        if (s.RequestTypeId is null || !NumericId().IsMatch(s.RequestTypeId))
        {
            errors.Add("requestTypeId", "validation.ticketing.request_type_id");
        }
        return s;
    }

    protected override async Task<PackCallResult> CallAsync(PackConnection connection, TicketingSettings settings, string action, JsonElement input, string idempotencyKey,
        CancellationToken ct)
    {
        if (action == "get_request_status")
        {
            var key = Text(input, "issue_key")!;
            if (!RecordNumber().IsMatch(key))
            {
                return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
            }
            var (failure, body) = await SendAsync(connection, settings, HttpMethod.Get, $"/rest/servicedeskapi/request/{Uri.EscapeDataString(key)}", null,
                new Dictionary<int, string> { [404] = NotFound }, false, ct);
            return failure ?? Request(body);
        }

        var values = new Dictionary<string, string?> { ["summary"] = Text(input, "summary"), ["description"] = Text(input, "description") };
        var request = new Dictionary<string, object?>
        {
            ["serviceDeskId"] = settings.ServiceDeskId,
            ["requestTypeId"] = settings.RequestTypeId,
            ["requestFieldValues"] = values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value),
        };
        if (Text(input, "on_behalf_of") is { } person)
        {
            request["raiseOnBehalfOf"] = person;
        }
        var (createFailure, created) = await SendAsync(connection, settings, HttpMethod.Post, "/rest/servicedeskapi/request", request,
            new Dictionary<int, string> { [400] = InvalidRequest }, true, ct);
        return createFailure ?? Request(created, writes: true);
    }

    private static PackCallResult Request(JsonElement body, bool writes = false)
    {
        if (Text(body, "issueKey") is not { } key)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: writes);
        }
        return Done(key, new { issueKey = Cut(key, 64), issueId = Cut(Text(body, "issueId"), 64), status = Cut(Text(body, "currentStatus", "status"), 100) });
    }
}
