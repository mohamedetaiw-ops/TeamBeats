using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.AiGateway.OpenAiCompatible;
using TeamBeats.Modules.AiGateway.Contracts;

namespace TeamBeats.Modules.AiGateway.OpenRouter;

/// <summary>
/// The OpenRouter adapter (spec 013 FR-004, ADR-0003): the OpenAI-compatible chat completions and embeddings API, through the
/// shared wire format in AiGateway.OpenAiCompatible. This project is the only place that knows OpenRouter's URL. The platform key is configuration from the
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

    /// <summary>Set on air-gapped installs (spec 013 FR-021): no shared provider is reachable, whatever other settings say.</summary>
    public const string AirGappedSetting = "Ai:AirGapped";

    private bool EgressBlocked => configuration.GetValue<bool>(EgressBlockedSetting) || configuration.GetValue<bool>(AirGappedSetting);

    public string Key => "openrouter";

    private string? ApiKey => configuration[ApiKeySetting] is { Length: > 0 } key ? key : null;

    public bool Reachable => ApiKey is not null && !EgressBlocked;

    /// <summary>A tenant's own key (spec 013 FR-020) reaches OpenRouter wherever the install's egress is open.</summary>
    public bool AcceptsTenantKey => !EgressBlocked;

    /// <summary>The tenant's own key when the call carries one, else the platform key.</summary>
    private string KeyFor(ProviderTarget? target) =>
        target?.ApiKey is { Length: > 0 } own ? own : ApiKey ?? throw new ProviderException("OpenRouter is not configured", retryable: false);

    private string BaseUrl => (configuration[BaseUrlSetting] is { Length: > 0 } url ? url : DefaultBaseUrl).TrimEnd('/');

    private TimeSpan Timeout => TimeSpan.FromSeconds(configuration.GetValue<int?>(TimeoutSetting) ?? 60);

    private const string Label = "OpenRouter";

    private HttpRequestMessage Request(string path, JsonObject body, ProviderTarget? target)
    {
        var message = OpenAiWire.Request(BaseUrl, path, body, KeyFor(target));
        message.Headers.Add("X-Title", "TeamBeats");
        return message;
    }

    private HttpClient Client => http.CreateClient(ClientName);

    public async Task<ProviderReply> ChatAsync(ProviderChat request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, Request("chat/completions", OpenAiWire.ChatBody(request, request.Model, stream: false), request.Target), stream: false, timeout, Label);
        return await OpenAiWire.ReadReplyAsync(response, timeout.Token, Label);
    }

    public async IAsyncEnumerable<ProviderStreamPart> StreamAsync(ProviderChat request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, Request("chat/completions", OpenAiWire.ChatBody(request, request.Model, stream: true), request.Target), stream: true, timeout, Label);
        await foreach (var part in OpenAiWire.ReadStreamAsync(response, timeout.Token, ct, Label))
        {
            yield return part;
        }
    }

    public async Task<ProviderEmbedding> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct, ProviderTarget? target = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, Request("embeddings", OpenAiWire.EmbedBody(model, inputs), target), stream: false, timeout, Label);
        return await OpenAiWire.ReadEmbeddingsAsync(response, inputs.Count, timeout.Token, Label);
    }

    public async Task<ProviderTranscription> TranscribeAsync(string model, byte[] audio, string mediaType, string? language, CancellationToken ct, ProviderTarget? target = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var message = OpenAiWire.TranscriptionRequest(BaseUrl, model, audio, mediaType, language,
            KeyFor(target));
        message.Headers.Add("X-Title", "TeamBeats");
        using var response = await OpenAiWire.SendAsync(Client, message, stream: false, timeout, Label);
        var text = await OpenAiWire.ReadTranscriptionAsync(response, timeout.Token, Label);
        return new ProviderTranscription(text, new AiUsage(0, Math.Max(1, text.Length / 4)));
    }

    public async Task<ProviderSpeech> SpeakAsync(string model, string text, string voice, CancellationToken ct, ProviderTarget? target = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await OpenAiWire.SendAsync(Client, Request("audio/speech", OpenAiWire.SpeechBody(model, text, voice), target), stream: false, timeout, Label);
        return new ProviderSpeech(await OpenAiWire.ReadSpeechAsync(response, timeout.Token, Label), "audio/mpeg", new AiUsage(Math.Max(1, text.Length / 4), 0));
    }
}

/// <summary>
/// Tests a tenant's own OpenRouter key (spec 013 FR-020) by reading the key's own record, which needs a valid key and
/// costs nothing. The key is sent only to OpenRouter and never logged.
/// </summary>
public sealed class OpenRouterKeyProbe(IHttpClientFactory http, IConfiguration configuration) : IProviderKeyProbe
{
    public string Provider => "openrouter";

    public async Task<string?> TestAsync(string apiKey, CancellationToken ct)
    {
        if (configuration.GetValue<bool>(OpenRouterProvider.EgressBlockedSetting) || configuration.GetValue<bool>(OpenRouterProvider.AirGappedSetting))
        {
            return "ai.provider_key.checks.egress_blocked";
        }
        var baseUrl = (configuration[OpenRouterProvider.BaseUrlSetting] is { Length: > 0 } url ? url : OpenRouterProvider.DefaultBaseUrl).TrimEnd('/');
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.GetValue<int?>(OpenRouterProvider.TimeoutSetting) ?? 60));
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/key");
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await http.CreateClient(OpenRouterProvider.ClientName).SendAsync(message, timeout.Token);
            return response.IsSuccessStatusCode ? null
                : response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? "ai.provider_key.checks.rejected"
                : "ai.provider_key.checks.unreachable";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return "ai.provider_key.checks.unreachable";
        }
    }
}

public static class OpenRouterModule
{
    public static IServiceCollection AddOpenRouterAdapter(this IServiceCollection services)
    {
        services.AddHttpClient(OpenRouterProvider.ClientName);
        services.AddSingleton<IModelProvider, OpenRouterProvider>();
        services.AddSingleton<IProviderKeyProbe, OpenRouterKeyProbe>();
        return services;
    }
}
