using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.CustomRest;

/// <summary>
/// How a custom REST connection signs in (spec 017 FR-003): <c>none</c>, <c>api_key</c> (a header), <c>bearer</c>,
/// <c>basic</c> or <c>oauth_client_credentials</c>. <paramref name="SecretRef"/> is a secret manager reference of this
/// tenant; the value never reaches TeamBeats configuration. <paramref name="ExpiresOn"/> is the credential's expiry date.
/// </summary>
public sealed record RestAuth(string Type, string? Header = null, string? Username = null, string? SecretRef = null, string? TokenUrl = null,
    string? ClientId = null, string? Scope = null, string? ExpiresOn = null);

/// <summary>Settings of a custom REST connection: an https base URL on the tenant allowlist, and how it signs in.</summary>
public sealed record RestSettings(string BaseUrl, RestAuth Auth);

/// <summary>An action run with the HTTP status the system answered, for the action test in the builder.</summary>
public sealed record RestExecution(PackCallResult Result, int? Status);

/// <summary>OAuth client credential tokens per connection, kept until a minute before they expire.</summary>
public sealed class RestTokenCache
{
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> tokens = new(StringComparer.Ordinal);

    public string? Get(string key, DateTimeOffset now) => tokens.TryGetValue(key, out var t) && t.Until > now ? t.Token : null;

    public void Set(string key, string token, DateTimeOffset until) => tokens[key] = (token, until);

    public void Drop(string key) => tokens.TryRemove(key, out _);
}

/// <summary>
/// The custom REST connector (spec 017 US4, FR-011, FR-012). Each connection holds its own actions, defined without
/// code. Every call checks the base URL against the tenant allowlist, goes through the connector HTTP handler that
/// refuses internal addresses, sends only the declared inputs, reads at most 1 MB of answer, and maps it to the
/// declared output fields. 401 and 403 are a refused credential; a declared 4xx is a business error; 429 and 5xx are
/// retried by the runtime when safe.
/// </summary>
public sealed class CustomRestPack(
    IDbSession session,
    ITenantContext context,
    RestSignIn signIn,
    IHttpClientFactory httpClients) : IConnectorPack
{
    public const string KindName = "custom_rest";

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationCustomRest;

    public IReadOnlyList<ConnectorActionDefinition> Actions => [];

    public async Task<IReadOnlyList<ConnectorActionDefinition>> ActionsForAsync(PackConnection connection, CancellationToken ct) =>
        (await LoadActionsAsync(connection.Id, ct)).Select(a => ToRuntime(a.Name, a.Definition)).ToList();

    public static ConnectorActionDefinition ToRuntime(string name, RestActionDefinition d) =>
        new(name, d.SideEffect, d.IdempotencyHeader is not null || d.SideEffect == ConnectorSideEffects.Read, (d.BusinessErrors ?? []).Select(b => b.Code).ToList(),
            (d.Inputs ?? []).Select(i => new ConnectorInput(i.Name, i.Type, i.Required)).ToList());

    public async Task<List<(string Name, RestActionDefinition Definition)>> LoadActionsAsync(Guid connectionId, CancellationToken ct) =>
        await session.QueryAsync("select name, definition::text from integrations.connector_action where connection_id = @connectionId order by name",
            new { connectionId }, r => (r.GetString(0), JsonSerializer.Deserialize<RestActionDefinition>(r.GetString(1), JsonDefaults.Options)!), ct);

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        var definition = await session.QuerySingleOrDefaultAsync(
            "select definition::text from integrations.connector_action where connection_id = @c and name = @n",
            new { c = call.Connection.Id, n = call.Action }, r => r.GetString(0), ct);
        if (definition is null)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        var d = JsonSerializer.Deserialize<RestActionDefinition>(definition, JsonDefaults.Options)!;
        return (await ExecuteAsync(call.Connection, d, call.Input, call.IdempotencyKey, ct)).Result;
    }

    /// <summary>Runs an action once: for calls through the runtime, and for the action test with sample input.</summary>
    public async Task<RestExecution> ExecuteAsync(PackConnection connection, RestActionDefinition d, JsonElement input, string idempotencyKey, CancellationToken ct)
    {
        var settings = ParseSettings(connection.Settings);
        if (settings is null)
        {
            return new(PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused), null);
        }
        if (RestDefinitions.CheckInput(d, input) is not null)
        {
            return new(PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput), null);
        }
        var baseUri = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        if (!await signIn.IsAllowedAsync(baseUri, ct))
        {
            return new(PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed), null);
        }
        var builder = new StringBuilder(settings.BaseUrl.TrimEnd('/')).Append(RestDefinitions.RenderPath(d.Path, input));
        var query = RestDefinitions.RenderQuery(d.Query ?? new Dictionary<string, string>(), input).ToList();
        if (query.Count > 0)
        {
            builder.Append('?').Append(string.Join('&', query.Select(q => Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(q.Value))));
        }
        var uri = new Uri(builder.ToString());
        if (!string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new(PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed), null);
        }

        using var request = new HttpRequestMessage(new HttpMethod(d.Method), uri);
        foreach (var h in d.Headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(h.Key, RestDefinitions.RenderHeader(h.Value, input));
        }
        if (d.IdempotencyHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(d.IdempotencyHeader, idempotencyKey);
        }
        if (d.Body is { } body)
        {
            request.Content = new StringContent(RestDefinitions.RenderBody(body, input)?.ToJsonString() ?? "null", Encoding.UTF8, "application/json");
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var auth = await signIn.AuthorizeAsync(connection, settings, request, ct);
        if (auth is not null)
        {
            return new(auth, null);
        }

        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        var business = (d.BusinessErrors ?? []).GroupBy(b => b.Status).ToDictionary(g => g.Key, g => g.First().Code);
        if (signIn.MapStatus(connection, settings, response, business) is { } failure)
        {
            return new(failure, status);
        }

        var payload = await RestSignIn.ReadLimitedAsync(response, ct);
        if (payload is null)
        {
            return new(PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true), status);
        }
        var output = d.Output ?? new Dictionary<string, string>();
        if (output.Count == 0 && d.ReferencePath is null)
        {
            return new(PackCallResult.Ok(), status);
        }
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new(PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true), status);
        }
        var mappedOutput = new Dictionary<string, JsonElement?>();
        foreach (var o in output)
        {
            mappedOutput[o.Key] = RestDefinitions.Select(root, o.Value);
        }
        var reference = d.ReferencePath is null ? null : RestDefinitions.Select(root, d.ReferencePath) switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
            _ => null,
        };
        if (reference is { Length: > 200 })
        {
            reference = reference[..200];
        }
        return new(PackCallResult.Ok(JsonSerializer.SerializeToElement(mappedOutput, JsonDefaults.Options), reference), status);
    }

    public static RestSettings? ParseSettings(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object ? settings.Deserialize<RestSettings>(JsonDefaults.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DateOnly? CredentialExpiry(JsonElement settings) => RestSignIn.ExpiresOn(ParseSettings(settings)?.Auth);

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = ParseSettings(settings);
        if (s is null || s.Auth is null)
        {
            throw AppException.Validation("settings", "validation.custom_rest.settings");
        }
        var errors = new ValidationErrors();
        var stored = await signIn.ValidateAsync(tenantId, s.BaseUrl, s.Auth, RestSignIn.AllTypes, errors, ct);
        errors.ThrowIfAny();
        return JsonSerializer.SerializeToElement(stored!, JsonDefaults.Options);
    }

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        var settings = ParseSettings(connection.Settings);
        if (settings is null)
        {
            return [new PackCheck("settings", false, "integrations.test.settings_invalid", ConnectorErrorClasses.RequestRefused)];
        }
        return await signIn.TestAsync(connection, settings, ct);
    }
}
