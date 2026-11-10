using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.CustomRest;

/// <summary>An input the caller maps (spec 017 FR-008): <c>string</c>, <c>number</c>, <c>integer</c> or <c>boolean</c>.</summary>
public sealed record RestInput(string Name, string Type, bool Required);

/// <summary>A 4xx answer the route can branch on, for example 404 as <c>permit_not_found</c>.</summary>
public sealed record RestBusinessError(int Status, string Code);

/// <summary>
/// A custom REST action (spec 017 US4, FR-011), with no code: method, path template, query, headers and a JSON body
/// template whose placeholders <c>{{input}}</c> name declared inputs, and a response mapping of output fields to JSON
/// paths such as <c>$.permit.status</c> or <c>$.items[0].id</c>. A body value that is exactly one placeholder takes the
/// input's own type; a placeholder inside longer text is replaced by its text. Values are escaped where they land, so an
/// input can never change the path, a header line or the JSON structure.
/// </summary>
public sealed record RestActionDefinition(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string>? Query,
    IReadOnlyDictionary<string, string>? Headers,
    JsonElement? Body,
    IReadOnlyList<RestInput>? Inputs,
    IReadOnlyDictionary<string, string>? Output,
    string? ReferencePath,
    string SideEffect,
    string? IdempotencyHeader,
    IReadOnlyList<RestBusinessError>? BusinessErrors);

/// <summary>Validation and rendering of <see cref="RestActionDefinition"/>. Pure functions, no I/O.</summary>
public static partial class RestDefinitions
{
    public const int MaxBodyBytes = 16 * 1024;
    public static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    public static readonly string[] InputTypes = ["string", "number", "integer", "boolean"];

    /// <summary>Headers the connection sets itself or that would change how the request travels.</summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "host", "cookie", "content-length", "content-type", "transfer-encoding", "connection", "upgrade", "te", "expect",
        "keep-alive", "proxy-authorization", "proxy-connection", "forwarded", "x-forwarded-for", "x-forwarded-host", "x-forwarded-proto",
    };

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    public static partial Regex Placeholder();

    [GeneratedRegex(@"^\{\{\s*([A-Za-z0-9_]+)\s*\}\}$")]
    private static partial Regex WholePlaceholder();

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9_]{0,40}$")]
    private static partial Regex FieldName();

    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$")]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]*|\[\d{1,4}\])*$")]
    private static partial Regex JsonPath();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}$")]
    public static partial Regex ActionName();

    [GeneratedRegex(@"^/[A-Za-z0-9._~!$&'()*+,;=:@%/{}\s-]*$")]
    private static partial Regex PathShape();

    public static RestActionDefinition Validate(RestActionDefinition? d)
    {
        var errors = new ValidationErrors();
        if (d is null)
        {
            throw AppException.Validation("definition", "validation.custom_rest.definition_required");
        }
        var method = d.Method?.ToUpperInvariant() ?? string.Empty;
        if (!Methods.Contains(method))
        {
            errors.Add("method", "validation.custom_rest.method");
        }
        var inputs = d.Inputs ?? [];
        if (inputs.Count > 50 || inputs.Any(i => i is null || !FieldName().IsMatch(i.Name ?? string.Empty) || !InputTypes.Contains(i.Type))
            || inputs.Select(i => i?.Name).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
        {
            errors.Add("inputs", "validation.custom_rest.inputs");
        }
        var declared = inputs.Where(i => i is not null).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);

        void Templated(string field, string? text)
        {
            foreach (Match m in Placeholder().Matches(text ?? string.Empty))
            {
                if (!declared.Contains(m.Groups[1].Value))
                {
                    errors.Add(field, "validation.custom_rest.undeclared_input");
                    return;
                }
            }
        }

        var path = d.Path ?? string.Empty;
        if (path.Length is 0 or > 500 || !PathShape().IsMatch(path) || path.Contains("..", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal)
            || Placeholder().Replace(path, "x").IndexOfAny(['{', '}', ' ']) >= 0)
        {
            errors.Add("path", "validation.custom_rest.path");
        }
        Templated("path", path);

        var query = d.Query ?? new Dictionary<string, string>();
        if (query.Count > 20 || query.Any(q => !HeaderName().IsMatch(q.Key) || q.Value is null || q.Value.Length > 500))
        {
            errors.Add("query", "validation.custom_rest.query");
        }
        foreach (var q in query)
        {
            Templated($"query.{q.Key}", q.Value);
        }

        var headers = d.Headers ?? new Dictionary<string, string>();
        if (headers.Count > 20 || headers.Any(h => !HeaderName().IsMatch(h.Key) || ReservedHeaders.Contains(h.Key) || h.Value is null || h.Value.Length > 500
            || h.Value.IndexOfAny(['\r', '\n', '\0']) >= 0
            || (d.IdempotencyHeader is not null && string.Equals(h.Key, d.IdempotencyHeader, StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add("headers", "validation.custom_rest.headers");
        }
        foreach (var h in headers)
        {
            Templated($"headers.{h.Key}", h.Value);
        }

        if (d.Body is { } body && body.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            if (method is "GET" or "DELETE")
            {
                errors.Add("body", "validation.custom_rest.body_not_allowed");
            }
            if (Encoding.UTF8.GetByteCount(body.GetRawText()) > MaxBodyBytes)
            {
                errors.Add("body", "validation.custom_rest.body_too_large");
            }
            foreach (var text in Strings(body))
            {
                Templated("body", text);
            }
        }

        var output = d.Output ?? new Dictionary<string, string>();
        if (output.Count > 50 || output.Any(o => !FieldName().IsMatch(o.Key) || o.Value is null || !JsonPath().IsMatch(o.Value)))
        {
            errors.Add("output", "validation.custom_rest.output");
        }
        if (d.ReferencePath is not null && !JsonPath().IsMatch(d.ReferencePath))
        {
            errors.Add("referencePath", "validation.custom_rest.output");
        }
        if (d.SideEffect is not (ConnectorSideEffects.Read or ConnectorSideEffects.Write or ConnectorSideEffects.ExternalEffect))
        {
            errors.Add("sideEffect", "validation.custom_rest.side_effect");
        }
        if (d.IdempotencyHeader is not null && (!HeaderName().IsMatch(d.IdempotencyHeader) || ReservedHeaders.Contains(d.IdempotencyHeader)))
        {
            errors.Add("idempotencyHeader", "validation.custom_rest.headers");
        }
        var business = d.BusinessErrors ?? [];
        if (business.Count > 20 || business.Any(b => b is null || b.Status is < 400 or > 499 || b.Status is 401 or 403 or 407 or 429
            || !ActionName().IsMatch(b.Code ?? string.Empty)) || business.Select(b => b?.Status).Distinct().Count() != business.Count)
        {
            errors.Add("businessErrors", "validation.custom_rest.business_errors");
        }
        errors.ThrowIfAny();
        return d with
        {
            Method = method,
            Query = query,
            Headers = headers,
            Inputs = inputs,
            Output = output,
            BusinessErrors = business,
            Body = d.Body is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } b2 ? b2 : null,
        };
    }

    /// <summary>
    /// Checks a call's input against the declared inputs: required ones present, types as declared. Fields that are not
    /// declared are never sent anywhere, so they are ignored. Returns null when valid.
    /// </summary>
    public static string? CheckInput(RestActionDefinition d, JsonElement input)
    {
        if (input.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            return "input";
        }
        foreach (var i in d.Inputs ?? [])
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(i.Name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                if (i.Required)
                {
                    return i.Name;
                }
                continue;
            }
            var ok = i.Type switch
            {
                "string" => v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 4000,
                "number" => v.ValueKind == JsonValueKind.Number,
                "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
                "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false,
            };
            if (!ok)
            {
                return i.Name;
            }
        }
        return null;
    }

    /// <summary>The path with each placeholder escaped as one path segment value.</summary>
    public static string RenderPath(string path, JsonElement input) =>
        Placeholder().Replace(path, m => Uri.EscapeDataString(Text(input, m.Groups[1].Value) ?? string.Empty));

    /// <summary>Query parameters; one whose template is a single missing optional input is left out.</summary>
    public static IEnumerable<KeyValuePair<string, string>> RenderQuery(IReadOnlyDictionary<string, string> query, JsonElement input)
    {
        foreach (var q in query)
        {
            var whole = WholePlaceholder().Match(q.Value);
            if (whole.Success && Text(input, whole.Groups[1].Value) is null)
            {
                continue;
            }
            yield return new(q.Key, Placeholder().Replace(q.Value, m => Text(input, m.Groups[1].Value) ?? string.Empty));
        }
    }

    /// <summary>A header value with control characters removed from what the inputs bring in.</summary>
    public static string RenderHeader(string value, JsonElement input) =>
        Placeholder().Replace(value, m => new string((Text(input, m.Groups[1].Value) ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()));

    public static JsonNode? RenderBody(JsonElement body, JsonElement input) => body.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(body.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, RenderBody(p.Value, input)))),
        JsonValueKind.Array => new JsonArray(body.EnumerateArray().Select(e => RenderBody(e, input)).ToArray()),
        JsonValueKind.String => RenderString(body.GetString()!, input),
        _ => JsonNode.Parse(body.GetRawText()),
    };

    private static JsonNode? RenderString(string text, JsonElement input)
    {
        var whole = WholePlaceholder().Match(text);
        if (whole.Success)
        {
            return input.ValueKind == JsonValueKind.Object && input.TryGetProperty(whole.Groups[1].Value, out var v) && v.ValueKind != JsonValueKind.Null
                ? JsonNode.Parse(v.GetRawText())
                : null;
        }
        return JsonValue.Create(Placeholder().Replace(text, m => Text(input, m.Groups[1].Value) ?? string.Empty));
    }

    /// <summary>Evaluates a path of the form <c>$.a.b[0].c</c>; null when any step is missing.</summary>
    public static JsonElement? Select(JsonElement root, string path)
    {
        var current = root;
        var i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var end = i + 1;
                while (end < path.Length && path[end] is not ('.' or '['))
                {
                    end++;
                }
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(path[(i + 1)..end], out current))
                {
                    return null;
                }
                i = end;
            }
            else
            {
                var end = path.IndexOf(']', i);
                var index = int.Parse(path[(i + 1)..end], CultureInfo.InvariantCulture);
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return null;
                }
                current = current[index];
                i = end + 1;
            }
        }
        return current;
    }

    private static string? Text(JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static IEnumerable<string> Strings(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                yield return e.GetString()!;
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    foreach (var s in Strings(p.Value))
                    {
                        yield return s;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray())
                {
                    foreach (var s in Strings(x))
                    {
                        yield return s;
                    }
                }
                break;
        }
    }
}
