using System.Globalization;
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

namespace TeamBeats.Modules.Integrations.Lms;

/// <summary>
/// Settings of an LMS connection (spec 028 US6, through spec 017): the LMS REST address and sign-in; where the course
/// list is (<paramref name="CoursesPath"/>, its items at <paramref name="CoursesItemsPath"/>, each canonical course field
/// at a path inside an item); where an assignment is sent and which canonical field goes where in its body (dotted names
/// make nested objects); where completions since a time are read (<c>{since}</c> is replaced with an ISO 8601 time) and
/// where each completion field is; an optional next page link in list answers; and the header that carries the
/// idempotency key when the LMS honours one.
/// </summary>
public sealed record LmsSettings(
    string? BaseUrl,
    RestAuth? Auth,
    string? CoursesPath,
    string? CoursesItemsPath,
    IReadOnlyDictionary<string, string>? CourseFields,
    string? AssignPath,
    IReadOnlyDictionary<string, string>? AssignFields,
    string? CompletionsPath,
    string? CompletionsItemsPath,
    IReadOnlyDictionary<string, string>? CompletionFields,
    string? NextPath,
    string? IdempotencyHeader)
{
    public RestSettings Rest => new(BaseUrl!, Auth!);
}

/// <summary>
/// The generic LMS pack over REST (spec 028 FR-015). It reads the course catalogue and completions as canonical lists
/// and sends assignments with only the mapped fields. Launch addresses must be https on an allowed host, so a catalogue
/// item never opens anything else. Its actions declare no inputs, so only the LMS contract calls them.
/// </summary>
public sealed partial class LmsPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : IConnectorPack
{
    public const string KindName = "lms_rest";
    public const string ListCourses = "list_courses";
    public const string Assign = "assign_course";
    public const string ListCompletions = "list_completions";
    public const string InvalidRequest = "invalid_request";
    public const int MaxPages = 50;
    public const int MaxItems = 5000;

    public static readonly IReadOnlyList<string> CourseCanonical = ["external_id", "title", "summary", "launch_url", "language"];
    public static readonly IReadOnlyList<string> AssignCanonical = ["reference", "course_id", "learner_email", "due_on"];
    public static readonly IReadOnlyList<string> CompletionCanonical = ["course_id", "learner_email", "completed_at", "score"];

    private static readonly string[] AuthTypes = ["api_key", "bearer", "basic", "oauth_client_credentials"];
    private static readonly IReadOnlyDictionary<int, string> AssignErrors = new Dictionary<int, string> { [400] = InvalidRequest, [409] = InvalidRequest, [422] = InvalidRequest };

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationConnectors;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new(ListCourses, ConnectorSideEffects.Read, true, []),
        new(Assign, ConnectorSideEffects.Write, false, [InvalidRequest]),
        new(ListCompletions, ConnectorSideEffects.Read, true, []),
    ];

    /// <summary>With an idempotency header the LMS drops a repeated assignment, so a retry after a timeout is safe.</summary>
    public async Task<IReadOnlyList<ConnectorActionDefinition>> ActionsForAsync(PackConnection connection, CancellationToken ct)
    {
        await Task.CompletedTask;
        return Parse(connection.Settings)?.IdempotencyHeader is not null
            ? [Actions[0], Actions[1] with { Idempotent = true }, Actions[2]]
            : Actions;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}(\.[a-z][a-z0-9_]{0,39}){0,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex FieldName();

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]{0,63}|\[[0-9]{1,3}\]){0,8}$")]
    private static partial Regex ResponsePath();

    [GeneratedRegex(@"^/[A-Za-z0-9._~/{}=&?-]{0,300}$")]
    private static partial Regex PathShape();

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"^[A-Za-z0-9._:/-]{1,200}$")]
    private static partial Regex ExternalId();

    public static LmsSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object && settings.Deserialize<LmsSettings>(JsonDefaults.Options) is { BaseUrl: not null, Auth: not null } s ? s : null;
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
            case ListCourses:
            {
                var (failure, items) = await ListAsync(call.Connection, s, s.CoursesPath!, s.CoursesItemsPath!, ct);
                if (failure is not null)
                {
                    return failure;
                }
                var courses = new List<object>();
                foreach (var item in items)
                {
                    if (await CourseAsync(s, item, ct) is { } course)
                    {
                        courses.Add(course);
                    }
                }
                return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { courses }), recordCount: courses.Count);
            }
            case ListCompletions:
            {
                var since = call.Input.ValueKind == JsonValueKind.Object && call.Input.TryGetProperty("since", out var v) && v.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
                if (since is null)
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var path = s.CompletionsPath!.Replace("{since}", Uri.EscapeDataString(since.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                    StringComparison.Ordinal);
                var (failure, items) = await ListAsync(call.Connection, s, path, s.CompletionsItemsPath!, ct);
                if (failure is not null)
                {
                    return failure;
                }
                var completions = items.Select(i => Completion(s, i)).Where(c => c is not null).ToList();
                return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { completions }), recordCount: completions.Count);
            }
            case Assign:
            {
                if (Body(s, call.Input) is not { } body)
                {
                    return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
                }
                var headers = s.IdempotencyHeader is { } h ? new Dictionary<string, string> { [h] = call.IdempotencyKey } : null;
                var (failure, _) = await SendAsync(call.Connection, s, HttpMethod.Post, Address(s, s.AssignPath!), body, headers, AssignErrors, true, ct);
                return failure ?? PackCallResult.Ok(JsonSerializer.SerializeToElement(new { sent = true }));
            }
            default:
                return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
    }

    private static Uri Address(LmsSettings s, string path) => new(s.BaseUrl!.TrimEnd('/') + path);

    private static string? Text(JsonElement item, string? path) =>
        path is not null && RestDefinitions.Select(item, path) is { } v ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()?.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static string? Field(IReadOnlyDictionary<string, string>? fields, string name) => fields?.GetValueOrDefault(name);

    /// <summary>A canonical course, or null when the item lacks an ID, a title or a safe launch address.</summary>
    private async Task<object?> CourseAsync(LmsSettings s, JsonElement item, CancellationToken ct)
    {
        var id = Text(item, Field(s.CourseFields, "external_id"));
        var title = Text(item, Field(s.CourseFields, "title"));
        var launch = Text(item, Field(s.CourseFields, "launch_url"));
        if (id is null || !ExternalId().IsMatch(id) || string.IsNullOrEmpty(title) || launch is null
            || !Uri.TryCreate(launch, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !await signIn.IsAllowedAsync(uri, ct))
        {
            return null;
        }
        var summary = Text(item, Field(s.CourseFields, "summary"));
        var language = Text(item, Field(s.CourseFields, "language"))?.ToLowerInvariant();
        return new
        {
            external_id = id,
            title = title.Length > 200 ? title[..200] : title,
            summary = summary is { Length: > 2000 } ? summary[..2000] : summary,
            launch_url = uri.AbsoluteUri,
            language = language is { Length: >= 2 and <= 10 } ? language : null,
        };
    }

    private static object? Completion(LmsSettings s, JsonElement item)
    {
        var course = Text(item, Field(s.CompletionFields, "course_id"));
        var email = Text(item, Field(s.CompletionFields, "learner_email"));
        var at = Text(item, Field(s.CompletionFields, "completed_at"));
        if (course is null || !ExternalId().IsMatch(course) || email is null || email.Length > 320 || !email.Contains('@')
            || at is null || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var completedAt))
        {
            return null;
        }
        int? score = Text(item, Field(s.CompletionFields, "score")) is { } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            && d is >= 0 and <= 100 ? (int)Math.Round(d) : null;
        return new { course_id = course, learner_email = email, completed_at = completedAt.ToUniversalTime(), score };
    }

    /// <summary>The assignment body: each mapped canonical field at its place, nothing else.</summary>
    public static JsonObject? Body(LmsSettings s, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var body = new JsonObject();
        foreach (var (canonical, target) in s.AssignFields ?? new Dictionary<string, string>())
        {
            if (input.TryGetProperty(canonical, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                Put(body, target, JsonNode.Parse(value.GetRawText()));
            }
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

    /// <summary>Every item of a list, following the next link on the LMS's own host, up to <see cref="MaxPages"/> pages and <see cref="MaxItems"/> items.</summary>
    private async Task<(PackCallResult? Failure, List<JsonElement> Items)> ListAsync(PackConnection connection, LmsSettings s, string path, string itemsPath, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        Uri? next = Address(s, path);
        for (var page = 1; next is not null; page++)
        {
            if (page > MaxPages || items.Count >= MaxItems)
            {
                return (PackCallResult.Fatal(ConnectorErrorClasses.InvalidResponse), items);
            }
            var (failure, answer) = await SendAsync(connection, s, HttpMethod.Get, next, null, null, null, false, ct);
            if (failure is not null)
            {
                return (failure, items);
            }
            if (RestDefinitions.Select(answer, itemsPath) is not { ValueKind: JsonValueKind.Array } list)
            {
                return (PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse), items);
            }
            items.AddRange(list.EnumerateArray().Take(MaxItems - items.Count));
            next = s.NextPath is { } p && Text(answer, p) is { Length: > 0 } link ? NextUri(s, link) : null;
        }
        return (null, items);
    }

    private static Uri? NextUri(LmsSettings s, string link)
    {
        var baseUri = new Uri(s.BaseUrl!.TrimEnd('/') + "/");
        if (link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal))
        {
            return new Uri(baseUri.GetLeftPart(UriPartial.Authority) + link);
        }
        return Uri.TryCreate(link, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttps
            && string.Equals(absolute.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(absolute.UserInfo) ? absolute : null;
    }

    private async Task<(PackCallResult? Failure, JsonElement Body)> SendAsync(PackConnection connection, LmsSettings s, HttpMethod method, Uri uri, JsonObject? body,
        IReadOnlyDictionary<string, string>? headers, IReadOnlyDictionary<int, string>? business, bool writes, CancellationToken ct)
    {
        var baseUri = new Uri(s.BaseUrl!.TrimEnd('/') + "/");
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
        if (writes)
        {
            // An assignment needs no answer: any 2xx means the LMS took it.
            return (null, default);
        }
        return await RestSignIn.ReadJsonAsync(response, ct) is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json
            ? (null, json)
            : (PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse), default);
    }

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.custom_rest.settings");
        var errors = new ValidationErrors();
        var rest = await signIn.ValidateAsync(tenantId, s.BaseUrl, s.Auth, AuthTypes, errors, ct);
        CheckPath(s.CoursesPath, "coursesPath", [], errors);
        CheckPath(s.AssignPath, "assignPath", [], errors);
        CheckPath(s.CompletionsPath, "completionsPath", ["{since}"], errors);
        if (s.CompletionsPath is not null && !s.CompletionsPath.Contains("{since}", StringComparison.Ordinal))
        {
            errors.Add("completionsPath", "validation.lms.path");
        }
        foreach (var (name, path) in new[] { ("coursesItemsPath", s.CoursesItemsPath), ("completionsItemsPath", s.CompletionsItemsPath) })
        {
            if (path is null || !ResponsePath().IsMatch(path))
            {
                errors.Add(name, "validation.lms.response_path");
            }
        }
        if (s.NextPath is not null && (!ResponsePath().IsMatch(s.NextPath) || s.NextPath == "$"))
        {
            errors.Add("nextPath", "validation.lms.response_path");
        }
        var courseFields = Paths(s.CourseFields, CourseCanonical, "courseFields", ["external_id", "title", "launch_url"], errors);
        var completionFields = Paths(s.CompletionFields, CompletionCanonical, "completionFields", ["course_id", "learner_email", "completed_at"], errors);
        var assignFields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, target) in s.AssignFields ?? new Dictionary<string, string>())
        {
            if (!AssignCanonical.Contains(canonical) || target is null || !FieldName().IsMatch(target))
            {
                errors.Add("assignFields", "validation.lms.field_name");
                continue;
            }
            assignFields[canonical] = target;
        }
        var targets = assignFields.Values.ToList();
        if (targets.Any(a => targets.Any(b => !ReferenceEquals(a, b) && (a == b || b.StartsWith(a + ".", StringComparison.Ordinal)))))
        {
            errors.Add("assignFields", "validation.lms.field_twice");
        }
        if (!assignFields.ContainsKey("course_id") || !assignFields.ContainsKey("learner_email"))
        {
            errors.Add("assignFields", "validation.lms.fields_required");
        }
        if (s.IdempotencyHeader is { } header && (!HeaderName().IsMatch(header) || header.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || header.Equals("host", StringComparison.OrdinalIgnoreCase) || header.Equals("cookie", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("idempotencyHeader", "validation.custom_rest.headers");
        }
        errors.ThrowIfAny();
        var stored = new LmsSettings(rest!.BaseUrl, rest.Auth, s.CoursesPath, s.CoursesItemsPath, courseFields, s.AssignPath, assignFields, s.CompletionsPath,
            s.CompletionsItemsPath, completionFields, s.NextPath, s.IdempotencyHeader);
        return JsonSerializer.SerializeToElement(stored, JsonDefaults.Options);
    }

    private static void CheckPath(string? path, string field, IReadOnlyList<string> placeholders, ValidationErrors errors)
    {
        var rest = path;
        foreach (var p in placeholders)
        {
            rest = rest?.Replace(p, "x", StringComparison.Ordinal);
        }
        if (path is null || !PathShape().IsMatch(path) || path.Contains("..", StringComparison.Ordinal) || rest!.IndexOfAny(['{', '}']) >= 0)
        {
            errors.Add(field, "validation.lms.path");
        }
    }

    private static Dictionary<string, string> Paths(IReadOnlyDictionary<string, string>? map, IReadOnlyList<string> allowed, string field, IReadOnlyList<string> required,
        ValidationErrors errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, path) in map ?? new Dictionary<string, string>())
        {
            if (!allowed.Contains(canonical) || path is null || !ResponsePath().IsMatch(path) || path == "$")
            {
                errors.Add(field, "validation.lms.response_path");
                continue;
            }
            result[canonical] = path;
        }
        if (required.Any(r => !result.ContainsKey(r)))
        {
            errors.Add(field, "validation.lms.fields_required");
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
