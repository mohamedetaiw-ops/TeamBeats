using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamBeats.DevFakes;

/// <summary>
/// In-memory OpenRouter (ADR-0003) for the AI gateway of spec 013, so AI features can be exercised without a model
/// provider account or cost. It answers the OpenAI-compatible chat completions and models endpoints with a deterministic
/// reply built from the last user message, in Arabic when that message is Arabic, and reports token usage.
/// Failure cases are triggered by markers in the last user message, for the gateway's failure tests:
/// <c>[fake:429]</c> rate limited, <c>[fake:500]</c> provider error, <c>[fake:fail:model]</c> provider error for that model only, <c>[fake:timeout]</c> no answer until cancelled,
/// <c>[fake:refuse]</c> a content filter stop, <c>[fake:cut]</c> a stream that ends without a final chunk,
/// <c>[fake:tool:name]</c> a call of tool <c>name</c>, <c>[fake:badjson]</c> plain text even when a JSON schema was asked
/// for. When the prompt holds numbered passages (the assistant of spec 014) the reply cites passage 1 as <c>[1]</c>, unless
/// the question holds <c>[fake:nocite]</c>. With a <c>response_format</c> the reply is the JSON object <c>{"answer": ...}</c>. Streaming
/// (<c>stream: true</c>) returns server-sent events. Embeddings are a bag of words (see <see cref="Embed"/>).
/// </summary>
public sealed partial class FakeOpenRouter : HttpMessageHandler
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\[fake:tool:([a-z_]+)\]")]
    private static partial System.Text.RegularExpressions.Regex ToolMarker();

    public const string BaseUrl = "https://openrouter.ai/api/v1";
    public const string ApiKey = "fictional-openrouter-key";

    /// <summary>Fake models plus the models the platform catalogue starts with, so the default policy works against the fake.</summary>
    public static readonly IReadOnlyList<string> Models =
        ["fake/teambeats-small", "fake/teambeats-large", "fake/teambeats-embed", "openai/gpt-4o-mini", "openai/gpt-4o", "openai/text-embedding-3-small"];

    /// <summary>The model of the last chat or embedding request, for tests that check routing.</summary>
    public string? LastModel { get; private set; }

    public bool Unavailable { get; set; }

    public int Requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        if (Unavailable)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        if (request.Headers.Authorization?.Parameter != ApiKey)
        {
            return Error(HttpStatusCode.Unauthorized, "No auth credentials found");
        }
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/v1/models" && request.Method == HttpMethod.Get)
        {
            return Json(new { data = Models.Select(m => new { id = m, name = m, context_length = 32_000 }) });
        }
        if (path == "/api/v1/embeddings" && request.Method == HttpMethod.Post)
        {
            var embedBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            LastModel = embedBody?["model"]?.GetValue<string>();
            var input = embedBody?["input"];
            var texts = input is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? string.Empty).ToList() : [input?.GetValue<string>() ?? string.Empty];
            return Json(new
            {
                data = texts.Select((t, i) => new { index = i, embedding = Embed(t) }),
                usage = new { prompt_tokens = texts.Sum(Tokens), total_tokens = texts.Sum(Tokens) },
            });
        }
        if (path != "/api/v1/chat/completions" || request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var model = body["model"]?.GetValue<string>() ?? Models[0];
        LastModel = model;
        if (!Models.Contains(model))
        {
            return Error(HttpStatusCode.BadRequest, $"{model} is not a valid model ID");
        }
        var messages = body["messages"]?.AsArray() ?? [];
        var prompt = string.Join("\n", messages.Select(m => Text(m?["content"])));
        var last = messages.LastOrDefault(m => m?["role"]?.GetValue<string>() == "user") is { } u ? Text(u["content"]) : string.Empty;

        if (last.Contains("[fake:429]", StringComparison.Ordinal))
        {
            return Error((HttpStatusCode)429, "Rate limit exceeded");
        }
        if (last.Contains("[fake:500]", StringComparison.Ordinal) || last.Contains($"[fake:fail:{model}]", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.InternalServerError, "Provider returned error");
        }
        if (last.Contains("[fake:timeout]", StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        var refused = last.Contains("[fake:refuse]", StringComparison.Ordinal);
        var reply = refused ? string.Empty : Reply(last);
        // Like a grounded model, cite the first passage when the prompt carries numbered passages (spec 014).
        if (!refused && prompt.Contains("<passage n=\"1\"", StringComparison.Ordinal) && !last.Contains("[fake:nocite]", StringComparison.Ordinal))
        {
            reply += " [1]";
        }
        if (!refused && body["response_format"] is not null && !last.Contains("[fake:badjson]", StringComparison.Ordinal))
        {
            reply = JsonSerializer.Serialize(new { answer = reply });
        }
        var tool = ToolMarker().Match(last);
        var toolCalls = tool.Success
            ? new[] { new { id = "call-fake-1", type = "function", function = new { name = tool.Groups[1].Value, arguments = "{}" } } }
            : null;
        var finish = refused ? "content_filter" : "stop";
        var id = "gen-fake-" + Guid.NewGuid().ToString("N")[..12];
        var usage = new { prompt_tokens = Tokens(prompt), completion_tokens = Tokens(reply), total_tokens = Tokens(prompt) + Tokens(reply) };

        if (body["stream"]?.GetValue<bool>() == true)
        {
            var sse = new StringBuilder();
            foreach (var word in reply.Split(' '))
            {
                var chunk = new { id, model, @object = "chat.completion.chunk", choices = new[] { new { index = 0, delta = new { content = word + " " }, finish_reason = (string?)null } } };
                sse.Append("data: ").Append(JsonSerializer.Serialize(chunk)).Append("\n\n");
            }
            if (!last.Contains("[fake:cut]", StringComparison.Ordinal))
            {
                var final = new { id, model, @object = "chat.completion.chunk", choices = new[] { new { index = 0, delta = new { content = string.Empty }, finish_reason = (string?)finish } }, usage };
                sse.Append("data: ").Append(JsonSerializer.Serialize(final)).Append("\n\ndata: [DONE]\n\n");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
        }

        return Json(new
        {
            id,
            model,
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            choices = new[] { new { index = 0, message = new { role = "assistant", content = reply, tool_calls = toolCalls }, finish_reason = toolCalls is null ? finish : "tool_calls" } },
            usage,
        });
    }

    /// <summary>A stable, recognisable answer: tests can assert on it and testers can see it is not a real model.</summary>
    public static string Reply(string question)
    {
        var excerpt = question.Length > 160 ? question[..160] : question;
        return IsArabic(question)
            ? $"هذا رد تجريبي من نموذج وهمي. سؤالك كان: {excerpt}"
            : $"This is a test answer from a fake model. You asked: {excerpt}";
    }

    private static bool IsArabic(string text) => text.Any(c => c is >= '؀' and <= 'ۿ');

    private static int Tokens(string text) => string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Content is a string or an array of parts with <c>text</c>.</summary>
    private static string Text(JsonNode? content) => content switch
    {
        JsonValue v => v.GetValue<string>(),
        JsonArray parts => string.Join(" ", parts.Select(p => p?["text"]?.GetValue<string>()).OfType<string>()),
        _ => string.Empty,
    };

    /// <summary>A deterministic 8-dimension vector, so similar inputs are equal and different inputs differ.</summary>
    /// <summary>Embedding dimensions of the fake: small, and different from the 1536 of the real model, so both paths run.</summary>
    public const int EmbeddingDimensions = 512;

    /// <summary>
    /// A deterministic bag of words: each word adds weight to one dimension picked by its hash, then the vector is scaled
    /// to unit length. Texts that share words are close and texts with none in common are almost orthogonal, so semantic
    /// search behaves predictably in tests without a model.
    /// </summary>
    private static float[] Embed(string text)
    {
        var vector = new float[EmbeddingDimensions];
        foreach (var word in WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value))
        {
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(word));
            vector[BitConverter.ToUInt32(hash, 0) % EmbeddingDimensions] += 1;
        }
        var length = MathF.Sqrt(vector.Sum(v => v * v));
        if (length == 0)
        {
            vector[0] = 1;
            return vector;
        }
        return [.. vector.Select(v => v / length)];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial System.Text.RegularExpressions.Regex WordPattern();

    private static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code = (int)status, message } }), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
