using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

/// <summary>What a library view says about a connected library: its connection, the document library it shows, and its upload setting.</summary>
public sealed record ConnectedLibraryView(Guid ConnectionId, string? ContainerName, bool Folder, bool UploadEnabled);

/// <summary>A connection that serves documents, with the document libraries a manager may connect (spec 011 FR-026).</summary>
public sealed record ConnectedSourceView(Guid ConnectionId, string Kind, string Name, IReadOnlyList<RepositoryContainer> Containers, string State);

public sealed record ConnectedLibraryRequest(
    IReadOnlyDictionary<string, string>? Names,
    string? Scope,
    Guid? ScopeId,
    Guid? ConnectionId,
    string? ContainerId,
    string? FolderId,
    string? ClassificationDefault);

/// <summary>A file or folder of a connected library, as the signed-in person sees it in the source system.</summary>
public sealed record ConnectedItemView(string Id, string Name, bool Folder, string? MediaType, long? Size, DateTimeOffset? ModifiedAt, string? WebLink);

/// <summary>
/// A page of a connected library. <paramref name="State"/> is <c>ok</c>, or why nothing is listed: <c>not_connected</c>
/// or <c>reconnect</c> (the person must connect their Microsoft 365 account), <c>not_found</c> or <c>unavailable</c>.
/// </summary>
/// <remarks>
/// <c>degraded</c> means SharePoint could not be reached and the items are the last synced copy, filtered by the
/// mirrored readers, as of <paramref name="SyncedAt"/> (FR-031).
/// </remarks>
public sealed record ConnectedPage(string State, IReadOnlyList<ConnectedItemView> Items, string? NextCursor, DateTimeOffset? SyncedAt = null);

/// <summary>Where a connected file opens in SharePoint, for a search result or citation the person may read.</summary>
public sealed record ConnectedLinkView(string WebLink);

/// <summary>
/// Connected libraries (spec 011 US9, FR-026, FR-027, FR-030). A library manager connects a SharePoint document library,
/// or one folder of it, at a scope they manage. Reading needs both the TeamBeats audience of the library and the
/// person's own permission in SharePoint: listing and opening run as the person through the Integrations module, so
/// SharePoint decides what they see, and TeamBeats keeps no copy of the content.
/// </summary>
public sealed partial class Libraries
{
    public const int MaxIdLength = 400;

    /// <summary>The connections that serve documents now and their document libraries; for people who manage libraries somewhere.</summary>
    public async Task<IReadOnlyList<ConnectedSourceView>> ConnectedSourcesAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        context.RequireUser();
        if (!await authz.HoldsAnywhereAsync(Permissions.DocManage, ct))
        {
            throw AppException.Denied();
        }
        var list = new List<ConnectedSourceView>();
        foreach (var source in await repositories.SourcesAsync(ct))
        {
            try
            {
                list.Add(new ConnectedSourceView(source.ConnectionId, source.Kind, source.Name, await repositories.ContainersAsync(source.ConnectionId, ct), "ok"));
            }
            catch (RepositoryUnavailableException)
            {
                list.Add(new ConnectedSourceView(source.ConnectionId, source.Kind, source.Name, [], "unavailable"));
            }
        }
        return list;
    }

    public async Task<LibraryView> CreateConnectedAsync(ConnectedLibraryRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        context.RequireUser();
        var errors = new ValidationErrors();
        Names(request.Names, "names", errors);
        var (ok, unit) = await ScopeUnitAsync(request.Scope, request.ScopeId, errors, ct);
        if (!ok)
        {
            errors.ThrowIfAny();
        }
        await authz.DemandAsync(Permissions.DocManage, unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct);
        var classification = request.ClassificationDefault ?? "internal";
        if (!ConditionAttributes.Classifications.Contains(classification))
        {
            errors.Add("classificationDefault", "validation.documents.classification");
        }
        RepositoryContainer? container = null;
        if (request.ConnectionId is not { } connection || string.IsNullOrWhiteSpace(request.ContainerId) || request.ContainerId.Length > MaxIdLength)
        {
            errors.Add("containerId", "validation.documents.connected_container");
        }
        else
        {
            try
            {
                container = (await repositories.ContainersAsync(connection, ct)).FirstOrDefault(c => c.Id == request.ContainerId);
            }
            catch (RepositoryUnavailableException)
            {
                throw AppException.Conflict("conflict.documents.connected_unavailable");
            }
            if (container is null)
            {
                errors.Add("containerId", "validation.documents.connected_container");
            }
        }
        if (request.FolderId is { } folder && (folder.Length == 0 || folder.Length > MaxIdLength))
        {
            errors.Add("folderId", "validation.documents.connected_folder");
        }
        errors.ThrowIfAny();
        if (request.FolderId is { } folderId)
        {
            // The folder must be a folder of that document library that the manager can see.
            var item = await repositories.GetAsync(request.ConnectionId!.Value, folderId, ct);
            if (item is not { Folder: true } || item.ContainerId != container!.Id)
            {
                throw AppException.Validation("folderId", "validation.documents.connected_folder");
            }
        }

        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into documents.library (tenant_id, id, names, scope, scope_id, org_unit_id, classification_default, created_by,
              source, connection_id, container_id, container_name, remote_folder_id)
            values (platform.current_tenant_id(), @id, @names, @scope, @scopeId, @unit, @classification, @by,
              'connected', @connection, @container, @containerName, @folder)
            """,
            new
            {
                id, names = new Jsonb(DocJson.Write(Trimmed(request.Names!))), scope = request.Scope, scopeId = request.ScopeId, unit, classification,
                by = context.ActorId, connection = request.ConnectionId, container = container!.Id, containerName = container.Name, folder = request.FolderId,
            }, ct);
        if (unit is { } owning)
        {
            await acl.AddAclEntryAsync(Securables.Library, id, "org_unit", owning, Permissions.DocView, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.DocLibraryCreated, Securables.Library, id.ToString(), TargetOrgUnitId: unit,
            Changes: new { names = request.Names, scope = request.Scope, scopeId = request.ScopeId, classification, connection = request.ConnectionId, container = container.Name }), ct);
        var library = (await Access.LibraryAsync(id, ct))!;
        return await ViewAsync(library, await Access.RightsAsync(library, DocumentAccess.LibrarySecurable(library), ct), ct);
    }

    /// <summary>The connected library if the caller is in its audience. Guests never read connected libraries.</summary>
    private async Task<(LibraryRow Library, ConnectedSource Source)> ConnectedVisibleAsync(Guid id, CancellationToken ct)
    {
        var (library, _) = await VisibleAsync(id, ct);
        if (library.Connected is not { } source || await Access.IsGuestAsync(ct))
        {
            throw AppException.NotFound();
        }
        return (library, source);
    }

    /// <summary>One page of a connected library as the caller sees it in the source system (FR-027).</summary>
    public async Task<ConnectedPage> ConnectedItemsAsync(Guid id, string? folder, string? cursor, CancellationToken ct)
    {
        var (_, source) = await ConnectedVisibleAsync(id, ct);
        if ((folder?.Length ?? 0) > MaxIdLength || (cursor?.Length ?? 0) > 2000)
        {
            return new ConnectedPage("not_found", [], null);
        }
        var email = await EmailAsync(ct);
        var page = await repositories.ListAsUserAsync(source.ConnectionId, source.ContainerId, folder ?? source.FolderId, cursor, email, ct);
        if (page.State == "unavailable" && cursor is null)
        {
            return await LastSyncedAsync(id, folder ?? source.FolderId, email, ct);
        }
        return new ConnectedPage(page.State,
            page.Items.Select(i => new ConnectedItemView(i.Id, i.Name, i.Folder, i.MediaType, i.Size, i.ModifiedAt, i.WebLink)).ToList(), page.NextCursor);
    }

    /// <summary>The content of a connected file, read live as the caller; null when they may not read it in the source system.</summary>
    public async Task<RepositoryContent?> ConnectedContentAsync(Guid id, string item, CancellationToken ct)
    {
        var (library, source) = await ConnectedVisibleAsync(id, ct);
        if (item.Length is 0 or > MaxIdLength)
        {
            return null;
        }
        RepositoryContent? content;
        try
        {
            content = await repositories.OpenAsync(source.ConnectionId, item, await EmailAsync(ct), ct);
        }
        catch (RepositoryUnavailableException ex)
        {
            throw AppException.Conflict(ex.Reason == "too_large" ? "conflict.documents.connected_too_large" : "conflict.documents.connected_unavailable");
        }
        if (content is not null)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.DocConnectedOpened, Securables.Library, library.Id.ToString(), TargetOrgUnitId: library.OrgUnitId,
                Changes: new { item }), ct);
        }
        return content;
    }

    /// <summary>The last synced copy of a folder, with only the items the mirrored readers give the person (FR-031).</summary>
    private async Task<ConnectedPage> LastSyncedAsync(Guid libraryId, string? folder, string email, CancellationToken ct)
    {
        var synced = await session.QuerySingleOrDefaultAsync("select last_success_at from documents.connected_sync where library_id = @libraryId",
            new { libraryId }, r => r.Get<DateTimeOffset?>("last_success_at"), ct);
        if (synced is null)
        {
            return new ConnectedPage("unavailable", [], null);
        }
        var items = await session.QueryAsync(
            """
            select remote_id, name, folder, media_type, size, modified_at, web_link from documents.connected_item
            where library_id = @libraryId and parent_remote_id is not distinct from @folder::text and (anyone or @email = any(readers) or folder)
            order by folder desc, name limit 500
            """, new { libraryId, folder, email = email.ToLowerInvariant() },
            r => new ConnectedItemView(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.Get<string>("media_type"), r.Get<long?>("size"),
                r.Get<DateTimeOffset?>("modified_at"), r.Get<string>("web_link")), ct);
        return new ConnectedPage("degraded", items, null, synced);
    }

    /// <summary>Where a connected file opens in SharePoint, when the person is in the audience and a mirrored reader; SharePoint checks again.</summary>
    public async Task<ConnectedLinkView> ConnectedLinkAsync(Guid id, Guid itemId, CancellationToken ct)
    {
        await ConnectedVisibleAsync(id, ct);
        var link = await session.ScalarAsync<string>(
            "select web_link from documents.connected_item where id = @itemId and library_id = @id and not folder and (anyone or @email = any(readers))",
            new { itemId, id, email = (await EmailAsync(ct)).ToLowerInvariant() }, ct);
        return link is { } l && l.StartsWith("https://", StringComparison.Ordinal) ? new ConnectedLinkView(l) : throw AppException.NotFound();
    }

    private async Task<string> EmailAsync(CancellationToken ct) => (await users.GetAsync(context.RequireUser(), ct))?.Email ?? string.Empty;
}
