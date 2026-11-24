using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Sms;

/// <summary>
/// Settings of an SMS gateway connection: the provider's REST address (on the tenant allowlist), its API key as a secret
/// manager reference, and the sender name or number shown on the phone. <paramref name="ExpiresOn"/> is the key's expiry date.
/// </summary>
public sealed record SmsSettings(string BaseUrl, string? ApiKeyRef, string? Sender, string? ExpiresOn = null)
{
    public RestSettings Rest => new(BaseUrl, new RestAuth("bearer", SecretRef: ApiKeyRef, ExpiresOn: ExpiresOn));
}

/// <summary>
/// The generic SMS gateway pack (spec 015 US6 scenario 2, FR-019, FR-020; the default of spec 015 section 17): one
/// action that posts <c>{ to, from, text, reference }</c> to <c>{baseUrl}/messages</c> with the API key as a bearer
/// token. The provider is chosen per tenant; any gateway with that shape, or a small relay in front of one, works. The
/// action declares no inputs, so it is never a route step or an agent tool: only notification delivery calls it. Its
/// input (the number and the text) is never kept in run history.
/// </summary>
public sealed class SmsPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : IConnectorPack
{
    public const string KindName = "sms";
    public const string SendSms = "send_sms";

    /// <summary>The gateway refused the number (400 or 422): no retry, the delivery says why.</summary>
    public const string NumberRefused = "number_refused";

    /// <summary>A single SMS segment of 160 GSM characters; longer text is cut before sending.</summary>
    public const int MaxText = 160;

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.NotificationsSms;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new(SendSms, ConnectorSideEffects.ExternalEffect, false, [NumberRefused], KeepInput: false),
    ];

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (Parse(call.Connection.Settings) is not { ApiKeyRef: not null } settings)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        if (call.Action != SendSms)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        if (Text(call.Input, "to") is not { } to || !PhoneNumbers.IsE164(to) || Text(call.Input, "text") is not { Length: > 0 and <= MaxText } text)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
        }
        var baseUri = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        var uri = new Uri(baseUri, "messages");
        if (uri.Scheme != Uri.UriSchemeHttps || !await signIn.IsAllowedAsync(baseUri, ct))
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { to, from = settings.Sender, text, reference = call.IdempotencyKey }), Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (await signIn.AuthorizeAsync(call.Connection, settings.Rest, request, ct) is { } refused)
        {
            return refused;
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (signIn.MapStatus(call.Connection, settings.Rest, response, new Dictionary<int, string> { [400] = NumberRefused, [422] = NumberRefused }) is { } failure)
        {
            return failure;
        }
        var body = await RestSignIn.ReadJsonAsync(response, ct);
        var id = body is { ValueKind: JsonValueKind.Object } b && Text(b, "id") is { Length: > 0 and <= 64 } found ? found : null;
        // The phone number never goes into run history: the output names the message only.
        return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { messageId = id }, JsonDefaults.Options), id);
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static SmsSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object && settings.Deserialize<SmsSettings>(JsonDefaults.Options) is { BaseUrl: not null } s ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DateOnly? CredentialExpiry(JsonElement settings) => RestSignIn.ExpiresOn(Parse(settings)?.Rest.Auth);

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.custom_rest.settings");
        var errors = new ValidationErrors();
        var rest = await signIn.ValidateAsync(tenantId, s.BaseUrl, new RestAuth("bearer", SecretRef: s.ApiKeyRef, ExpiresOn: s.ExpiresOn), ["bearer"], errors, ct);
        // A sender is an alphanumeric name of up to 11 characters or a number in international form.
        var sender = s.Sender?.Trim();
        if (string.IsNullOrEmpty(sender) || !(PhoneNumbers.IsE164(sender) || sender.Length <= 11 && sender.All(char.IsAsciiLetterOrDigit)))
        {
            errors.Add("sender", "validation.sms.sender_invalid");
        }
        var renamed = new ValidationErrors();
        foreach (var e in errors.Entries)
        {
            renamed.Add(e.Field == "auth.secretRef" ? "apiKeyRef" : e.Field, e.Key);
        }
        renamed.ThrowIfAny();
        return JsonSerializer.SerializeToElement(new SmsSettings(rest!.BaseUrl, s.ApiKeyRef, sender, s.ExpiresOn), JsonDefaults.Options);
    }

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { ApiKeyRef: not null } settings)
        {
            return [new PackCheck("settings", false, "integrations.test.settings_invalid", ConnectorErrorClasses.RequestRefused)];
        }
        // A test never sends a message: it checks the address, the allowlist and that the key can be read.
        return await signIn.TestAsync(connection, settings.Rest, ct);
    }
}

/// <summary>Phone numbers in E.164 form: a plus, then 8 to 15 digits, the first not zero.</summary>
public static class PhoneNumbers
{
    public static bool IsE164(string value) =>
        value.Length is >= 9 and <= 16 && value[0] == '+' && value[1] is >= '1' and <= '9' && value.Skip(1).All(char.IsAsciiDigit);

    /// <summary>A directory phone as people type it (spaces, dashes, brackets) in E.164 form, or null.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        var candidate = trimmed.StartsWith('+') ? "+" + digits : trimmed.StartsWith("00", StringComparison.Ordinal) && digits.Length > 2 ? "+" + digits[2..] : null;
        return candidate is not null && IsE164(candidate) ? candidate : null;
    }
}
