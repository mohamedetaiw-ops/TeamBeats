using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using TeamBeats.Modules.AiGateway.Contracts;

namespace TeamBeats.Modules.AiGateway.OpenAiCompatible;

/// <summary>
/// Models hosted inside the install network (spec 013 R3, FR-021): an OpenAI-compatible server the operator of an
/// air-gapped or on-premises install runs (vLLM, for example), at <c>Ai:Local:BaseUrl</c> with an optional key at
/// <c>Ai:Local:ApiKey</c>. The platform operator lists its models in the catalogue as <c>local/{model}</c>. The address is
/// install configuration, never tenant input, so the client may reach private addresses.
/// </summary>
public sealed class LocalModelProvider(IHttpClientFactory http, IConfiguration configuration) : IModelProvider
{
    public const string ProviderKey = "local";
    public const string ClientName = "ai-local";
    public const string BaseUrlSetting = "Ai:Local:BaseUrl";
    public const string ApiKeySetting = "Ai:Local:ApiKey";
    public const string TimeoutSetting = "Ai:Local:TimeoutSeconds";
    private const string Label = "Hosted model server";

    public string Key => ProviderKey;

    private string? BaseUrl => configuration[BaseUrlSetting] is { Length: > 0 } url ? url.TrimEnd('/') : null;

    /// <summary>Reachable once the operator configured the server; it is inside the install network, so egress rules do not apply.</summary>
    public bool Reachable => BaseUrl is not null;

    private TimeSpan Timeout => TimeSpan.FromSeconds(configuration.GetValue<int?>(TimeoutSetting) ?? 120);

    private HttpClient Client => http.CreateClient(ClientName);

    /// <summary>The server's own name of a catalogue model <c>local/{model}</c>.</summary>
    public static string ServerModel(string model) => model.StartsWith(ProviderKey + "/", StringComparison.Ordinal) ? model[(ProviderKey.Length + 1)..] : model;

    private ProviderTarget Target(string model) =>
        new(BaseUrl ?? throw new ProviderException("No hosted model server is configured", retryable: false), configuration[ApiKeySetting] is { Length: > 0 } key ? key : "none", ServerModel(model));

    public async Task<ProviderReply> ChatAsync(ProviderChat request, CancellationToken ct)
    {
        var t = Target(request.Model);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, OpenAiWire.Request(t.BaseUrl, "chat/completions", OpenAiWire.ChatBody(request, t.Model, stream: false), t.ApiKey),
            stream: false, timeout, Label);
        return await OpenAiWire.ReadReplyAsync(response, timeout.Token, Label);
    }

    public async IAsyncEnumerable<ProviderStreamPart> StreamAsync(ProviderChat request, [EnumeratorCancellation] CancellationToken ct)
    {
        var t = Target(request.Model);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, OpenAiWire.Request(t.BaseUrl, "chat/completions", OpenAiWire.ChatBody(request, t.Model, stream: true), t.ApiKey),
            stream: true, timeout, Label);
        await foreach (var part in OpenAiWire.ReadStreamAsync(response, timeout.Token, ct, Label))
        {
            yield return part;
        }
    }

    public async Task<ProviderEmbedding> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct, ProviderTarget? target = null)
    {
        var t = Target(model);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, OpenAiWire.Request(t.BaseUrl, "embeddings", OpenAiWire.EmbedBody(t.Model, inputs), t.ApiKey), stream: false, timeout, Label);
        return await OpenAiWire.ReadEmbeddingsAsync(response, inputs.Count, timeout.Token, Label);
    }

    public async Task<ProviderTranscription> TranscribeAsync(string model, byte[] audio, string mediaType, string? language, CancellationToken ct, ProviderTarget? target = null)
    {
        var t = Target(model);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, OpenAiWire.TranscriptionRequest(t.BaseUrl, t.Model, audio, mediaType, language, t.ApiKey), stream: false, timeout, Label);
        var text = await OpenAiWire.ReadTranscriptionAsync(response, timeout.Token, Label);
        return new ProviderTranscription(text, new AiUsage(0, Math.Max(1, text.Length / 4)));
    }

    public async Task<ProviderSpeech> SpeakAsync(string model, string text, string voice, CancellationToken ct, ProviderTarget? target = null)
    {
        var t = Target(model);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, OpenAiWire.Request(t.BaseUrl, "audio/speech", OpenAiWire.SpeechBody(t.Model, text, voice), t.ApiKey), stream: false, timeout, Label);
        return new ProviderSpeech(await OpenAiWire.ReadSpeechAsync(response, timeout.Token, Label), "audio/mpeg", new AiUsage(Math.Max(1, text.Length / 4), 0));
    }
}
