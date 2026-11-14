using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.AiGateway.OpenAiCompatible;

/// <summary>
/// The adapter for a tenant's own OpenAI-compatible endpoint (spec 013 US6, FR-004): vLLM, Azure OpenAI with an
/// OpenAI-compatible route, or any server that speaks chat completions and embeddings. The gateway hands it the target of
/// each call (base address, key read from the secret manager at that moment, and model name); nothing is cached here. Calls
/// go through the outbound guard of <see cref="SafeHttp"/>, so an endpoint cannot point at an internal address unless the
/// installation allows private addresses (dedicated and on-premises installs).
/// </summary>
public sealed class PrivateEndpointProvider(IHttpClientFactory http, IConfiguration configuration) : IModelProvider, IPrivateEndpointProbe
{
    public const string ClientName = "ai-private";
    public const string TimeoutSetting = "Ai:PrivateEndpoints:TimeoutSeconds";
    private const string Label = "Private endpoint";

    public string Key => PrivateEndpoints.ProviderKey;

    /// <summary>Always reachable as a provider; each endpoint has its own circuit and its own test.</summary>
    public bool Reachable => true;

    private TimeSpan Timeout => TimeSpan.FromSeconds(configuration.GetValue<int?>(TimeoutSetting) ?? 60);

    private HttpClient Client => http.CreateClient(ClientName);

    private static ProviderTarget Require(ProviderTarget? target) =>
        target ?? throw new ProviderException("Private endpoint call without a target", retryable: false);

    public async Task<ProviderReply> ChatAsync(ProviderChat request, CancellationToken ct)
    {
        var target = Require(request.Target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client,
            OpenAiWire.Request(target.BaseUrl, "chat/completions", OpenAiWire.ChatBody(request, target.Model, stream: false), target.ApiKey), stream: false, timeout, Label);
        return await OpenAiWire.ReadReplyAsync(response, timeout.Token, Label);
    }

    public async IAsyncEnumerable<ProviderStreamPart> StreamAsync(ProviderChat request, [EnumeratorCancellation] CancellationToken ct)
    {
        var target = Require(request.Target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client,
            OpenAiWire.Request(target.BaseUrl, "chat/completions", OpenAiWire.ChatBody(request, target.Model, stream: true), target.ApiKey), stream: true, timeout, Label);
        await foreach (var part in OpenAiWire.ReadStreamAsync(response, timeout.Token, ct, Label))
        {
            yield return part;
        }
    }

    public async Task<ProviderEmbedding> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct, ProviderTarget? target = null)
    {
        var t = Require(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, OpenAiWire.Request(t.BaseUrl, "embeddings", OpenAiWire.EmbedBody(t.Model, inputs), t.ApiKey),
            stream: false, timeout, Label);
        return await OpenAiWire.ReadEmbeddingsAsync(response, inputs.Count, timeout.Token, Label);
    }

    public async Task<ProviderTranscription> TranscribeAsync(string model, byte[] audio, string mediaType, string? language, CancellationToken ct, ProviderTarget? target = null)
    {
        var t = Require(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, OpenAiWire.TranscriptionRequest(t.BaseUrl, t.Model, audio, mediaType, language, t.ApiKey),
            stream: false, timeout, Label);
        var text = await OpenAiWire.ReadTranscriptionAsync(response, timeout.Token, Label);
        return new ProviderTranscription(text, new AiUsage(0, Math.Max(1, text.Length / 4)));
    }

    public async Task<ProviderSpeech> SpeakAsync(string model, string text, string voice, CancellationToken ct, ProviderTarget? target = null)
    {
        var t = Require(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, OpenAiWire.Request(t.BaseUrl, "audio/speech", OpenAiWire.SpeechBody(t.Model, text, voice), t.ApiKey),
            stream: false, timeout, Label);
        return new ProviderSpeech(await OpenAiWire.ReadSpeechAsync(response, timeout.Token, Label), "audio/mpeg", new AiUsage(Math.Max(1, text.Length / 4), 0));
    }

    /// <summary>
    /// The connection and capability test (US6 scenario 1): the models list answers with the key, every registered model is
    /// listed, and each model answers one small call for what it was registered for (a chat, or an embedding). Details are
    /// resource keys; the key never appears in them.
    /// </summary>
    public async Task<IReadOnlyList<EndpointCheck>> TestAsync(string baseUrl, string apiKey, IReadOnlyList<PrivateModel> models, CancellationToken ct)
    {
        var checks = new List<EndpointCheck>();
        HashSet<string> listed;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(Timeout);
            using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            try
            {
                using var response = await Client.SendAsync(message, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return [new EndpointCheck("connection", null, false, "ai.endpoints.checks.rejected")];
                }
                if (!response.IsSuccessStatusCode)
                {
                    return [new EndpointCheck("connection", null, false, "ai.endpoints.checks.unreachable")];
                }
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                listed = body?["data"] is JsonArray data
                    ? [.. data.Select(d => d?["id"]?.GetValue<string>()).OfType<string>()]
                    : [];
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException
                && !ct.IsCancellationRequested)
            {
                return [new EndpointCheck("connection", null, false, ex is HttpRequestException { InnerException: BlockedAddressException }
                    ? "ai.endpoints.checks.blocked_address" : "ai.endpoints.checks.unreachable")];
            }
        }
        checks.Add(new EndpointCheck("connection", null, true, null));

        foreach (var model in models)
        {
            if (!listed.Contains(model.Name))
            {
                checks.Add(new EndpointCheck("model", model.Name, false, "ai.endpoints.checks.model_missing"));
                continue;
            }
            checks.Add(new EndpointCheck("model", model.Name, true, null));
            var target = new ProviderTarget(baseUrl, apiKey, model.Name);
            // One call per model in the capability it serves. A speech to text model needs real audio, so its listing is its check.
            var kind = model.Capabilities.Contains(ModelCapabilities.Embeddings) ? "embeddings"
                : model.Capabilities.Contains(ModelCapabilities.Chat) ? "chat"
                : model.Capabilities.Contains(ModelCapabilities.Speech) ? "speech"
                : null;
            if (kind is null)
            {
                continue;
            }
            try
            {
                switch (kind)
                {
                    case "embeddings":
                        await EmbedAsync(model.Name, ["ping"], ct, target);
                        break;
                    case "speech":
                        await SpeakAsync(model.Name, "ping", "alloy", ct, target);
                        break;
                    default:
                        await ChatAsync(new ProviderChat(model.Name, [new AiMessage(AiRoles.User, "ping")], null, null, 8, null, target), ct);
                        break;
                }
                checks.Add(new EndpointCheck(kind, model.Name, true, null));
            }
            catch (ProviderException)
            {
                checks.Add(new EndpointCheck(kind, model.Name, false, "ai.endpoints.checks.call_failed"));
            }
        }
        return checks;
    }
}

public static class OpenAiCompatibleModule
{
    /// <summary>Registers the private endpoint adapter and its probe, on a client guarded against internal addresses.</summary>
    public static IServiceCollection AddOpenAiCompatibleAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(PrivateEndpointProvider.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => SafeHttp.CreateHandler(configuration.GetValue<bool>(SafeHttp.AllowPrivateSetting)));
        services.AddSingleton<PrivateEndpointProvider>();
        services.AddSingleton<IModelProvider>(sp => sp.GetRequiredService<PrivateEndpointProvider>());
        services.AddSingleton<IPrivateEndpointProbe>(sp => sp.GetRequiredService<PrivateEndpointProvider>());
        return services;
    }
}
