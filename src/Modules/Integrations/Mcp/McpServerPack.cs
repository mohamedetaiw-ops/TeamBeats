using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Integrations.Ticketing;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Mcp;

/// <summary>
/// An external MCP server as a connection (spec 027 R3, FR-006), over the Streamable HTTP transport: the base URL is
/// the server's MCP endpoint, https and on the tenant allowlist, signed in like any REST connection. Each call opens a
/// session (<c>initialize</c>), sends one request and reads the JSON or event stream answer. <c>list_tools</c> reads the
/// server's tools a page at a time; <c>call_tool</c> calls one tool with arguments as JSON text. Which tools agents may
/// use is decided in <see cref="McpTools"/>, never by the server. Behind <c>ai.agents.mcp</c>.
/// </summary>
public sealed class McpServerPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : TicketingPack(context, signIn, httpClients)
{
    public const string KindName = "mcp_server";
    public const string ListTools = "list_tools";
    public const string CallTool = "call_tool";
    public const string UnknownTool = "unknown_tool";
    public const string InvalidArguments = "invalid_arguments";
    public const string ProtocolVersion = "2025-06-18";
    public const int MaxText = 16_000;

    private readonly IHttpClientFactory _http = httpClients;

    public override string Kind => KindName;

    public override string CapabilityKey => Capabilities.AiAgentsMcp;

    protected override IReadOnlyCollection<string> AuthTypes => ["none", "bearer", "api_key", "oauth_client_credentials"];

    public override IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new(ListTools, ConnectorSideEffects.Read, true, [], [new("cursor", "string", false)]),
        // Whether a tool changes anything is the administrator's call when approving it, so the transport counts every call as a write.
        new(CallTool, ConnectorSideEffects.Write, false, [UnknownTool, InvalidArguments], [new("name", "string", true), new("arguments", "string", true)]),
    ];

    protected override string ProbePath(TicketingSettings settings) => string.Empty;

    protected override async Task<PackCallResult?> ProbeAsync(PackConnection connection, TicketingSettings settings, CancellationToken ct) =>
        (await RequestAsync(connection, settings, "tools/list", new { }, false, ct)).Failure;

    protected override async Task<PackCallResult> CallAsync(PackConnection connection, TicketingSettings settings, string action, JsonElement input, string idempotencyKey,
        CancellationToken ct)
    {
        if (action == ListTools)
        {
            var cursor = Text(input, "cursor");
            if (cursor is { Length: > 1000 })
            {
                return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
            }
            var (failure, result) = await RequestAsync(connection, settings, "tools/list", cursor is null ? new { } : new { cursor }, false, ct);
            return failure ?? Tools(result);
        }

        var name = Text(input, "name")!;
        JsonElement arguments;
        try
        {
            using var doc = JsonDocument.Parse(Text(input, "arguments")!);
            arguments = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
        }
        if (name.Length > 128 || arguments.ValueKind != JsonValueKind.Object)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
        }
        var (callFailure, answer) = await RequestAsync(connection, settings, "tools/call", new { name, arguments }, true, ct);
        return callFailure ?? Answer(answer);
    }

    /// <summary>
    /// One MCP exchange: <c>initialize</c>, the <c>initialized</c> notification, then <paramref name="method"/>. Returns
    /// the failure, or the JSON-RPC <c>result</c>. A JSON-RPC error is a business error for the two codes a tool call can
    /// cause, and a refused request otherwise.
    /// </summary>
    private async Task<(PackCallResult? Failure, JsonElement Result)> RequestAsync(PackConnection connection, TicketingSettings settings, string method, object parameters,
        bool writes, CancellationToken ct)
    {
        var endpoint = new Uri(settings.BaseUrl);
        if (endpoint.Scheme != Uri.UriSchemeHttps || !await SignIn.IsAllowedAsync(endpoint, ct))
        {
            return (PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed), default);
        }
        var init = await PostAsync(connection, settings, endpoint, null, new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = ProtocolVersion, capabilities = new { }, clientInfo = new { name = "TeamBeats", version = "1" } },
        }, 1, false, ct);
        if (init.Failure is not null)
        {
            return (init.Failure, default);
        }
        var notified = await PostAsync(connection, settings, endpoint, init.Session, new { jsonrpc = "2.0", method = "notifications/initialized" }, null, false, ct);
        if (notified.Failure is not null)
        {
            return (notified.Failure, default);
        }
        var answer = await PostAsync(connection, settings, endpoint, init.Session, new { jsonrpc = "2.0", id = 2, method, @params = parameters }, 2, writes, ct);
        if (answer.Failure is not null)
        {
            return (answer.Failure, default);
        }
        var message = answer.Message;
        if (message.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
            return (code switch
            {
                -32602 => PackCallResult.Business(method == "tools/call" && Text((JsonElement?)error, "message") is { } m && m.Contains("tool", StringComparison.OrdinalIgnoreCase)
                    && m.Contains("unknown", StringComparison.OrdinalIgnoreCase) ? UnknownTool : InvalidArguments),
                -32601 => PackCallResult.Business(UnknownTool),
                _ => PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused),
            }, default);
        }
        return message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            ? (null, result)
            : (PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: writes), default);
    }

    private sealed record Exchange(PackCallResult? Failure, JsonElement Message, string? Session);

    private async Task<Exchange> PostAsync(PackConnection connection, TicketingSettings settings, Uri endpoint, string? session, object body, int? id, bool writes,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        if (session is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        }
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json");
        if (await SignIn.AuthorizeAsync(connection, settings.Rest, request, ct) is { } refused)
        {
            return new Exchange(refused, default, null);
        }
        using var client = _http.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (SignIn.MapStatus(connection, settings.Rest, response) is { } failure)
        {
            return new Exchange(failure, default, null);
        }
        var next = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : session;
        if (next is { Length: > 200 } || next?.Any(ch => ch is < '!' or > '~') == true)
        {
            return new Exchange(PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: writes), default, null);
        }
        if (id is null)
        {
            return new Exchange(null, default, next);
        }
        var text = await RestSignIn.ReadLimitedAsync(response, ct);
        var message = text is null ? null : Message(text, response.Content.Headers.ContentType?.MediaType, id.Value);
        return message is { } m
            ? new Exchange(null, m, next)
            : new Exchange(PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: writes), default, null);
    }

    /// <summary>The JSON-RPC answer with <paramref name="id"/>, from a JSON body or from the <c>data</c> lines of an event stream.</summary>
    public static JsonElement? Message(string text, string? mediaType, int id)
    {
        IEnumerable<string> candidates = mediaType == "text/event-stream"
            ? Events(text)
            : [text];
        foreach (var candidate in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var found) && found.ValueKind == JsonValueKind.Number
                    && found.TryGetInt32(out var n) && n == id)
                {
                    return root.Clone();
                }
            }
            catch (JsonException)
            {
                // Not this event.
            }
        }
        return null;
    }

    private static IEnumerable<string> Events(string text)
    {
        var data = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString();
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }
        if (data.Length > 0)
        {
            yield return data.ToString();
        }
    }

    /// <summary>The canonical tool page: name, description, input schema and whether the server says the tool only reads.</summary>
    private static PackCallResult Tools(JsonElement result)
    {
        if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse);
        }
        var list = new List<object>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (Text(tool, "name") is not { Length: > 0 and <= 128 } name || !tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var readOnly = tool.TryGetProperty("annotations", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("readOnlyHint", out var r)
                && r.ValueKind == JsonValueKind.True;
            list.Add(new { name, description = Cut(Text(tool, "description"), 2000) ?? string.Empty, inputSchema = schema, readOnly });
        }
        var next = Cut(Text(result, "nextCursor"), 1000);
        return Done("tools", new { tools = list, next });
    }

    /// <summary>A tool answer: its text content joined and cut, and whether the tool reported an error.</summary>
    private static PackCallResult Answer(JsonElement result)
    {
        var text = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (Text(part, "type") == "text" && Text(part, "text") is { } t)
                {
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }
                    text.Append(t);
                }
            }
        }
        var isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        var value = text.Length > MaxText ? text.ToString(0, MaxText) : text.ToString();
        return Done("call", new { text = value, isError });
    }
}
