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
    HostAllowlist allowlist,
    ISecretStore secrets,
    IHttpClientFactory httpClients,
    RestTokenCache tokens,
    IClock clock) : IConnectorPack
{
    public const string KindName = "custom_rest";
    public const int MaxResponseBytes = 1024 * 1024;

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
        if (!await allowlist.IsAllowedAsync(baseUri, ct))
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
        var auth = await AuthorizeAsync(connection, settings, request, ct);
        if (auth is not null)
        {
            return new(auth, null);
        }

        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        if (status is 401 or 403)
        {
            if (settings.Auth.Type == "oauth_client_credentials")
            {
                tokens.Drop(TokenKey(connection, settings));
            }
            return new(PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused), status);
        }
        if (status == 429)
        {
            return new(PackCallResult.Technical(ConnectorErrorClasses.Throttled, retryAfter: RetryAfter(response)), status);
        }
        if (status >= 500)
        {
            return new(PackCallResult.Technical(ConnectorErrorClasses.ServerError, mayHaveActed: true, retryAfter: RetryAfter(response)), status);
        }
        if (status >= 400)
        {
            var mapped = (d.BusinessErrors ?? []).FirstOrDefault(b => b.Status == status);
            return new(mapped is null ? PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused) : PackCallResult.Business(mapped.Code), status);
        }
        if (status is < 200 or >= 300)
        {
            return new(PackCallResult.Fatal(ConnectorErrorClasses.InvalidResponse), status);
        }

        var payload = await ReadLimitedAsync(response, ct);
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

    private async Task<PackCallResult?> AuthorizeAsync(PackConnection connection, RestSettings settings, HttpRequestMessage request, CancellationToken ct)
    {
        var a = settings.Auth;
        if (a.Type == "none")
        {
            return null;
        }
        var secret = a.SecretRef is null ? null : await secrets.GetTenantSecretAsync(connection.TenantId, a.SecretRef, ct);
        if (string.IsNullOrEmpty(secret))
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.CredentialMissing);
        }
        switch (a.Type)
        {
            case "api_key":
                request.Headers.TryAddWithoutValidation(a.Header!, secret);
                return null;
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                return null;
            case "basic":
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{a.Username}:{secret}")));
                return null;
            case "oauth_client_credentials":
                var (token, failure) = await TokenAsync(connection, settings, secret, ct);
                if (token is null)
                {
                    return failure;
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return null;
            default:
                return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
    }

    private async Task<(string? Token, PackCallResult? Failure)> TokenAsync(PackConnection connection, RestSettings settings, string secret, CancellationToken ct)
    {
        var key = TokenKey(connection, settings);
        var now = clock.UtcNow;
        if (tokens.Get(key, now) is { } cached)
        {
            return (cached, null);
        }
        var a = settings.Auth;
        var tokenUri = new Uri(a.TokenUrl!);
        if (!await allowlist.IsAllowedAsync(tokenUri, ct))
        {
            return (null, PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed));
        }
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = a.ClientId!, ["client_secret"] = secret };
        if (!string.IsNullOrEmpty(a.Scope))
        {
            form["scope"] = a.Scope;
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(tokenUri, content, ct);
        var status = (int)response.StatusCode;
        if (status is 400 or 401 or 403)
        {
            return (null, PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused));
        }
        if (!response.IsSuccessStatusCode)
        {
            return (null, PackCallResult.Technical(status == 429 ? ConnectorErrorClasses.Throttled : ConnectorErrorClasses.ServerError, retryAfter: RetryAfter(response)));
        }
        var payload = await ReadLimitedAsync(response, ct);
        try
        {
            using var doc = JsonDocument.Parse(payload ?? "{}");
            var token = doc.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrEmpty(token))
            {
                return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
            }
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 300;
            tokens.Set(key, token, now.AddSeconds(Math.Max(0, seconds - 60)));
            return (token, null);
        }
        catch (JsonException)
        {
            return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
        }
    }

    private static string TokenKey(PackConnection c, RestSettings s) =>
        $"{c.TenantId:N}:{c.Id:N}:{s.Auth.TokenUrl}:{s.Auth.ClientId}:{s.Auth.SecretRef}:{s.Auth.Scope}";

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } d ? (d > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : d) : null;

    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        int read;
        while (total <= MaxResponseBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
        }
        return total > MaxResponseBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
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

    public DateOnly? CredentialExpiry(JsonElement settings) =>
        ParseSettings(settings)?.Auth?.ExpiresOn is { } e && DateOnly.TryParseExact(e, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = ParseSettings(settings);
        var errors = new ValidationErrors();
        if (s is null || s.Auth is null)
        {
            throw AppException.Validation("settings", "validation.custom_rest.settings");
        }
        if (!Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) || !baseUri.IsDefaultPort && baseUri.Port is < 1 or > 65535
            || IPAddress.TryParse(baseUri.IdnHost.Trim('[', ']'), out _))
        {
            errors.Add("baseUrl", "validation.custom_rest.base_url");
        }
        else if (!await allowlist.IsAllowedAsync(baseUri, ct))
        {
            errors.Add("baseUrl", "validation.custom_rest.host_not_allowed");
        }
        var a = s.Auth;
        if (a.Type is not ("none" or "api_key" or "bearer" or "basic" or "oauth_client_credentials"))
        {
            errors.Add("auth.type", "validation.custom_rest.auth_type");
        }
        if (a.Type != "none" && SecretReferences.NameFor(tenantId, a.SecretRef) is null)
        {
            errors.Add("auth.secretRef", "validation.custom_rest.secret_ref");
        }
        if (a.Type == "api_key" && (a.Header is null || a.Header.Length > 64 || a.Header.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            || a.Header.Equals("host", StringComparison.OrdinalIgnoreCase) || a.Header.Equals("cookie", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("auth.header", "validation.custom_rest.headers");
        }
        if (a.Type == "basic" && (string.IsNullOrEmpty(a.Username) || a.Username.Length > 200 || a.Username.Contains(':')))
        {
            errors.Add("auth.username", "validation.custom_rest.username");
        }
        if (a.Type == "oauth_client_credentials")
        {
            if (!Uri.TryCreate(a.TokenUrl, UriKind.Absolute, out var tokenUri) || tokenUri.Scheme != Uri.UriSchemeHttps || !await allowlist.IsAllowedAsync(tokenUri, ct))
            {
                errors.Add("auth.tokenUrl", "validation.custom_rest.host_not_allowed");
            }
            if (string.IsNullOrEmpty(a.ClientId) || a.ClientId.Length > 200)
            {
                errors.Add("auth.clientId", "validation.custom_rest.client_id");
            }
            if (a.Scope is { Length: > 500 })
            {
                errors.Add("auth.scope", "validation.custom_rest.scope");
            }
        }
        if (a.ExpiresOn is not null && !DateOnly.TryParseExact(a.ExpiresOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add("auth.expiresOn", "validation.custom_rest.expires_on");
        }
        errors.ThrowIfAny();
        // Stored with only the fields that apply to the sign-in type.
        var stored = new RestSettings(baseUri!.GetLeftPart(UriPartial.Path).TrimEnd('/'), new RestAuth(a.Type,
            a.Type == "api_key" ? a.Header : null, a.Type == "basic" ? a.Username : null, a.Type == "none" ? null : a.SecretRef,
            a.Type == "oauth_client_credentials" ? a.TokenUrl : null, a.Type == "oauth_client_credentials" ? a.ClientId : null,
            a.Type == "oauth_client_credentials" ? a.Scope : null, a.ExpiresOn));
        return JsonSerializer.SerializeToElement(stored, JsonDefaults.Options);
    }

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        var settings = ParseSettings(connection.Settings);
        if (settings is null)
        {
            return [new PackCheck("settings", false, "integrations.test.settings_invalid", ConnectorErrorClasses.RequestRefused)];
        }
        var checks = new List<PackCheck>();
        var hostOk = await allowlist.IsAllowedAsync(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), ct);
        checks.Add(new PackCheck("base_url", hostOk, hostOk ? "integrations.test.host_allowed" : "integrations.test.host_not_allowed",
            hostOk ? null : ConnectorErrorClasses.HostNotAllowed));
        if (settings.Auth.Type != "none")
        {
            var secret = settings.Auth.SecretRef is null ? null : await secrets.GetTenantSecretAsync(connection.TenantId, settings.Auth.SecretRef, ct);
            var found = !string.IsNullOrEmpty(secret);
            checks.Add(new PackCheck("credential", found, found ? "integrations.test.credential_found" : "integrations.test.credential_missing",
                found ? null : ConnectorErrorClasses.CredentialMissing));
            if (found && settings.Auth.Type == "oauth_client_credentials")
            {
                tokens.Drop(TokenKey(connection, settings));
                PackCheck check;
                try
                {
                    var (token, failure) = await TokenAsync(connection, settings, secret!, ct);
                    check = token is not null
                        ? new PackCheck("token", true, "integrations.test.token_ok")
                        : new PackCheck("token", false, "integrations.test.token_refused", failure?.ErrorClass);
                }
                catch (HttpRequestException)
                {
                    check = new PackCheck("token", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    check = new PackCheck("token", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
                }
                checks.Add(check);
            }
        }
        return checks;
    }
}
