using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.AiGateway.Contracts;

namespace TeamBeats.Modules.AiGateway.OpenRouter;

/// <summary>
/// The OpenRouter adapter (spec 013 FR-004, ADR-0003): the OpenAI-compatible chat completions and embeddings API. This
/// project is the only place that knows OpenRouter's URL and wire format. The platform key is configuration from the
/// secret store (<c>Ai:OpenRouter:ApiKey</c>) and is never logged. Timeouts, 429 and 5xx answers are retryable.
/// </summary>
public sealed class OpenRouterProvider(IHttpClientFactory http, IConfiguration configuration) : IModelProvider
{
    public const string ClientName = "openrouter";
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
    public const string ApiKeySetting = "Ai:OpenRouter:ApiKey";
    public const string BaseUrlSetting = "Ai:OpenRouter:BaseUrl";
    public const string TimeoutSetting = "Ai:OpenRouter:TimeoutSeconds";

    /// <summary>Set on installs whose network blocks egress (dedicated or on-premises): OpenRouter is unreachable by design (US6 scenario 3).</summary>
    public const string EgressBlockedSetting = "Ai:EgressBlocked";

    public string Key => "openrouter";

    private string? ApiKey => configuration[ApiKeySetting] is { Length: > 0 } key ? key : null;

    public bool Reachable => ApiKey is not null && !configuration.GetValue<bool>(EgressBlockedSetting);

    private string BaseUrl => (configuration[BaseUrlSetting] is { Length: > 0 } url ? url : DefaultBaseUrl).TrimEnd('/');

    private TimeSpan Timeout => TimeSpan.FromSeconds(configuration.GetValue<int?>(TimeoutSetting) ?? 60);

    private HttpRequestMessage Request(string path, JsonObject body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{path}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey ?? throw new ProviderException("OpenRouter is not configured", retryable: false));
        message.Headers.Add("X-Title", "TeamBeats");
        return message;
    }

    /// <summary>
    /// A message with files as content parts: images as data URLs and PDFs as files, both inline, so nothing is stored
    /// at the provider beyond the call.
    /// </summary>
    internal static JsonArray Parts(string text, IReadOnlyList<AiAttachment> files)
    {
        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        foreach (var file in files)
        {
            var url = $"data:{file.MediaType};base64,{Convert.ToBase64String(file.Data)}";
            parts.Add(file.MediaType == "application/pdf"
                ? new JsonObject { ["type"] = "file", ["file"] = new JsonObject { ["filename"] = "document.pdf", ["file_data"] = url } }
                : new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
        }
        return parts;
    }

    internal static JsonObject ChatBody(ProviderChat request, bool stream)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var message = new JsonObject { ["role"] = m.Role, ["content"] = m.Attachments is { Count: > 0 } files ? Parts(m.Content, files) : m.Content };
            if (m.ToolCallId is not null)
            {
                message["tool_call_id"] = m.ToolCallId;
            }
            if (m.ToolCalls is { Count: > 0 } calls)
            {
                message["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                })]);
            }
            messages.Add(message);
        }
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages };
        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.Parameters.GetRawText()) },
            })]);
        }
        if (request.ResponseSchema is { } schema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "response", ["strict"] = true, ["schema"] = JsonNode.Parse(schema.GetRawText()) },
            };
        }
        if (request.MaxTokens is { } max)
        {
            body["max_tokens"] = max;
        }
        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }
        if (stream)
        {
            body["stream"] = true;
        }
        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, bool stream, CancellationTokenSource timeout)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(ClientName).SendAsync(message, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new ProviderException("OpenRouter timed out", retryable: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("OpenRouter could not be reached", retryable: true, ex);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            var retryable = status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500;
            throw new ProviderException($"OpenRouter answered {(int)status}", retryable);
        }
        return response;
    }

    public async Task<ProviderReply> ChatAsync(ProviderChat request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await SendAsync(Request("chat/completions", ChatBody(request, stream: false)), stream: false, timeout);
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (JsonException ex)
        {
            throw new ProviderException("OpenRouter returned a body that is not JSON", retryable: true, ex);
        }
        var choice = body?["choices"]?[0];
        if (choice is null)
        {
            throw new ProviderException("OpenRouter returned no choice", retryable: true);
        }
        var message = choice["message"];
        var calls = message?["tool_calls"] is JsonArray array
            ? array.Select(c => new AiToolCall(c?["id"]?.GetValue<string>() ?? string.Empty, c?["function"]?["name"]?.GetValue<string>() ?? string.Empty,
                c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")).ToList()
            : [];
        return new ProviderReply(message?["content"]?.GetValue<string>() ?? string.Empty, calls, Usage(body?["usage"]), choice["finish_reason"]?.GetValue<string>() ?? "stop");
    }

    public async IAsyncEnumerable<ProviderStreamPart> StreamAsync(ProviderChat request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await SendAsync(Request("chat/completions", ChatBody(request, stream: true)), stream: true, timeout);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new ProviderException("OpenRouter stream broke", retryable: true, ex);
            }
            if (line is null)
            {
                // The stream ended without a final chunk; the gateway flags what arrived as partial.
                yield break;
            }
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }
            var data = line[6..];
            if (data == "[DONE]")
            {
                yield break;
            }
            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }
            var choice = chunk?["choices"]?[0];
            var delta = choice?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(delta))
            {
                yield return new ProviderStreamPart(delta, null);
            }
            if (choice?["finish_reason"]?.GetValue<string>() is { } finish)
            {
                yield return new ProviderStreamPart(null, new ProviderReply(string.Empty, [], Usage(chunk?["usage"]), finish));
                yield break;
            }
        }
    }

    public async Task<ProviderEmbedding> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var body = new JsonObject { ["model"] = model, ["input"] = new JsonArray([.. inputs.Select(i => (JsonNode)JsonValue.Create(i))]) };
        using var response = await SendAsync(Request("embeddings", body), stream: false, timeout);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (json?["data"] is not JsonArray data || data.Count != inputs.Count)
        {
            throw new ProviderException("OpenRouter returned the wrong number of embeddings", retryable: true);
        }
        var vectors = data.OrderBy(d => d?["index"]?.GetValue<int>() ?? 0)
            .Select(d => d!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray()).ToList();
        return new ProviderEmbedding(vectors, Usage(json["usage"]));
    }

    private static AiUsage Usage(JsonNode? usage) => usage is null
        ? AiUsage.None
        : new AiUsage(usage["prompt_tokens"]?.GetValue<int>() ?? 0, usage["completion_tokens"]?.GetValue<int>() ?? 0);
}

public static class OpenRouterModule
{
    public static IServiceCollection AddOpenRouterAdapter(this IServiceCollection services)
    {
        services.AddHttpClient(OpenRouterProvider.ClientName);
        services.AddSingleton<IModelProvider, OpenRouterProvider>();
        return services;
    }
}
