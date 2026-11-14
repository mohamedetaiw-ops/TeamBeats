using System.Net.Http.Headers;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Google;

/// <summary>Loads a usable Google Workspace connection of the current tenant and calls Google's JSON APIs with a token.</summary>
public sealed class GoogleApi(IDbSession session, ITenantContext context, IEntitlementService entitlements, IHttpClientFactory httpClients)
{
    public const int MaxPages = 2_000;

    /// <summary>The connection when it is a Google Workspace connection that is enabled, in the package and has <paramref name="feature"/> on.</summary>
    public async Task<(PackConnection Connection, GoogleSettings Settings)?> LoadAsync(Guid connectionId, string feature, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            return null;
        }
        var row = await session.QuerySingleOrDefaultAsync(
            "select settings::text from integrations.connection where id = @id and kind = @kind and enabled",
            new { id = connectionId, kind = GooglePack.KindName }, r => r.GetString(0), ct);
        if (row is null)
        {
            return null;
        }
        var json = JsonDocument.Parse(row).RootElement.Clone();
        return GooglePack.Parse(json) is { } s && s.Has(feature)
            ? (new PackConnection(context.RequireTenant(), connectionId, GooglePack.KindName, 1, null, json), s)
            : null;
    }

    /// <summary>The enabled Google Workspace connections with Drive on, while connectors are in the package.</summary>
    public async Task<IReadOnlyList<RepositorySource>> DriveConnectionsAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            return [];
        }
        var rows = await session.QueryAsync("select id, display_name, settings::text from integrations.connection where kind = @kind and enabled order by display_name",
            new { kind = GooglePack.KindName }, r => (Id: r.GetGuid(0), Name: r.GetString(1), Settings: r.GetString(2)), ct);
        return rows.Where(r => GooglePack.Parse(JsonDocument.Parse(r.Settings).RootElement.Clone()) is { } s && s.Has(GoogleFeatures.Drive))
            .Select(r => new RepositorySource(r.Id, GooglePack.KindName, r.Name)).ToList();
    }

    /// <summary>GET a JSON answer of at most 1 MB. Returns the error class instead when Google refuses or fails.</summary>
    public async Task<(JsonElement Body, string? ErrorClass, int Status)> GetAsync(string token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return (default, status switch
                {
                    401 or 403 => ConnectorErrorClasses.CredentialRefused,
                    429 => ConnectorErrorClasses.Throttled,
                    >= 500 => ConnectorErrorClasses.ServerError,
                    _ => ConnectorErrorClasses.RequestRefused,
                }, status);
            }
            return await RestSignIn.ReadJsonAsync(response, ct) is { ValueKind: JsonValueKind.Object } json
                ? (json, null, status)
                : (default, ConnectorErrorClasses.InvalidResponse, status);
        }
        catch (HttpRequestException)
        {
            return (default, ConnectorErrorClasses.NotResponding, 0);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (default, ConnectorErrorClasses.NotResponding, 0);
        }
    }

    public static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool True(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}

/// <summary>
/// Google Workspace users and groups for directory sync (spec 003 US6, spec 017 T019), read through the Admin SDK
/// Directory API as the administrator in the settings. Google has no change feed for users, so every read lists all
/// users and groups and is marked complete; the deletion threshold of spec 003 protects against a short list. Users are
/// keyed by their Google ID, which is the <c>sub</c> of Google sign-in. Google's model is translated here; no Google
/// type leaves this class.
/// </summary>
public sealed class GoogleDirectory(GoogleApi api, GoogleSignIn signIn, IClock clock) : IPackDirectory
{
    public const string Issuer = "https://accounts.google.com";

    public async Task<string?> SourceOfAsync(Guid connectionId, CancellationToken ct = default) =>
        await api.LoadAsync(connectionId, GoogleFeatures.Directory, ct) is null ? null : PackDirectorySources.Google;

    public string IssuerFor(Guid connectionId) => Issuer;

    public async Task<DirectoryChanges> ReadChangesAsync(Guid connectionId, string? usersCursor, string? groupsCursor, CancellationToken ct = default)
    {
        var (connection, settings) = await api.LoadAsync(connectionId, GoogleFeatures.Directory, ct) ?? throw new DirectoryUnavailableException("connection_unusable");
        var token = await signIn.TokenAsync(connection, settings, settings.AdminEmail!, GoogleHosts.DirectoryScope, ct);
        if (!token.Ok)
        {
            throw new DirectoryUnavailableException(Reason(token.ErrorClass));
        }
        var customer = Uri.EscapeDataString(settings.CustomerId ?? "my_customer");

        var raw = new List<JsonElement>();
        await PagesAsync(connection, settings, token.AccessToken!, $"{GoogleHosts.DirectoryBase}/users?customer={customer}&projection=basic&maxResults=500", "users",
            raw.Add, ct);
        // The manager is given by email; it is passed on as the manager's directory ID.
        var byEmail = raw.Where(u => GoogleApi.Text(u, "primaryEmail") is not null && GoogleApi.Text(u, "id") is not null)
            .GroupBy(u => GoogleApi.Text(u, "primaryEmail")!.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => GoogleApi.Text(g.First(), "id")!, StringComparer.Ordinal);
        var users = raw.Where(u => GoogleApi.Text(u, "id") is not null).Select(u => MapUser(u, byEmail)).ToList();

        var groups = new List<DirectoryGroupRecord>();
        var listed = new List<(string Id, string? Name)>();
        await PagesAsync(connection, settings, token.AccessToken!, $"{GoogleHosts.DirectoryBase}/groups?customer={customer}&maxResults=200", "groups",
            g =>
            {
                if (GoogleApi.Text(g, "id") is { } id)
                {
                    listed.Add((id, GoogleApi.Text(g, "name")));
                }
            }, ct);
        foreach (var (id, name) in listed)
        {
            var members = new List<string>();
            await PagesAsync(connection, settings, token.AccessToken!, $"{GoogleHosts.DirectoryBase}/groups/{Uri.EscapeDataString(id)}/members?maxResults=200", "members",
                m =>
                {
                    // Only user members; nested groups are not expanded, as in Entra ID.
                    if (GoogleApi.Text(m, "type") == "USER" && GoogleApi.Text(m, "id") is { } member)
                    {
                        members.Add(member);
                    }
                }, ct);
            groups.Add(new DirectoryGroupRecord(id, false, name ?? "", members, [], MembersComplete: true));
        }

        var at = clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return new DirectoryChanges(users, groups, at, at, Complete: true);
    }

    private async Task PagesAsync(PackConnection connection, GoogleSettings settings, string token, string url, string field, Action<JsonElement> each, CancellationToken ct)
    {
        string? page = null;
        for (var n = 0; n < GoogleApi.MaxPages; n++)
        {
            var (body, error, _) = await api.GetAsync(token, page is null ? url : $"{url}&pageToken={Uri.EscapeDataString(page)}", ct);
            if (error is not null)
            {
                if (error == ConnectorErrorClasses.CredentialRefused)
                {
                    signIn.Refused(connection, settings, settings.AdminEmail!, GoogleHosts.DirectoryScope);
                }
                throw new DirectoryUnavailableException(Reason(error));
            }
            foreach (var item in GoogleApi.Items(body, field))
            {
                each(item);
            }
            page = GoogleApi.Text(body, "nextPageToken");
            if (string.IsNullOrEmpty(page))
            {
                return;
            }
        }
        throw new DirectoryUnavailableException("too_large");
    }

    private static string Reason(string? errorClass) => errorClass switch
    {
        ConnectorErrorClasses.CredentialRefused => "consent_missing",
        ConnectorErrorClasses.CredentialMissing => "credential_missing",
        ConnectorErrorClasses.HostNotAllowed => "host_not_allowed",
        ConnectorErrorClasses.Throttled => "throttled",
        _ => "unreachable",
    };

    private static DirectoryUserRecord MapUser(JsonElement u, IReadOnlyDictionary<string, string> idsByEmail)
    {
        var name = u.TryGetProperty("name", out var n) ? GoogleApi.Text(n, "fullName") : null;
        var email = GoogleApi.Text(u, "primaryEmail");
        var org = Primary(u, "organizations");
        var phones = GoogleApi.Items(u, "phones").ToList();
        string? Phone(string type) => phones.Where(p => GoogleApi.Text(p, "type") == type).Select(p => GoogleApi.Text(p, "value")).FirstOrDefault();
        var employeeId = GoogleApi.Items(u, "externalIds").Where(x => GoogleApi.Text(x, "type") == "organization").Select(x => GoogleApi.Text(x, "value")).FirstOrDefault();
        var managerEmail = GoogleApi.Items(u, "relations").Where(x => GoogleApi.Text(x, "type") == "manager").Select(x => GoogleApi.Text(x, "value")).FirstOrDefault();
        var location = Primary(u, "locations");
        var language = GoogleApi.Items(u, "languages").Select(l => GoogleApi.Text(l, "languageCode")).FirstOrDefault(l => l is not null);
        var enabled = !GoogleApi.True(u, "suspended") && !GoogleApi.True(u, "archived");
        return new DirectoryUserRecord(
            GoogleApi.Text(u, "id")!,
            false,
            enabled,
            name ?? "",
            email ?? "",
            email ?? "",
            GoogleApi.Text(org, "title") ?? "",
            GoogleApi.Text(location, "buildingId") ?? GoogleApi.Text(org, "location") ?? "",
            Phone("work") ?? "",
            employeeId ?? "",
            language ?? "",
            CompanyName: GoogleApi.Text(org, "name") ?? "",
            Department: GoogleApi.Text(org, "department") ?? "",
            Division: GoogleApi.Text(org, "costCenter") ?? "",
            WorkPhone: Phone("work") ?? "",
            MobilePhone: Phone("mobile") ?? "",
            ManagerId: managerEmail is not null && idsByEmail.TryGetValue(managerEmail.ToLowerInvariant(), out var manager) ? manager : "");
    }

    /// <summary>The entry marked primary, or the first.</summary>
    private static JsonElement Primary(JsonElement u, string field)
    {
        var items = GoogleApi.Items(u, field).ToList();
        return items.FirstOrDefault(i => GoogleApi.True(i, "primary"), items.FirstOrDefault());
    }
}
