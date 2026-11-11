using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;

namespace TeamBeats.Modules.Integrations.Google;

/// <summary>
/// Google Drive as a document repository (ADR-0016, spec 017 FR-019, INT-004). Only the shared drives listed in the
/// connection settings are read. Listing, item reads and changes act as the administrator in the settings, who must be
/// a member of each listed drive; opening content acts as the user, so Drive decides whether that user may read it.
/// Google Docs, Sheets and Slides are opened as PDF. No Drive type leaves this class.
/// </summary>
public sealed class GoogleDrive(GoogleApi api, GoogleSignIn signIn, IHttpClientFactory httpClients) : IDocumentRepository
{
    public const long MaxContentBytes = 100L * 1024 * 1024;
    private const string FolderType = "application/vnd.google-apps.folder";
    private const string GoogleTypes = "application/vnd.google-apps.";
    private const string ItemFields = "id,name,mimeType,size,modifiedTime,version,webViewLink,parents,driveId,trashed";

    public async Task<IReadOnlyList<RepositoryContainer>> ContainersAsync(Guid connectionId, CancellationToken ct = default)
    {
        if (await api.LoadAsync(connectionId, GoogleFeatures.Drive, ct) is not { } loaded)
        {
            return [];
        }
        var token = await TokenAsync(loaded, loaded.Settings.AdminEmail!, ct);
        var containers = new List<RepositoryContainer>();
        foreach (var drive in loaded.Settings.SharedDrives ?? [])
        {
            var (body, error, status) = await api.GetAsync(token, $"{GoogleHosts.DriveBase}/drives/{Uri.EscapeDataString(drive)}?fields=id,name", ct);
            if (status == 404)
            {
                continue;
            }
            Throw(loaded, error, loaded.Settings.AdminEmail!);
            containers.Add(new RepositoryContainer(drive, GoogleApi.Text(body, "name") ?? drive));
        }
        return containers;
    }

    public async Task<RepositoryPage> ListAsync(Guid connectionId, string containerId, string? folderId, string? cursor, CancellationToken ct = default)
    {
        var loaded = await RequireAsync(connectionId, containerId, ct);
        var token = await TokenAsync(loaded, loaded.Settings.AdminEmail!, ct);
        var parent = folderId ?? containerId;
        if (!IsId(parent))
        {
            return new RepositoryPage([], null);
        }
        var q = Uri.EscapeDataString($"'{parent}' in parents and trashed = false");
        var url = $"{GoogleHosts.DriveBase}/files?corpora=drive&driveId={Uri.EscapeDataString(containerId)}&includeItemsFromAllDrives=true&supportsAllDrives=true"
            + $"&q={q}&pageSize=200&orderBy=folder,name&fields=nextPageToken,files({ItemFields})"
            + (cursor is null ? "" : $"&pageToken={Uri.EscapeDataString(cursor)}");
        var (body, error, _) = await api.GetAsync(token, url, ct);
        Throw(loaded, error, loaded.Settings.AdminEmail!);
        var items = GoogleApi.Items(body, "files").Select(f => Map(f, containerId)).Where(i => i is not null).Select(i => i!).ToList();
        return new RepositoryPage(items, GoogleApi.Text(body, "nextPageToken"));
    }

    public async Task<RepositoryItem?> GetAsync(Guid connectionId, string itemId, CancellationToken ct = default)
    {
        if (!IsId(itemId) || await api.LoadAsync(connectionId, GoogleFeatures.Drive, ct) is not { } loaded)
        {
            return null;
        }
        var token = await TokenAsync(loaded, loaded.Settings.AdminEmail!, ct);
        var (file, error, status) = await api.GetAsync(token, $"{GoogleHosts.DriveBase}/files/{itemId}?supportsAllDrives=true&fields={ItemFields}", ct);
        if (status == 404)
        {
            return null;
        }
        Throw(loaded, error, loaded.Settings.AdminEmail!);
        if (!Allowed(loaded.Settings, GoogleApi.Text(file, "driveId")) || GoogleApi.True(file, "trashed") || Map(file, GoogleApi.Text(file, "driveId")!) is not { } item)
        {
            return null;
        }
        var permissions = new List<RepositoryPermission>();
        string? page = null;
        do
        {
            var (body, perr, _) = await api.GetAsync(token, $"{GoogleHosts.DriveBase}/files/{itemId}/permissions?supportsAllDrives=true&pageSize=100"
                + "&fields=nextPageToken,permissions(type,role,emailAddress,domain,deleted)" + (page is null ? "" : $"&pageToken={Uri.EscapeDataString(page)}"), ct);
            Throw(loaded, perr, loaded.Settings.AdminEmail!);
            permissions.AddRange(GoogleApi.Items(body, "permissions").Select(MapPermission).Where(p => p is not null).Select(p => p!));
            page = GoogleApi.Text(body, "nextPageToken");
        }
        while (!string.IsNullOrEmpty(page) && permissions.Count < 10_000);
        return item with { Permissions = permissions };
    }

    public async Task<RepositoryChanges> ChangesAsync(Guid connectionId, string containerId, string? cursor, CancellationToken ct = default)
    {
        var loaded = await RequireAsync(connectionId, containerId, ct);
        var token = await TokenAsync(loaded, loaded.Settings.AdminEmail!, ct);
        var drive = Uri.EscapeDataString(containerId);
        if (cursor is null)
        {
            var (start, error, _) = await api.GetAsync(token, $"{GoogleHosts.DriveBase}/changes/startPageToken?driveId={drive}&supportsAllDrives=true", ct);
            Throw(loaded, error, loaded.Settings.AdminEmail!);
            return new RepositoryChanges([], GoogleApi.Text(start, "startPageToken") ?? throw new RepositoryUnavailableException("unreachable"), false);
        }
        var (body, err, _) = await api.GetAsync(token, $"{GoogleHosts.DriveBase}/changes?pageToken={Uri.EscapeDataString(cursor)}&driveId={drive}"
            + $"&includeItemsFromAllDrives=true&supportsAllDrives=true&pageSize=500&fields=nextPageToken,newStartPageToken,changes(fileId,removed,file({ItemFields}))", ct);
        Throw(loaded, err, loaded.Settings.AdminEmail!);
        var changes = new List<RepositoryChange>();
        foreach (var c in GoogleApi.Items(body, "changes"))
        {
            if (GoogleApi.Text(c, "fileId") is not { } id)
            {
                continue;
            }
            var file = c.TryGetProperty("file", out var f) ? f : default;
            var item = GoogleApi.True(c, "removed") || GoogleApi.True(file, "trashed") || GoogleApi.Text(file, "driveId") != containerId ? null : Map(file, containerId);
            changes.Add(new RepositoryChange(id, item is null, item));
        }
        var next = GoogleApi.Text(body, "nextPageToken");
        return next is not null
            ? new RepositoryChanges(changes, next, true)
            : new RepositoryChanges(changes, GoogleApi.Text(body, "newStartPageToken") ?? cursor, false);
    }

    public async Task<RepositoryContent?> OpenAsync(Guid connectionId, string itemId, string userEmail, CancellationToken ct = default)
    {
        if (!IsId(itemId) || string.IsNullOrWhiteSpace(userEmail) || await api.LoadAsync(connectionId, GoogleFeatures.Drive, ct) is not { } loaded)
        {
            return null;
        }
        var user = userEmail.Trim().ToLowerInvariant();
        var token = await signIn.TokenAsync(loaded.Connection, loaded.Settings, user, GoogleHosts.DriveScope, ct);
        if (token.ErrorClass == ConnectorErrorClasses.CredentialRefused)
        {
            // Google refuses to act as someone outside the customer's Workspace: that user may not read the item.
            return null;
        }
        if (!token.Ok)
        {
            throw new RepositoryUnavailableException(Reason(token.ErrorClass));
        }
        var (file, error, status) = await api.GetAsync(token.AccessToken!, $"{GoogleHosts.DriveBase}/files/{itemId}?supportsAllDrives=true&fields={ItemFields}", ct);
        if (status is 403 or 404)
        {
            return null;
        }
        Throw(loaded, error, user);
        if (!Allowed(loaded.Settings, GoogleApi.Text(file, "driveId")) || GoogleApi.True(file, "trashed"))
        {
            return null;
        }
        var type = GoogleApi.Text(file, "mimeType") ?? "application/octet-stream";
        if (type == FolderType)
        {
            return null;
        }
        var native = type.StartsWith(GoogleTypes, StringComparison.Ordinal);
        var url = native
            ? $"{GoogleHosts.DriveBase}/files/{itemId}/export?mimeType={Uri.EscapeDataString("application/pdf")}"
            : $"{GoogleHosts.DriveBase}/files/{itemId}?alt=media&supportsAllDrives=true";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var client = httpClients.CreateClient(SafeHttp.ClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            request.Dispose();
            client.Dispose();
            throw new RepositoryUnavailableException("unreachable");
        }
        var owner = new Owner(response, request, client);
        try
        {
            var code = (int)response.StatusCode;
            if (code is 403 or 404)
            {
                owner.Dispose();
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new RepositoryUnavailableException(code == 429 ? "throttled" : "unreachable");
            }
            var length = response.Content.Headers.ContentLength;
            if (length > MaxContentBytes)
            {
                throw new RepositoryUnavailableException("too_large");
            }
            var name = GoogleApi.Text(file, "name") ?? itemId;
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return new RepositoryContent(stream, native ? "application/pdf" : type, native ? name + ".pdf" : name, length, owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    private sealed class Owner(params IDisposable[] parts) : IDisposable
    {
        public void Dispose()
        {
            foreach (var p in parts)
            {
                p.Dispose();
            }
        }
    }

    private async Task<(PackConnection Connection, GoogleSettings Settings)> RequireAsync(Guid connectionId, string containerId, CancellationToken ct)
    {
        var loaded = await api.LoadAsync(connectionId, GoogleFeatures.Drive, ct) ?? throw new RepositoryUnavailableException("connection_unusable");
        return Allowed(loaded.Settings, containerId) ? loaded : throw new RepositoryUnavailableException("container_not_allowed");
    }

    private async Task<string> TokenAsync((PackConnection Connection, GoogleSettings Settings) loaded, string subject, CancellationToken ct)
    {
        var token = await signIn.TokenAsync(loaded.Connection, loaded.Settings, subject, GoogleHosts.DriveScope, ct);
        return token.Ok ? token.AccessToken! : throw new RepositoryUnavailableException(Reason(token.ErrorClass));
    }

    private void Throw((PackConnection Connection, GoogleSettings Settings) loaded, string? error, string subject)
    {
        if (error is null)
        {
            return;
        }
        if (error == ConnectorErrorClasses.CredentialRefused)
        {
            signIn.Refused(loaded.Connection, loaded.Settings, subject, GoogleHosts.DriveScope);
        }
        throw new RepositoryUnavailableException(Reason(error));
    }

    private static string Reason(string? errorClass) => errorClass switch
    {
        ConnectorErrorClasses.CredentialRefused => "consent_missing",
        ConnectorErrorClasses.CredentialMissing => "credential_missing",
        ConnectorErrorClasses.HostNotAllowed => "host_not_allowed",
        ConnectorErrorClasses.Throttled => "throttled",
        _ => "unreachable",
    };

    private static bool Allowed(GoogleSettings settings, string? driveId) => driveId is not null && settings.SharedDrives?.Contains(driveId) == true;

    /// <summary>Drive IDs are letters, digits, '-' and '_'; anything else is never sent to Google.</summary>
    private static bool IsId(string id) => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static RepositoryItem? Map(JsonElement f, string containerId)
    {
        if (GoogleApi.Text(f, "id") is not { } id || GoogleApi.Text(f, "name") is not { } name)
        {
            return null;
        }
        var type = GoogleApi.Text(f, "mimeType");
        var parent = GoogleApi.Items(f, "parents").Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null).FirstOrDefault(p => p is not null);
        long? size = long.TryParse(GoogleApi.Text(f, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null;
        DateTimeOffset? modified = DateTimeOffset.TryParse(GoogleApi.Text(f, "modifiedTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : null;
        var link = GoogleApi.Text(f, "webViewLink");
        return new RepositoryItem(id, containerId, parent == containerId ? null : parent, name, type == FolderType, type == FolderType ? null : type, size, modified,
            GoogleApi.Text(f, "version"), link is not null && link.StartsWith("https://", StringComparison.Ordinal) ? link : null);
    }

    private static RepositoryPermission? MapPermission(JsonElement p)
    {
        if (GoogleApi.True(p, "deleted"))
        {
            return null;
        }
        var role = GoogleApi.Text(p, "role") switch
        {
            "owner" or "organizer" => "owner",
            "fileOrganizer" or "writer" => "write",
            "commenter" or "reader" => "read",
            _ => null,
        };
        var type = GoogleApi.Text(p, "type");
        if (role is null || type is not ("user" or "group" or "domain" or "anyone"))
        {
            return null;
        }
        return new RepositoryPermission(new RepositoryPrincipal(type, GoogleApi.Text(p, "emailAddress")?.ToLowerInvariant(), GoogleApi.Text(p, "domain")?.ToLowerInvariant()), role);
    }
}
