using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>An allowed SharePoint site of a connection (spec 011 FR-025).</summary>
public sealed record SharePointSiteView(string SiteId, string Name, string WebUrl, DateTimeOffset AddedAt);

public sealed record AddSharePointSiteRequest(string? Url);

/// <summary>A Microsoft 365 connection usable for SharePoint documents: its directory and the sites it allows.</summary>
internal sealed record SharePointConnection(Guid Id, string ExternalTenantId, IReadOnlyList<(string Id, string Name)> Sites);

/// <summary>
/// The allowed sites of the SharePoint documents feature (spec 011 US9 scenario 1, FR-025). The integration
/// administrator names a site by its address; TeamBeats resolves it with the application's own access, so a site the
/// customer has not granted to the application (Sites.Selected) is refused. Only allowed sites are ever read, even
/// when Microsoft grants more.
/// </summary>
public sealed class SharePointSites(IDbSession session, ITenantContext context, IMicrosoftGraph graph, IAuditWriter audit, IOutbox outbox, IClock clock)
{
    public const int MaxSites = 50;

    public async Task<IReadOnlyList<SharePointSiteView>> ListAsync(Guid connectionId, CancellationToken ct)
    {
        await ConnectionAsync(connectionId, ct);
        return await session.QueryAsync(
            "select site_id, display_name, web_url, added_at from integrations.sharepoint_site where connection_id = @c order by display_name, site_id",
            new { c = connectionId }, r => new SharePointSiteView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)), ct);
    }

    public async Task<SharePointSiteView> AddAsync(Guid connectionId, AddSharePointSiteRequest request, CancellationToken ct)
    {
        var external = await ConnectionAsync(connectionId, ct);
        var (host, path) = Parse(request.Url) ?? throw AppException.Validation("url", "validation.integrations.sharepoint_url");
        if (!await session.ScalarAsync<bool>(
                "select exists (select 1 from integrations.connection_feature where connection_id = @c and feature = @f and enabled and consent_status = 'granted')",
                new { c = connectionId, f = Microsoft365Features.SharePointDocuments }, ct))
        {
            throw AppException.Conflict("conflict.integrations.sharepoint_consent");
        }
        if (await session.ScalarAsync<long>("select count(*) from integrations.sharepoint_site where connection_id = @c", new { c = connectionId }, ct) >= MaxSites)
        {
            throw AppException.Validation("url", "validation.integrations.sharepoint_sites_max");
        }
        JsonElement? site;
        try
        {
            var token = await graph.AcquireAppTokenAsync(external, ct);
            site = await graph.AppGetAsync(token, $"sites/{host}:{path}?$select=id,displayName,webUrl", ct);
        }
        catch (GraphConsentMissingException)
        {
            site = null;
        }
        catch (GraphUnavailableException)
        {
            throw AppException.Conflict("conflict.integrations.sharepoint_unreachable");
        }
        if (site is not { } s || Text(s, "id") is not { } siteId)
        {
            // Not there, or the customer has not granted this site to the TeamBeats application.
            throw AppException.Validation("url", "validation.integrations.sharepoint_site_not_granted");
        }
        var name = Text(s, "displayName") ?? path;
        var webUrl = Text(s, "webUrl") is { } w && w.StartsWith("https://", StringComparison.Ordinal) ? w : $"https://{host}{path}";
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into integrations.sharepoint_site (tenant_id, connection_id, site_id, display_name, web_url, added_by, added_at)
            values (platform.current_tenant_id(), @c, @siteId, @name, @webUrl, @user, @now)
            on conflict (tenant_id, connection_id, site_id) do nothing
            """, new { c = connectionId, siteId, name, webUrl, user = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionSiteAllowed, "connection", connectionId.ToString(), Changes: new { site = siteId, name }), ct);
        return new SharePointSiteView(siteId, name, webUrl, now);
    }

    public async Task RemoveAsync(Guid connectionId, string siteId, CancellationToken ct)
    {
        await ConnectionAsync(connectionId, ct);
        var removed = await session.ExecAsync("delete from integrations.sharepoint_site where connection_id = @c and site_id = @siteId",
            new { c = connectionId, siteId }, ct);
        if (removed == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionSiteRemoved, "connection", connectionId.ToString(), Changes: new { site = siteId }), ct);
        // Connected libraries of the site's document libraries are archived and their synced copy removed (spec 011 data model).
        await outbox.EnqueueAsync(new DocumentContainersRemoved(connectionId), ct);
    }

    private async Task<string> ConnectionAsync(Guid connectionId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select external_tenant_id from integrations.connection where id = @id and kind = @k",
            new { id = connectionId, k = ConnectionKinds.Microsoft365 }, r => r.GetString(0), ct) ?? throw AppException.NotFound();

    /// <summary>
    /// A SharePoint Online site address: https, a <c>*.sharepoint.com</c> host, and the root or a <c>/sites/</c> or
    /// <c>/teams/</c> path. Anything else is never sent to Microsoft.
    /// </summary>
    internal static (string Host, string Path)? Parse(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
        {
            return null;
        }
        var host = uri.Host.ToLowerInvariant();
        if (!host.EndsWith(".sharepoint.com", StringComparison.Ordinal) || host.Length <= ".sharepoint.com".Length
            || !host[..^".sharepoint.com".Length].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return null;
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return (host, "/");
        }
        if (segments.Length < 2 || segments[0].ToLowerInvariant() is not ("sites" or "teams")
            || !segments[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return null;
        }
        return (host, $"/{segments[0].ToLowerInvariant()}/{segments[1]}");
    }

    internal static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// SharePoint Online as a document repository (ADR-0016, spec 011 US9). Containers are the document libraries of the
/// allowed sites. Listing, item reads with permissions and changes use the application's own access to those sites
/// (Sites.Selected); listing for a person and opening content use that person's delegated access, so SharePoint's own
/// permissions decide what they see (FR-027). Item IDs are the drive ID and the item ID joined by <c>~</c>. No Graph type
/// leaves this class.
/// </summary>
public sealed class SharePointLibraries(
    IDbSession session,
    IEntitlementService entitlements,
    IMicrosoftGraph graph,
    DelegatedAccessService access,
    IMemoryCache cache,
    ITenantContext context) : IDocumentRepository
{
    public const long MaxContentBytes = 250L * 1024 * 1024;
    private const string ItemSelect = "id,name,folder,file,size,lastModifiedDateTime,eTag,webUrl,parentReference,sensitivityLabel";

    /// <summary>The longest file or folder name SharePoint keeps; longer names are refused before Microsoft is called.</summary>
    public const int MaxNameLength = 250;
    private static readonly TimeSpan DrivesFreshness = TimeSpan.FromMinutes(5);

    internal async Task<SharePointConnection?> LoadAsync(Guid connectionId, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationMicrosoft365, ct))
        {
            return null;
        }
        var external = await session.QuerySingleOrDefaultAsync(
            """
            select c.external_tenant_id from integrations.connection c
            join integrations.connection_feature f on f.connection_id = c.id
            where c.id = @id and c.kind = @k and c.enabled and c.status <> 'disabled' and f.feature = @f and f.enabled and f.consent_status = 'granted'
            """, new { id = connectionId, k = ConnectionKinds.Microsoft365, f = Microsoft365Features.SharePointDocuments }, r => r.GetString(0), ct);
        if (external is null)
        {
            return null;
        }
        var sites = await session.QueryAsync("select site_id, display_name from integrations.sharepoint_site where connection_id = @c order by display_name",
            new { c = connectionId }, r => (r.GetString(0), r.GetString(1)), ct);
        return new SharePointConnection(connectionId, external, sites);
    }

    public async Task<IReadOnlyList<RepositorySource>> SourcesAsync(CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationMicrosoft365, ct))
        {
            return [];
        }
        return await session.QueryAsync(
            """
            select c.id, c.display_name from integrations.connection c
            join integrations.connection_feature f on f.connection_id = c.id
            where c.kind = @k and c.enabled and c.status <> 'disabled' and f.feature = @f and f.enabled and f.consent_status = 'granted'
            order by c.display_name
            """, new { k = ConnectionKinds.Microsoft365, f = Microsoft365Features.SharePointDocuments },
            r => new RepositorySource(r.GetGuid(0), ConnectionKinds.Microsoft365, r.GetString(1)), ct);
    }

    public async Task<IReadOnlyList<RepositoryContainer>> ContainersAsync(Guid connectionId, CancellationToken ct = default)
    {
        if (await LoadAsync(connectionId, ct) is not { } connection)
        {
            return [];
        }
        return (await DrivesAsync(connection, ct)).Select(d => new RepositoryContainer(d.Key, d.Value)).ToList();
    }

    /// <summary>The document libraries of the allowed sites, by drive ID, kept for a few minutes per connection.</summary>
    private async Task<IReadOnlyDictionary<string, string>> DrivesAsync(SharePointConnection connection, CancellationToken ct)
    {
        var key = $"sharepoint-drives:{context.RequireTenant()}:{connection.Id}:{string.Join(',', connection.Sites.Select(s => s.Id))}";
        if (cache.TryGetValue(key, out IReadOnlyDictionary<string, string>? cached) && cached is not null)
        {
            return cached;
        }
        var token = await AppTokenAsync(connection, ct);
        var drives = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (siteId, siteName) in connection.Sites)
        {
            var body = await AppGetAsync(token, $"sites/{Uri.EscapeDataString(siteId)}/drives?$select=id,name", ct);
            foreach (var d in Items(body))
            {
                if (SharePointSites.Text(d, "id") is { } id && IsPart(id))
                {
                    drives[id] = $"{siteName} / {SharePointSites.Text(d, "name") ?? id}";
                }
            }
        }
        cache.Set(key, (IReadOnlyDictionary<string, string>)drives, DrivesFreshness);
        return drives;
    }

    public async Task<RepositoryPage> ListAsync(Guid connectionId, string containerId, string? folderId, string? cursor, CancellationToken ct = default)
    {
        var (_, token) = await RequireAsync(connectionId, containerId, ct);
        if (!FolderIn(containerId, folderId, out var folder) || (cursor is not null && !IsCursor(cursor)))
        {
            return new RepositoryPage([], null);
        }
        var body = await AppGetAsync(token, ChildrenPath(containerId, folder, cursor), ct);
        return new RepositoryPage(Items(body).Select(i => Map(i, containerId)).Where(i => i is not null).Select(i => i!).ToList(), SkipToken(body));
    }

    public async Task<RepositoryItem?> GetAsync(Guid connectionId, string itemId, CancellationToken ct = default)
    {
        if (Split(itemId) is not (var drive, var item) || await LoadAsync(connectionId, ct) is not { } connection || !(await DrivesAsync(connection, ct)).ContainsKey(drive))
        {
            return null;
        }
        var token = await AppTokenAsync(connection, ct);
        var body = await AppGetAsync(token, $"drives/{drive}/items/{item}?$select={ItemSelect}", ct, allowMissing: true);
        if (body.ValueKind != JsonValueKind.Object || Map(body, drive) is not { } mapped)
        {
            return null;
        }
        var permissions = Items(await AppGetAsync(token, $"drives/{drive}/items/{item}/permissions", ct)).Select(MapPermission)
            .Where(p => p is not null).Select(p => p!).ToList();
        return mapped with { Permissions = permissions };
    }

    public async Task<RepositoryChanges> ChangesAsync(Guid connectionId, string containerId, string? cursor, CancellationToken ct = default)
    {
        var (_, token) = await RequireAsync(connectionId, containerId, ct);
        if (cursor is null)
        {
            var start = await AppGetAsync(token, $"drives/{containerId}/root/delta?token=latest", ct);
            return new RepositoryChanges([], DeltaToken(start) ?? throw new RepositoryUnavailableException("unreachable"), false);
        }
        var body = await AppGetAsync(token, $"drives/{containerId}/root/delta?token={Uri.EscapeDataString(cursor)}&$select={ItemSelect},deleted", ct);
        var changes = new List<RepositoryChange>();
        foreach (var i in Items(body))
        {
            if (SharePointSites.Text(i, "id") is not { } id || !IsPart(id))
            {
                continue;
            }
            var removed = i.TryGetProperty("deleted", out _);
            var mapped = removed ? null : Map(i, containerId);
            // The drive's root appears in every delta; it is the container itself, not an item.
            if (mapped is not null && i.TryGetProperty("root", out _))
            {
                continue;
            }
            changes.Add(new RepositoryChange($"{containerId}~{id}", mapped is null, mapped));
        }
        var next = SkipToken(body, "@odata.nextLink", "token");
        return next is not null ? new RepositoryChanges(changes, next, true) : new RepositoryChanges(changes, DeltaToken(body) ?? cursor, false);
    }

    public async Task<RepositoryContent?> OpenAsync(Guid connectionId, string itemId, string userEmail, CancellationToken ct = default)
    {
        if (Split(itemId) is not (var drive, var item) || await LoadAsync(connectionId, ct) is not { } connection || !(await DrivesAsync(connection, ct)).ContainsKey(drive))
        {
            return null;
        }
        var grant = await access.AcquireAsync(Microsoft365Features.SharePointDocuments, ct);
        if (grant.State != M365States.Ok)
        {
            return null;
        }
        try
        {
            var meta = await graph.DelegatedSendAsync(grant.AccessToken!, HttpMethod.Get, $"drives/{drive}/items/{item}?$select=name,file,size", null, ct);
            if (!meta.TryGetProperty("file", out var file))
            {
                return null;
            }
            var size = meta.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : (long?)null;
            if (size > MaxContentBytes)
            {
                throw new RepositoryUnavailableException("too_large");
            }
            var stream = await graph.DelegatedDownloadAsync(grant.AccessToken!, $"drives/{drive}/items/{item}/content", ct);
            return new RepositoryContent(stream, SharePointSites.Text(file, "mimeType") ?? "application/octet-stream", SharePointSites.Text(meta, "name") ?? item,
                stream.Length, NoOwner.Instance);
        }
        catch (GraphCallException ex) when (ex.Kind is "forbidden" or "not_found" or "no_mailbox")
        {
            return null;
        }
        catch (GraphCallException ex)
        {
            throw new RepositoryUnavailableException(ex.Kind == "unauthorized" ? "reconnect" : "unreachable");
        }
    }

    public async Task<RepositoryUserPage> ListAsUserAsync(Guid connectionId, string containerId, string? folderId, string? cursor, string userEmail,
        CancellationToken ct = default)
    {
        if (!IsPart(containerId) || !FolderIn(containerId, folderId, out var folder) || (cursor is not null && !IsCursor(cursor)))
        {
            return RepositoryUserPage.Not("not_found");
        }
        SharePointConnection? connection;
        try
        {
            connection = await LoadAsync(connectionId, ct);
            if (connection is null)
            {
                return RepositoryUserPage.Not("unavailable");
            }
            if (!(await DrivesAsync(connection, ct)).ContainsKey(containerId))
            {
                return RepositoryUserPage.Not("not_found");
            }
        }
        catch (RepositoryUnavailableException)
        {
            return RepositoryUserPage.Not("unavailable");
        }
        var grant = await access.AcquireAsync(Microsoft365Features.SharePointDocuments, ct);
        if (grant.State is M365States.NotConnected or M365States.Reconnect)
        {
            return RepositoryUserPage.Not(grant.State);
        }
        if (grant.State != M365States.Ok)
        {
            return RepositoryUserPage.Not("unavailable");
        }
        try
        {
            var body = await graph.DelegatedSendAsync(grant.AccessToken!, HttpMethod.Get, ChildrenPath(containerId, folder, cursor), null, ct);
            return new RepositoryUserPage("ok", Items(body).Select(i => Map(i, containerId)).Where(i => i is not null).Select(i => i!).ToList(), SkipToken(body));
        }
        catch (GraphCallException ex)
        {
            return RepositoryUserPage.Not(ex.Kind switch
            {
                "unauthorized" => M365States.Reconnect,
                "forbidden" or "not_found" or "no_mailbox" => "not_found",
                _ => "unavailable",
            });
        }
    }

    public async Task<RepositoryWrite> UploadAsUserAsync(Guid connectionId, string containerId, string? folderId, string name, Stream content, long length,
        string userEmail, CancellationToken ct = default)
    {
        if (!IsPart(containerId) || !FolderIn(containerId, folderId, out var folder) || !IsName(name))
        {
            return RepositoryWrite.Not("not_found");
        }
        if (length > MaxContentBytes)
        {
            return RepositoryWrite.Not("too_large");
        }
        // A file of the same name gets a new version, as uploading it in SharePoint would.
        var path = $"drives/{containerId}/items/{folder ?? "root"}:/{Uri.EscapeDataString(name)}:/content?@microsoft.graph.conflictBehavior=replace";
        return await WriteAsync(connectionId, containerId, token => graph.DelegatedUploadAsync(token, path, content, length, ct), ct);
    }

    public async Task<RepositoryWrite> RenameAsUserAsync(Guid connectionId, string itemId, string name, string userEmail, CancellationToken ct = default)
    {
        if (Split(itemId) is not (var drive, var item) || !IsName(name))
        {
            return RepositoryWrite.Not("not_found");
        }
        return await WriteAsync(connectionId, drive,
            token => graph.DelegatedSendAsync(token, HttpMethod.Patch, $"drives/{drive}/items/{item}?@microsoft.graph.conflictBehavior=fail", new { name }, ct), ct);
    }

    public async Task<IReadOnlyList<RepositoryColumn>> ColumnsAsync(Guid connectionId, string containerId, CancellationToken ct = default)
    {
        var (_, token) = await RequireAsync(connectionId, containerId, ct);
        var body = await AppGetAsync(token, $"drives/{containerId}/list/columns?$select=name,displayName,readOnly,hidden", ct, allowMissing: true);
        return Items(body)
            .Where(c => !(c.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True) && !(c.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True))
            .Select(c => (Name: SharePointSites.Text(c, "name"), Display: SharePointSites.Text(c, "displayName")))
            .Where(c => c.Name is { } n && IsColumn(n) && !SystemColumns.Contains(n))
            .Select(c => new RepositoryColumn(c.Name!, c.Display is { Length: > 0 and <= 200 } d ? d : c.Name!))
            .OrderBy(c => c.DisplayName, StringComparer.Ordinal).ToList();
    }

    public async Task<RepositoryItemDetails?> DetailsAsync(Guid connectionId, string itemId, bool versions, CancellationToken ct = default)
    {
        if (await GetAsync(connectionId, itemId, ct) is not { } item || Split(itemId) is not (var drive, var id))
        {
            return null;
        }
        var connection = (await LoadAsync(connectionId, ct))!;
        var token = await AppTokenAsync(connection, ct);
        var listItem = await AppGetAsync(token, $"drives/{drive}/items/{id}/listItem?$select=createdDateTime,createdBy&$expand=fields", ct, allowMissing: true);
        DateTimeOffset? created = DateTimeOffset.TryParse(SharePointSites.Text(listItem, "createdDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var c) ? c : null;
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        if (listItem.ValueKind == JsonValueKind.Object && listItem.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in fields.EnumerateObject().Where(f => IsColumn(f.Name) && !SystemColumns.Contains(f.Name)))
            {
                var text = field.Value.ValueKind switch
                {
                    JsonValueKind.String => field.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => field.Value.GetRawText(),
                    _ => null,
                };
                if (text is { Length: <= 4000 })
                {
                    columns[field.Name] = text;
                }
            }
        }
        var history = new List<RepositoryVersion>();
        if (versions && !item.Folder)
        {
            foreach (var v in Items(await AppGetAsync(token, $"drives/{drive}/items/{id}/versions?$top=200", ct, allowMissing: true)))
            {
                if (SharePointSites.Text(v, "id") is { } versionId && IsVersion(versionId))
                {
                    history.Add(new RepositoryVersion(versionId,
                        DateTimeOffset.TryParse(SharePointSites.Text(v, "lastModifiedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : null,
                        Email(v, "lastModifiedBy"), v.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : null));
                }
            }
            // Microsoft lists the newest first; an import adds them oldest first.
            history.Reverse();
        }
        return new RepositoryItemDetails(item, created, Email(listItem, "createdBy"), columns, history);
    }

    public async Task<RepositoryContent?> ReadAsync(Guid connectionId, string itemId, string? versionId, CancellationToken ct = default)
    {
        if (Split(itemId) is not (var drive, var id) || (versionId is not null && !IsVersion(versionId)))
        {
            return null;
        }
        var (_, token) = await RequireAsync(connectionId, drive, ct);
        var meta = await AppGetAsync(token, $"drives/{drive}/items/{id}?$select=name,file,size", ct, allowMissing: true);
        if (meta.ValueKind != JsonValueKind.Object || !meta.TryGetProperty("file", out var file))
        {
            return null;
        }
        if (meta.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) && bytes > MaxContentBytes)
        {
            throw new RepositoryUnavailableException("too_large");
        }
        Stream? stream;
        try
        {
            stream = await graph.AppDownloadAsync(token, versionId is null ? $"drives/{drive}/items/{id}/content" : $"drives/{drive}/items/{id}/versions/{versionId}/content", ct);
        }
        catch (GraphConsentMissingException)
        {
            throw new RepositoryUnavailableException("consent_missing");
        }
        catch (GraphUnavailableException)
        {
            throw new RepositoryUnavailableException("unreachable");
        }
        return stream is null ? null
            : new RepositoryContent(stream, SharePointSites.Text(file, "mimeType") ?? "application/octet-stream", SharePointSites.Text(meta, "name") ?? id, stream.Length, NoOwner.Instance);
    }

    /// <summary>Columns SharePoint keeps for every file, which an import takes from the item itself or does not need.</summary>
    private static readonly HashSet<string> SystemColumns = new(StringComparer.Ordinal)
    {
        "ContentType", "FileLeafRef", "LinkFilename", "LinkFilenameNoMenu", "DocIcon", "FileSizeDisplay", "ItemChildCount", "FolderChildCount", "Edit",
        "Created", "Modified", "Author", "Editor", "AppAuthor", "AppEditor", "ComplianceAssetId", "id", "ID",
    };

    private static bool IsColumn(string name) => name.Length is > 0 and <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !name.StartsWith('_');

    private static bool IsVersion(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '.');

    private static string? Email(JsonElement e, string identity) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(identity, out var set) && set.TryGetProperty("user", out var user)
            && SharePointSites.Text(user, "email") is { Length: > 3 and <= 320 } email && email.Contains('@', StringComparison.Ordinal) ? email.ToLowerInvariant() : null;

    /// <summary>A write as the signed-in person with the SharePoint uploads feature, inside an allowed document library.</summary>
    private async Task<RepositoryWrite> WriteAsync(Guid connectionId, string drive, Func<string, Task<JsonElement>> write, CancellationToken ct)
    {
        try
        {
            if (await LoadAsync(connectionId, ct) is not { } connection)
            {
                return RepositoryWrite.Not("unavailable");
            }
            if (!(await DrivesAsync(connection, ct)).ContainsKey(drive))
            {
                return RepositoryWrite.Not("not_found");
            }
        }
        catch (RepositoryUnavailableException)
        {
            return RepositoryWrite.Not("unavailable");
        }
        var grant = await access.AcquireAsync(Microsoft365Features.SharePointUpload, ct);
        if (grant.State is M365States.NotConnected or M365States.Reconnect)
        {
            return RepositoryWrite.Not(grant.State);
        }
        if (grant.State != M365States.Ok)
        {
            return RepositoryWrite.Not("unavailable");
        }
        try
        {
            var body = await write(grant.AccessToken!);
            return Map(body, drive) is { } written ? new RepositoryWrite("ok", written) : RepositoryWrite.Not("unavailable");
        }
        catch (GraphCallException ex)
        {
            return RepositoryWrite.Not(ex.Kind switch
            {
                "unauthorized" => M365States.Reconnect,
                "forbidden" => "forbidden",
                "conflict" => "conflict",
                "too_large" => "too_large",
                "not_found" or "no_mailbox" => "not_found",
                _ => "unavailable",
            });
        }
    }

    /// <summary>A name SharePoint accepts: no path separators or characters SharePoint refuses, not only dots or spaces.</summary>
    internal static bool IsName(string name) =>
        name.Length is > 0 and <= MaxNameLength && name.Trim().Trim('.').Length > 0 && name == name.Trim()
        && !name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '#' or '%');

    private async Task<(SharePointConnection Connection, AppToken Token)> RequireAsync(Guid connectionId, string containerId, CancellationToken ct)
    {
        var connection = await LoadAsync(connectionId, ct) ?? throw new RepositoryUnavailableException("connection_unusable");
        if (!IsPart(containerId) || !(await DrivesAsync(connection, ct)).ContainsKey(containerId))
        {
            throw new RepositoryUnavailableException("container_not_allowed");
        }
        return (connection, await AppTokenAsync(connection, ct));
    }

    private async Task<AppToken> AppTokenAsync(SharePointConnection connection, CancellationToken ct)
    {
        try
        {
            return await graph.AcquireAppTokenAsync(connection.ExternalTenantId, ct);
        }
        catch (GraphConsentMissingException)
        {
            throw new RepositoryUnavailableException("consent_missing");
        }
        catch (GraphUnavailableException)
        {
            throw new RepositoryUnavailableException("unreachable");
        }
    }

    private async Task<JsonElement> AppGetAsync(AppToken token, string path, CancellationToken ct, bool allowMissing = false)
    {
        try
        {
            return await graph.AppGetAsync(token, path, ct) ?? (allowMissing ? default : throw new RepositoryUnavailableException("not_granted"));
        }
        catch (GraphConsentMissingException)
        {
            throw new RepositoryUnavailableException("consent_missing");
        }
        catch (GraphUnavailableException)
        {
            throw new RepositoryUnavailableException("unreachable");
        }
    }

    private static string ChildrenPath(string drive, string? folder, string? cursor) =>
        $"drives/{drive}/items/{folder ?? "root"}/children?$top=200&$select={ItemSelect}&$orderby=name"
        + (cursor is null ? string.Empty : $"&$skiptoken={Uri.EscapeDataString(cursor)}");

    private static IEnumerable<JsonElement> Items(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>The value of one query parameter of the next page's link, so the link itself is never followed.</summary>
    private static string? SkipToken(JsonElement body, string link = "@odata.nextLink", string parameter = "$skiptoken")
    {
        if (SharePointSites.Text(body, link) is not { } next || !Uri.TryCreate(next, UriKind.Absolute, out var uri))
        {
            return null;
        }
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && Uri.UnescapeDataString(pair[..at]) == parameter)
            {
                var value = Uri.UnescapeDataString(pair[(at + 1)..]);
                return IsCursor(value) ? value : null;
            }
        }
        return null;
    }

    private static string? DeltaToken(JsonElement body) => SkipToken(body, "@odata.deltaLink", "token");

    /// <summary>Drive and item IDs are letters, digits, '!', '-' and '_'; anything else is never sent to Microsoft.</summary>
    private static bool IsPart(string id) => id.Length is > 0 and <= 200 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '!' or '-' or '_');

    private static bool IsCursor(string value) => value.Length is > 0 and <= 2000 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '!' or '-' or '_' or '.' or '=' or '%' or '+' or '/');

    /// <summary>A folder given by its item ID (<c>drive~item</c>) must be in the container; null is the container's root.</summary>
    private static bool FolderIn(string containerId, string? folderId, out string? folder)
    {
        folder = null;
        if (folderId is null)
        {
            return true;
        }
        if (Split(folderId) is not (var drive, var item) || drive != containerId)
        {
            return false;
        }
        folder = item;
        return true;
    }

    private static (string Drive, string Item)? Split(string itemId)
    {
        var parts = itemId.Split('~');
        return parts.Length == 2 && IsPart(parts[0]) && IsPart(parts[1]) ? (parts[0], parts[1]) : null;
    }

    private static RepositoryItem? Map(JsonElement i, string drive)
    {
        if (SharePointSites.Text(i, "id") is not { } id || !IsPart(id) || SharePointSites.Text(i, "name") is not { } name)
        {
            return null;
        }
        var folder = i.TryGetProperty("folder", out _);
        var type = i.TryGetProperty("file", out var file) ? SharePointSites.Text(file, "mimeType") : null;
        long? size = i.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : null;
        DateTimeOffset? modified = DateTimeOffset.TryParse(SharePointSites.Text(i, "lastModifiedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m)
            ? m : null;
        var parent = i.TryGetProperty("parentReference", out var p) ? SharePointSites.Text(p, "id") : null;
        var parentIsRoot = i.TryGetProperty("parentReference", out var pr) && SharePointSites.Text(pr, "path") is { } path && path.EndsWith("root:", StringComparison.Ordinal);
        var link = SharePointSites.Text(i, "webUrl");
        // The Microsoft Purview label on the file (spec 011 FR-032), kept only when its ID is a GUID.
        var label = i.TryGetProperty("sensitivityLabel", out var l) && SharePointSites.Text(l, "labelId") is { } labelId && Guid.TryParse(labelId, out var labelGuid)
            ? new RepositoryLabel(labelGuid.ToString("D"), SharePointSites.Text(l, "displayName") is { Length: > 0 and <= 200 } labelName ? labelName : null)
            : null;
        return new RepositoryItem($"{drive}~{id}", drive, parent is null || parentIsRoot || !IsPart(parent) ? null : $"{drive}~{parent}", name, folder,
            folder ? null : type ?? "application/octet-stream", folder ? null : size, modified, SharePointSites.Text(i, "eTag"),
            link is not null && link.StartsWith("https://", StringComparison.Ordinal) ? link : null, Label: folder ? null : label);
    }

    private static RepositoryPermission? MapPermission(JsonElement p)
    {
        var roles = p.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetString()).ToList() : [];
        var role = roles.Contains("owner") ? "owner" : roles.Contains("write") ? "write" : roles.Contains("read") ? "read" : null;
        if (role is null || !p.TryGetProperty("grantedToV2", out var to))
        {
            return null;
        }
        if (to.TryGetProperty("user", out var user) && SharePointSites.Text(user, "email") is { } email)
        {
            return new RepositoryPermission(new RepositoryPrincipal("user", email.ToLowerInvariant(), null), role);
        }
        if (to.TryGetProperty("group", out var group) && SharePointSites.Text(group, "email") is { } groupEmail)
        {
            return new RepositoryPermission(new RepositoryPrincipal("group", groupEmail.ToLowerInvariant(), null), role);
        }
        // SharePoint site groups and sharing links are not mirrored, which only narrows access (ADR-0016).
        return null;
    }

    private sealed class NoOwner : IDisposable
    {
        public static readonly NoOwner Instance = new();

        public void Dispose()
        {
        }
    }
}
