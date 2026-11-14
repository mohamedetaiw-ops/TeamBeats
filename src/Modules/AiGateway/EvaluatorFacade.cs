using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>What a verified evaluator token allows: one tenant, one run, its classification and the model under test.</summary>
public sealed record EvaluatorGrant(Guid TenantId, Guid RunId, DataClassification Classification, string? ModelUnderTest, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints and checks evaluator facade tokens (spec 013 FR-018). The signing key is derived with HKDF from the debug log key
/// in the secret store, so no new secret is needed and every API instance agrees. A token is
/// <c>tbev1.{payload}.{signature}</c>, both base64url; the payload names the tenant, run, classification, model under
/// test and expiry. Nothing about the token is stored.
/// </summary>
public sealed class EvaluatorTokens(IConfiguration configuration, ITenantContext context, IClock clock) : IAiEvaluatorTokens
{
    public const string EnabledSetting = "Ai:EvaluatorFacade:Enabled";
    public const string HostSetting = "Ai:EvaluatorFacade:Host";
    public const string DefaultHost = "ai-gateway.internal";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const string Prefix = "tbev1.";
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("teambeats/ai/evaluator-facade/v1");

    public static bool Enabled(IConfiguration configuration) => configuration.GetValue<bool>(EnabledSetting);

    public static string Host(IConfiguration configuration) => configuration[HostSetting] is { Length: > 0 } host ? host : DefaultHost;

    private byte[]? Key()
    {
        if (configuration[DebugLogProtector.KeySetting] is not { Length: > 0 } value)
        {
            return null;
        }
        try
        {
            var root = Convert.FromBase64String(value);
            return root.Length == 32 ? HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, info: Info) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string? Mint(Guid runId, DataClassification classification, string? modelUnderTest)
    {
        if (!Enabled(configuration) || Key() is not { } key)
        {
            return null;
        }
        var payload = new JsonObject
        {
            ["t"] = context.RequireTenant(),
            ["r"] = runId,
            ["u"] = AiUseCases.Evaluation,
            ["c"] = Classifications.Key(classification),
            ["m"] = modelUnderTest,
            ["e"] = clock.UtcNow.Add(Lifetime).ToUnixTimeSeconds(),
        };
        var body = Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return $"{Prefix}{body}.{Base64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body)))}";
    }

    /// <summary>The grant of a valid, unexpired token, or null for anything else.</summary>
    public EvaluatorGrant? Verify(string? token)
    {
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal) || Key() is not { } key)
        {
            return null;
        }
        var parts = token[Prefix.Length..].Split('.');
        if (parts.Length != 2)
        {
            return null;
        }
        try
        {
            var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[1])))
            {
                return null;
            }
            var payload = JsonNode.Parse(FromBase64Url(parts[0]))!;
            var expires = DateTimeOffset.FromUnixTimeSeconds(payload["e"]!.GetValue<long>());
            if (payload["u"]?.GetValue<string>() != AiUseCases.Evaluation || expires <= clock.UtcNow ||
                Classifications.Parse(payload["c"]?.GetValue<string>()) is not { } level)
            {
                return null;
            }
            return new EvaluatorGrant(payload["t"]!.GetValue<Guid>(), payload["r"]!.GetValue<Guid>(), level, payload["m"]?.GetValue<string>(), expires);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}

/// <summary>
/// The internal OpenAI-compatible facade for sandboxed evaluator workers (spec 013 FR-018, ADR-0015):
/// <c>POST /internal/ai/v1/chat/completions</c>, mapped only when the installation turns it on and answering only on the
/// internal host name. The bearer token sets the tenant; the request runs through <see cref="IAiGateway"/> as an
/// <c>evaluation</c> call with the run's classification, so allowlist, routing, budgets and metering apply as for any judge
/// call. Tools and streaming are refused; a denial answers 403 with the reason as an OpenAI error.
/// </summary>
public static class EvaluatorFacade
{
    public const string Path = "/internal/ai/v1/chat/completions";
    public const int MaxMessages = 50;

    public static void MapEvaluatorFacade(this IEndpointRouteBuilder app)
    {
        var configuration = app.ServiceProvider.GetRequiredService<IConfiguration>();
        if (!EvaluatorTokens.Enabled(configuration))
        {
            return;
        }
        app.MapPost(Path, HandleAsync).RequireHost(EvaluatorTokens.Host(configuration)).ExcludeFromDescription();
    }

    private static IResult Error(int status, string type, string code) =>
        Results.Json(new { error = new { message = code, type, code } }, statusCode: status);

    private static async Task<IResult> HandleAsync(HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        var header = http.Request.Headers.Authorization.ToString();
        var grant = header.StartsWith("Bearer ", StringComparison.Ordinal) ? services.GetRequiredService<EvaluatorTokens>().Verify(header[7..].Trim()) : null;
        if (grant is null || await services.GetRequiredService<ITenantRegistry>().FindByIdAsync(grant.TenantId, ct) is not { Status: TenantLifecycleStatus.Active })
        {
            return Error(StatusCodes.Status401Unauthorized, "invalid_request_error", "invalid_token");
        }

        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request_error", "invalid_json");
        }
        if (body?["stream"]?.GetValue<bool>() == true || body?["tools"] is not null)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request_error", "unsupported");
        }
        var messages = new List<AiMessage>();
        foreach (var m in body?["messages"] as JsonArray ?? [])
        {
            var role = m?["role"]?.GetValue<string>();
            var content = m?["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var text) => text,
                JsonArray parts => string.Join("\n", parts.Where(p => p?["type"]?.GetValue<string>() == "text").Select(p => p?["text"]?.GetValue<string>())),
                _ => null,
            };
            if (role is not (AiRoles.System or AiRoles.User or AiRoles.Assistant) || content is null)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_request_error", "invalid_messages");
            }
            messages.Add(new AiMessage(role, content));
        }
        if (messages.Count is 0 or > MaxMessages)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request_error", "invalid_messages");
        }
        JsonElement? schema = body?["response_format"]?["json_schema"]?["schema"] is { } s ? JsonSerializer.Deserialize<JsonElement>(s.ToJsonString()) : null;

        var tenant = services.GetRequiredService<TenantContextAccessor>();
        tenant.SetTenant(grant.TenantId);
        tenant.SetActor(ActorType.System, null);
        var result = await services.GetRequiredService<IAiGateway>().ChatAsync(new AiRequest(AiUseCases.Evaluation, messages, grant.Classification,
            ResponseSchema: schema, Model: body?["model"]?.GetValue<string>() is { Length: > 0 } model ? model : null,
            MaxTokens: body?["max_tokens"]?.GetValue<int>(), Temperature: body?["temperature"]?.GetValue<double>(), ModelUnderTest: grant.ModelUnderTest), ct);
        if (result.Completion is not { } completion)
        {
            return Error(result.Denial == AiDenials.ProviderUnavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status403Forbidden, "policy_denied",
                result.Denial ?? AiDenials.InvalidRequest);
        }
        return Results.Json(new
        {
            id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = completion.Model,
            choices = new[] { new { index = 0, message = new { role = AiRoles.Assistant, content = completion.Content }, finish_reason = completion.FinishReason } },
            usage = new { prompt_tokens = completion.Usage.InputTokens, completion_tokens = completion.Usage.OutputTokens, total_tokens = completion.Usage.InputTokens + completion.Usage.OutputTokens },
        });
    }
}
