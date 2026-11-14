using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using TeamBeats.Modules.AiGateway.Contracts;

namespace TeamBeats.Modules.AiGateway.OpenAiCompatible;

/// <summary>
/// The OpenAI-compatible chat completions and embeddings wire format, shared by the OpenRouter adapter and the private
/// endpoint adapter (spec 013 FR-004). Provider DTOs never leave the adapter projects; the gateway sees only its own
/// records. <paramref name="label"/> names the provider in failure messages, never a key.
/// </summary>
public static class OpenAiWire
{
    public static HttpRequestMessage Request(string baseUrl, string path, JsonObject body, string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/{path}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    /// <summary>A message with files as content parts: images as data URLs and PDFs as files, both inline, so nothing is stored at the provider beyond the call.</summary>
    public static JsonArray Parts(string text, IReadOnlyList<AiAttachment> files)
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

    public static JsonObject ChatBody(ProviderChat request, string model, bool stream)
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
        var body = new JsonObject { ["model"] = model, ["messages"] = messages };
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

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage message, bool stream, CancellationTokenSource timeout, string label)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new ProviderException($"{label} timed out", retryable: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException($"{label} could not be reached", retryable: true, ex);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            var retryable = status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500;
            throw new ProviderException($"{label} answered {(int)status}", retryable);
        }
        return response;
    }

    public static async Task<ProviderReply> ReadReplyAsync(HttpResponseMessage response, CancellationToken ct, string label)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ProviderException($"{label} returned a body that is not JSON", retryable: true, ex);
        }
        var choice = body?["choices"]?[0] ?? throw new ProviderException($"{label} returned no choice", retryable: true);
        var message = choice["message"];
        var calls = message?["tool_calls"] is JsonArray array
            ? array.Select(c => new AiToolCall(c?["id"]?.GetValue<string>() ?? string.Empty, c?["function"]?["name"]?.GetValue<string>() ?? string.Empty,
                c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")).ToList()
            : [];
        return new ProviderReply(message?["content"]?.GetValue<string>() ?? string.Empty, calls, Usage(body?["usage"]), choice["finish_reason"]?.GetValue<string>() ?? "stop");
    }

    /// <summary>Reads server-sent events: text deltas, then one final part. A stream that ends without one is left for the gateway to flag as partial.</summary>
    public static async IAsyncEnumerable<ProviderStreamPart> ReadStreamAsync(HttpResponseMessage response, CancellationToken token, CancellationToken caller, string label,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException && !caller.IsCancellationRequested)
            {
                throw new ProviderException($"{label} stream broke", retryable: true, ex);
            }
            if (line is null)
            {
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
            catch (System.Text.Json.JsonException)
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

    public static JsonObject EmbedBody(string model, IReadOnlyList<string> inputs) =>
        new() { ["model"] = model, ["input"] = new JsonArray([.. inputs.Select(i => (JsonNode)JsonValue.Create(i))]) };

    public static async Task<ProviderEmbedding> ReadEmbeddingsAsync(HttpResponseMessage response, int count, CancellationToken ct, string label)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json?["data"] is not JsonArray data || data.Count != count)
        {
            throw new ProviderException($"{label} returned the wrong number of embeddings", retryable: true);
        }
        var vectors = data.OrderBy(d => d?["index"]?.GetValue<int>() ?? 0)
            .Select(d => d!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray()).ToList();
        return new ProviderEmbedding(vectors, Usage(json["usage"]));
    }

    /// <summary>The audio formats a transcription accepts, by media type, and the file extension the endpoint expects.</summary>
    public static readonly IReadOnlyDictionary<string, string> AudioFormats = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["audio/webm"] = "webm",
        ["audio/ogg"] = "ogg",
        ["audio/mpeg"] = "mp3",
        ["audio/mp4"] = "m4a",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
    };

    /// <summary>An OpenAI-compatible transcription request: the audio as a multipart file, which OpenRouter and self-hosted servers both take.</summary>
    public static HttpRequestMessage TranscriptionRequest(string baseUrl, string model, byte[] audio, string mediaType, string? language, string apiKey)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", $"speech.{AudioFormats.GetValueOrDefault(mediaType, "webm")}");
        form.Add(new StringContent(model), "model");
        if (language is not null)
        {
            form.Add(new StringContent(language), "language");
        }
        var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/audio/transcriptions") { Content = form };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    public static async Task<string> ReadTranscriptionAsync(HttpResponseMessage response, CancellationToken ct, string label)
    {
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["text"]?.GetValue<string>()
                ?? throw new ProviderException($"{label} returned no transcription", retryable: true);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ProviderException($"{label} returned a body that is not JSON", retryable: true, ex);
        }
    }

    public static JsonObject SpeechBody(string model, string text, string voice) =>
        new() { ["model"] = model, ["input"] = text, ["voice"] = voice, ["response_format"] = "mp3" };

    public static async Task<byte[]> ReadSpeechAsync(HttpResponseMessage response, CancellationToken ct, string label)
    {
        var audio = await response.Content.ReadAsByteArrayAsync(ct);
        return audio.Length > 0 ? audio : throw new ProviderException($"{label} returned no audio", retryable: true);
    }

    public static AiUsage Usage(JsonNode? usage) => usage is null
        ? AiUsage.None
        : new AiUsage(usage["prompt_tokens"]?.GetValue<int>() ?? 0, usage["completion_tokens"]?.GetValue<int>() ?? 0);
}
