using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Google;

/// <summary>What a Google Workspace connection is used for.</summary>
public static class GoogleFeatures
{
    /// <summary>Users and groups for directory sync (spec 003 US6).</summary>
    public const string Directory = "directory";

    /// <summary>Shared drives through the document repository contract (ADR-0016, INT-004).</summary>
    public const string Drive = "drive";

    public static readonly string[] All = [Directory, Drive];
}

/// <summary>
/// Settings of a Google Workspace connection: the customer's primary domain, the administrator the directory is read as,
/// the service account with domain-wide delegation and its private key as a secret manager reference (the PEM key, or
/// the key file Google gives), the features in use, and for Drive the shared drives TeamBeats may read. TeamBeats never
/// reads a drive that is not listed, whatever the delegation allows.
/// </summary>
public sealed record GoogleSettings(
    string? Domain,
    string? AdminEmail,
    string? ServiceAccountEmail,
    string? PrivateKeyRef,
    IReadOnlyList<string>? Features,
    IReadOnlyList<string>? SharedDrives = null,
    string? CustomerId = null,
    string? ExpiresOn = null)
{
    public bool Has(string feature) => Features?.Contains(feature) == true;
}

/// <summary>
/// The Google Workspace pack (spec 017 T019): an extension point for Google Workspace through the same canonical
/// contracts as Microsoft 365. Directory sync reads users and groups through <see cref="GoogleDirectory"/>; Drive is a
/// document repository (<see cref="GoogleDrive"/>). The pack declares no actions, so nothing here is a route step or
/// an agent tool; Test connection checks each host, the key and a token for each feature.
/// </summary>
public sealed partial class GooglePack(ITenantContext context, GoogleSignIn signIn, IHttpClientFactory httpClients) : IConnectorPack
{
    public const string KindName = "google_workspace";
    public const int MaxSharedDrives = 50;

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationConnectors;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } = [];

    public Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct) => Task.FromResult(PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction));

    public static GoogleSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object ? settings.Deserialize<GoogleSettings>(JsonDefaults.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DateOnly? CredentialExpiry(JsonElement settings) => RestSignIn.ExpiresOn(new RestAuth("bearer", ExpiresOn: Parse(settings)?.ExpiresOn));

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"^[a-z0-9._%+'-]{1,64}@([a-z0-9-]+\.)+[a-z]{2,63}$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{4,28}[a-z0-9]@[a-z][a-z0-9-]{4,28}[a-z0-9]\.iam\.gserviceaccount\.com$")]
    private static partial Regex ServiceAccountPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex DriveIdPattern();

    [GeneratedRegex(@"^(my_customer|C[A-Za-z0-9]{6,20})$")]
    private static partial Regex CustomerPattern();

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.custom_rest.settings");
        var errors = new ValidationErrors();
        var domain = s.Domain?.Trim().ToLowerInvariant();
        if (domain is null || !DomainPattern().IsMatch(domain))
        {
            errors.Add("domain", "validation.google.domain");
        }
        var admin = s.AdminEmail?.Trim().ToLowerInvariant();
        if (admin is null || !EmailPattern().IsMatch(admin) || domain is not null && !admin.EndsWith("@" + domain, StringComparison.Ordinal))
        {
            errors.Add("adminEmail", "validation.google.admin_email");
        }
        var account = s.ServiceAccountEmail?.Trim().ToLowerInvariant();
        if (account is null || !ServiceAccountPattern().IsMatch(account))
        {
            errors.Add("serviceAccountEmail", "validation.google.service_account");
        }
        if (SecretReferences.NameFor(tenantId, s.PrivateKeyRef) is null)
        {
            errors.Add("privateKeyRef", "validation.custom_rest.secret_ref");
        }
        var customer = string.IsNullOrWhiteSpace(s.CustomerId) ? "my_customer" : s.CustomerId.Trim();
        if (!CustomerPattern().IsMatch(customer))
        {
            errors.Add("customerId", "validation.google.customer_id");
        }
        var features = (s.Features ?? []).Select(f => f.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (features.Count == 0 || features.Any(f => !GoogleFeatures.All.Contains(f)))
        {
            errors.Add("features", "validation.google.features");
        }
        var drives = (s.SharedDrives ?? []).Select(d => d.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (features.Contains(GoogleFeatures.Drive) && (drives.Count is 0 or > MaxSharedDrives || drives.Any(d => !DriveIdPattern().IsMatch(d))))
        {
            errors.Add("sharedDrives", "validation.google.shared_drives");
        }
        foreach (var host in GoogleHosts.For(features.Where(GoogleFeatures.All.Contains)))
        {
            if (!await signIn.IsAllowedAsync(host, ct))
            {
                errors.Add("hosts", "validation.google.host_not_allowed");
                break;
            }
        }
        if (s.ExpiresOn is not null && RestSignIn.ExpiresOn(new RestAuth("bearer", ExpiresOn: s.ExpiresOn)) is null)
        {
            errors.Add("expiresOn", "validation.custom_rest.expires_on");
        }
        errors.ThrowIfAny();
        return JsonSerializer.SerializeToElement(new GoogleSettings(domain, admin, account, s.PrivateKeyRef, [.. GoogleFeatures.All.Where(features.Contains)],
            features.Contains(GoogleFeatures.Drive) ? drives : [], customer, s.ExpiresOn), JsonDefaults.Options);
    }

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        var checks = new List<PackCheck>();
        if (Parse(connection.Settings) is not { } s)
        {
            return checks;
        }
        var features = GoogleFeatures.All.Where(s.Has).ToList();
        var hostsOk = true;
        foreach (var host in GoogleHosts.For(features))
        {
            hostsOk &= await signIn.IsAllowedAsync(host, ct);
        }
        checks.Add(new PackCheck("hosts", hostsOk, hostsOk ? "integrations.test.host_allowed" : "integrations.test.host_not_allowed", hostsOk ? null : ConnectorErrorClasses.HostNotAllowed));
        var (key, email) = await signIn.KeyAsync(connection.TenantId, s, ct);
        using (key)
        {
            var keyOk = key is not null && (email is null || string.Equals(email, s.ServiceAccountEmail, StringComparison.OrdinalIgnoreCase));
            checks.Add(new PackCheck("credential", keyOk, keyOk ? "integrations.test.credential_found" : "integrations.test.google_key_invalid",
                keyOk ? null : ConnectorErrorClasses.CredentialMissing));
            if (!keyOk || !hostsOk)
            {
                return checks;
            }
        }
        if (s.Has(GoogleFeatures.Directory))
        {
            var token = await signIn.TokenAsync(connection, s, s.AdminEmail!, GoogleHosts.DirectoryScope, ct, fresh: true);
            checks.Add(token.Ok
                ? new PackCheck("directory", true, "integrations.test.token_ok")
                : new PackCheck("directory", false, token.ErrorClass == ConnectorErrorClasses.NotResponding ? "integrations.test.not_responding" : "integrations.test.google_delegation_refused",
                    token.ErrorClass));
        }
        if (s.Has(GoogleFeatures.Drive))
        {
            var token = await signIn.TokenAsync(connection, s, s.AdminEmail!, GoogleHosts.DriveScope, ct, fresh: true);
            if (!token.Ok)
            {
                checks.Add(new PackCheck("drive", false, token.ErrorClass == ConnectorErrorClasses.NotResponding ? "integrations.test.not_responding" : "integrations.test.google_delegation_refused",
                    token.ErrorClass));
                return checks;
            }
            var missing = 0;
            foreach (var drive in s.SharedDrives ?? [])
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{GoogleHosts.DriveBase}/drives/{Uri.EscapeDataString(drive)}?fields=id");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                using var client = httpClients.CreateClient(SafeHttp.ClientName);
                try
                {
                    using var response = await client.SendAsync(request, ct);
                    missing += response.IsSuccessStatusCode ? 0 : 1;
                }
                catch (HttpRequestException)
                {
                    checks.Add(new PackCheck("drive", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding));
                    return checks;
                }
            }
            checks.Add(missing == 0
                ? new PackCheck("drive", true, "integrations.test.google_drives_ok")
                : new PackCheck("drive", false, "integrations.test.google_drives_missing", ConnectorErrorClasses.RequestRefused));
        }
        return checks;
    }
}
