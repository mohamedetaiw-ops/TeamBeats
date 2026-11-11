using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Finance;

/// <summary>
/// Settings of a finance hand-off connection (spec 017 FR-021): the finance system's REST address and sign-in, where
/// claims are sent and where their status is read (<c>{reference}</c> is the finance system's own reference), which
/// canonical fields go where in the body (<paramref name="Fields"/> and, per line, <paramref name="LineFields"/> under
/// <paramref name="LinesField"/>; dotted names make nested objects), where the reference and the status are in the
/// answers (paths such as <c>$.data.id</c>), the finance system's words for each payment status, and the header that
/// carries the idempotency key, when the system honours one.
/// </summary>
public sealed record FinanceSettings(
    string? BaseUrl,
    RestAuth? Auth,
    string? SubmitPath,
    string? StatusPath,
    IReadOnlyDictionary<string, string>? Fields,
    string? LinesField,
    IReadOnlyDictionary<string, string>? LineFields,
    string? ReferencePath,
    string? StatusValuePath,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? StatusValues,
    string? IdempotencyHeader)
{
    public RestSettings Rest => new(BaseUrl!, Auth!);
}

/// <summary>
/// The generic finance hand-off pack over REST (spec 017 plan: "generic finance hand-off over REST" for spec 022
/// expenses). It sends only the mapped fields of the canonical claim and returns canonical output: the reference, and
/// for a status read one of <see cref="FinancePaymentStatuses"/> or <c>unknown</c>. Its actions declare no inputs, so
/// only the finance hand-off contract calls them.
/// </summary>
public sealed partial class FinancePack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : IConnectorPack
{
    public const string KindName = "finance_rest";
    public const string Submit = "submit_expense";
    public const string Status = "get_payment_status";
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";

    public static readonly IReadOnlyList<string> CanonicalFields =
        ["source_id", "employee_number", "currency", "amount", "claim_date", "description", "cost_centre", "project"];

    public static readonly IReadOnlyList<string> CanonicalLineFields = ["category", "date", "amount", "description", "original_amount", "original_currency"];

    private static readonly string[] AuthTypes = ["api_key", "bearer", "basic", "oauth_client_credentials"];
    private static readonly IReadOnlyDictionary<int, string> SubmitErrors = new Dictionary<int, string> { [400] = InvalidRequest, [409] = InvalidRequest, [422] = InvalidRequest };
    private static readonly IReadOnlyDictionary<int, string> StatusErrors = new Dictionary<int, string> { [404] = NotFound };

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationConnectors;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new(Submit, ConnectorSideEffects.Write, false, [InvalidRequest]),
        new(Status, ConnectorSideEffects.Read, true, [NotFound]),
    ];

    /// <summary>With an idempotency header the finance system drops a repeated submit, so a retry after a timeout is safe.</summary>
    public async Task<IReadOnlyList<ConnectorActionDefinition>> ActionsForAsync(PackConnection connection, CancellationToken ct)
    {
        await Task.CompletedTask;
        return Parse(connection.Settings)?.IdempotencyHeader is not null
            ? [Actions[0] with { Idempotent = true }, Actions[1]]
            : Actions;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}(\.[a-z][a-z0-9_]{0,39}){0,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex FieldName();

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]{0,63}|\[[0-9]{1,3}\]){1,8}$")]
    private static partial Regex ResponsePath();

    [GeneratedRegex(@"^/[A-Za-z0-9._~/{}-]{0,300}$")]
    private static partial Regex PathShape();

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,100}$")]
    private static partial Regex Reference();

    public static FinanceSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object && settings.Deserialize<FinanceSettings>(JsonDefaults.Options) is { BaseUrl: not null, Auth: not null } s ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DateOnly? CredentialExpiry(JsonElement settings) => RestSignIn.ExpiresOn(Parse(settings)?.Auth);

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (Parse(call.Connection.Settings) is not { } s)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        switch (call.Action)
        {
            case Submit:
            {
                if (Body(s, call.Input) is not { } body)
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var headers = s.IdempotencyHeader is { } h ? new Dictionary<string, string> { [h] = call.IdempotencyKey } : null;
                var (failure, answer) = await SendAsync(call.Connection, s, HttpMethod.Post, s.SubmitPath!, body, headers, SubmitErrors, true, ct);
                if (failure is not null)
                {
                    return failure;
                }
                var reference = Scalar(answer, s.ReferencePath!);
                if (reference is null || !Reference().IsMatch(reference))
                {
                    // The claim may be in finance without a reference we can use: an operator must look.
                    return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true);
                }
                return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { reference, status = MapStatus(s, Scalar(answer, s.StatusValuePath ?? "$.status")) ?? FinancePaymentStatuses.Submitted }),
                    reference);
            }
            case Status:
            {
                var reference = call.Input.ValueKind == JsonValueKind.Object && call.Input.TryGetProperty("reference", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                if (reference is null || !Reference().IsMatch(reference))
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var path = s.StatusPath!.Replace("{reference}", Uri.EscapeDataString(reference), StringComparison.Ordinal);
                var (failure, answer) = await SendAsync(call.Connection, s, HttpMethod.Get, path, null, null, StatusErrors, false, ct);
                if (failure is not null)
                {
                    return failure;
                }
                var status = MapStatus(s, Scalar(answer, s.StatusValuePath ?? "$.status")) ?? "unknown";
                return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { reference, status }), reference);
            }
            default:
                return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
    }

    private static string? MapStatus(FinanceSettings s, string? value)
    {
        if (value is null)
        {
            return null;
        }
        foreach (var (status, words) in s.StatusValues ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            if (words.Any(w => string.Equals(w, value, StringComparison.OrdinalIgnoreCase)))
            {
                return status;
            }
        }
        return null;
    }

    private static string? Scalar(JsonElement answer, string path) =>
        RestDefinitions.Select(answer, path) is { } v ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    /// <summary>The request body: each mapped canonical field at its place, nothing else.</summary>
    public static JsonObject? Body(FinanceSettings s, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var body = new JsonObject();
        foreach (var (canonical, target) in s.Fields ?? new Dictionary<string, string>())
        {
            if (input.TryGetProperty(canonical, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                Put(body, target, JsonNode.Parse(value.GetRawText()));
            }
        }
        if (s.LinesField is { } linesField && input.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var line in lines.EnumerateArray())
            {
                var item = new JsonObject();
                foreach (var (canonical, target) in s.LineFields ?? new Dictionary<string, string>())
                {
                    if (line.TryGetProperty(canonical, out var value) && value.ValueKind != JsonValueKind.Null)
                    {
                        Put(item, target, JsonNode.Parse(value.GetRawText()));
                    }
                }
                array.Add(item);
            }
            Put(body, linesField, array);
        }
        return body;
    }

    private static void Put(JsonObject root, string dotted, JsonNode? value)
    {
        var parts = dotted.Split('.');
        var current = root;
        foreach (var part in parts[..^1])
        {
            if (current[part] is not JsonObject next)
            {
                next = new JsonObject();
                current[part] = next;
            }
            current = next;
        }
        current[parts[^1]] = value;
    }

    private async Task<(PackCallResult? Failure, JsonElement Body)> SendAsync(PackConnection connection, FinanceSettings s, HttpMethod method, string path,
        JsonObject? body, IReadOnlyDictionary<string, string>? headers, IReadOnlyDictionary<int, string> business, bool writes, CancellationToken ct)
    {
        var baseUri = new Uri(s.BaseUrl!.TrimEnd('/') + "/");
        var uri = new Uri(s.BaseUrl!.TrimEnd('/') + path);
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || !await signIn.IsAllowedAsync(baseUri, ct))
        {
            return (PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed), default);
        }
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (await signIn.AuthorizeAsync(connection, s.Rest, request, ct) is { } refused)
        {
            return (refused, default);
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (signIn.MapStatus(connection, s.Rest, response, business) is { } failure)
        {
            return (failure, default);
        }
        return await RestSignIn.ReadJsonAsync(response, ct) is { ValueKind: JsonValueKind.Object } json
            ? (null, json)
            : (PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: writes), default);
    }

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.custom_rest.settings");
        var errors = new ValidationErrors();
        var rest = await signIn.ValidateAsync(tenantId, s.BaseUrl, s.Auth, AuthTypes, errors, ct);
        if (s.SubmitPath is null || !PathShape().IsMatch(s.SubmitPath) || s.SubmitPath.Contains("..", StringComparison.Ordinal) || s.SubmitPath.Contains('{'))
        {
            errors.Add("submitPath", "validation.finance.submit_path");
        }
        if (s.StatusPath is null || !PathShape().IsMatch(s.StatusPath) || s.StatusPath.Contains("..", StringComparison.Ordinal)
            || s.StatusPath.Replace("{reference}", "x", StringComparison.Ordinal).IndexOfAny(['{', '}']) >= 0 || !s.StatusPath.Contains("{reference}", StringComparison.Ordinal))
        {
            errors.Add("statusPath", "validation.finance.status_path");
        }
        var fields = Map(s.Fields, CanonicalFields, "fields", errors);
        if (!fields.ContainsKey("amount") || !fields.ContainsKey("currency") || !fields.ContainsKey("employee_number"))
        {
            errors.Add("fields", "validation.finance.fields_required");
        }
        var lineFields = s.LinesField is null ? new Dictionary<string, string>() : Map(s.LineFields, CanonicalLineFields, "lineFields", errors);
        if (s.LinesField is not null && !FieldName().IsMatch(s.LinesField))
        {
            errors.Add("linesField", "validation.finance.field_name");
        }
        if (s.ReferencePath is null || !ResponsePath().IsMatch(s.ReferencePath))
        {
            errors.Add("referencePath", "validation.finance.response_path");
        }
        if (s.StatusValuePath is not null && !ResponsePath().IsMatch(s.StatusValuePath))
        {
            errors.Add("statusValuePath", "validation.finance.response_path");
        }
        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (status, words) in s.StatusValues ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            var clean = (words ?? []).Select(w => w.Trim()).Where(w => w.Length is > 0 and <= 50).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!FinancePaymentStatuses.All.Contains(status) || clean.Count is 0 or > 10 || clean.Any(w => !seen.Add(w)))
            {
                errors.Add("statusValues", "validation.finance.status_values");
                continue;
            }
            values[status] = clean;
        }
        if (!values.ContainsKey(FinancePaymentStatuses.Paid))
        {
            errors.Add("statusValues", "validation.finance.status_values");
        }
        if (s.IdempotencyHeader is { } header && (!HeaderName().IsMatch(header) || header.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || header.Equals("host", StringComparison.OrdinalIgnoreCase) || header.Equals("cookie", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("idempotencyHeader", "validation.custom_rest.headers");
        }
        errors.ThrowIfAny();
        var stored = new FinanceSettings(rest!.BaseUrl, rest.Auth, s.SubmitPath, s.StatusPath, fields, s.LinesField, s.LinesField is null ? null : lineFields,
            s.ReferencePath, s.StatusValuePath, values, s.IdempotencyHeader);
        return JsonSerializer.SerializeToElement(stored, JsonDefaults.Options);
    }

    private static Dictionary<string, string> Map(IReadOnlyDictionary<string, string>? map, IReadOnlyList<string> allowed, string field, ValidationErrors errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, target) in map ?? new Dictionary<string, string>())
        {
            if (!allowed.Contains(canonical) || target is null || !FieldName().IsMatch(target))
            {
                errors.Add(field, "validation.finance.field_name");
                continue;
            }
            result[canonical] = target;
        }
        // Two fields at one place, or one place inside another, would overwrite each other.
        var targets = result.Values.ToList();
        if (targets.Any(a => targets.Any(b => !ReferenceEquals(a, b) && (a == b || b.StartsWith(a + ".", StringComparison.Ordinal)))))
        {
            errors.Add(field, "validation.finance.field_twice");
        }
        return result;
    }

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { } s)
        {
            return [new PackCheck("settings", false, "integrations.test.settings_invalid", ConnectorErrorClasses.RequestRefused)];
        }
        return await signIn.TestAsync(connection, s.Rest, ct);
    }
}
