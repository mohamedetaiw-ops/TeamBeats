using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// An import from a SharePoint document library, or one folder of it, into a TeamBeats library (spec 011 FR-033).
/// <paramref name="Columns"/> maps SharePoint column names to fields of <paramref name="TypeId"/>; <paramref name="Folder"/>
/// is the TeamBeats folder the files go under.
/// </summary>
public sealed record SharePointImportRequest(Guid? ConnectionId, string? ContainerId, string? FolderId, Guid? Folder, bool AllVersions, Guid? TypeId,
    IReadOnlyDictionary<string, string>? Columns);

/// <summary>Where an import from SharePoint reads, and how each file becomes a document; kept on the import.</summary>
public sealed record SharePointImportOptions(Guid ConnectionId, string ContainerId, string? FolderId, bool AllVersions, Guid? TypeId,
    IReadOnlyDictionary<string, string> Columns);

/// <summary>
/// Import from SharePoint into TeamBeats libraries (spec 011 FR-033, US10). A library manager picks a document library
/// (or folder) the connection allows, whether to bring every version or only the latest, a document type and which
/// SharePoint column fills which field. The job then lists the source and adds each file through bulk import (FR-022):
/// the normal upload checks, original dates, the author when their email matches a user, and a row per file in the
/// report, which also says when SharePoint gave a file narrower permissions than the TeamBeats library will. The source
/// is only read, never changed, with the connection's own access.
/// </summary>
public sealed class SharePointImports(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    Libraries libraries,
    DocumentImports imports,
    IDocumentRepository repositories,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const string Source = "sharepoint";
    public const int MaxColumns = 50;
    public const int MaxFolders = 500;

    /// <summary>The columns of a document library that an import may map, for people who manage libraries somewhere.</summary>
    public async Task<IReadOnlyList<RepositoryColumn>> ColumnsAsync(Guid connectionId, string containerId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        context.RequireUser();
        if (!await authz.HoldsAnywhereAsync(Permissions.DocManage, ct))
        {
            throw AppException.Denied();
        }
        if (containerId.Length is 0 or > Libraries.MaxIdLength)
        {
            throw AppException.NotFound();
        }
        try
        {
            return await repositories.ColumnsAsync(connectionId, containerId, ct);
        }
        catch (RepositoryUnavailableException)
        {
            throw AppException.Conflict("conflict.documents.connected_unavailable");
        }
    }

    /// <summary>The folders directly inside a document library, or inside one of its folders, that an import may start from.</summary>
    public async Task<IReadOnlyList<RepositoryContainer>> FoldersAsync(Guid connectionId, string containerId, string? folderId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        context.RequireUser();
        if (!await authz.HoldsAnywhereAsync(Permissions.DocManage, ct))
        {
            throw AppException.Denied();
        }
        if (containerId.Length is 0 or > Libraries.MaxIdLength || folderId is { Length: 0 or > Libraries.MaxIdLength })
        {
            throw AppException.NotFound();
        }
        try
        {
            if (!(await repositories.ContainersAsync(connectionId, ct)).Any(c => c.Id == containerId))
            {
                throw AppException.NotFound();
            }
            var folders = new List<RepositoryContainer>();
            string? cursor = null;
            do
            {
                var page = await repositories.ListAsync(connectionId, containerId, folderId, cursor, ct);
                folders.AddRange(page.Items.Where(i => i.Folder && i.ContainerId == containerId).Select(i => new RepositoryContainer(i.Id, i.Name)));
                cursor = page.NextCursor;
            }
            while (cursor is not null && folders.Count < MaxFolders);
            return folders.Take(MaxFolders).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (RepositoryUnavailableException)
        {
            throw AppException.Conflict("conflict.documents.connected_unavailable");
        }
    }

    public async Task<ImportView> StartAsync(Guid libraryId, SharePointImportRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var library = await libraries.ManagedAsync(libraryId, ct);
        library.RequireOwn();
        if (library.Status != "active")
        {
            throw AppException.Conflict("conflict.documents.library_archived");
        }
        var errors = new ValidationErrors();
        if (request.Folder is { } target && !await session.ScalarAsync<bool>(
                "select exists (select 1 from documents.folder where id = @target and library_id = @libraryId)", new { target, libraryId }, ct))
        {
            errors.Add("folder", "validation.documents.folder_parent");
        }
        DocumentTypeView? type = null;
        if (request.TypeId is { } typeId)
        {
            type = (await libraries.TypesAsync(libraryId, ct)).FirstOrDefault(t => t.Id == typeId && t.Status == "active");
            if (type is null)
            {
                errors.Add("typeId", "validation.documents.type_unknown");
            }
        }
        var columns = request.Columns ?? new Dictionary<string, string>();
        if (columns.Count > MaxColumns || columns.Any(c => c.Key.Length is 0 or > 100 || type?.Fields.Any(f => f.Key == c.Value) != true))
        {
            errors.Add("columns", "validation.documents.import_columns_map");
        }
        RepositoryContainer? container = null;
        if (request.ConnectionId is not { } connection || string.IsNullOrWhiteSpace(request.ContainerId) || request.ContainerId.Length > Libraries.MaxIdLength)
        {
            errors.Add("containerId", "validation.documents.connected_container");
        }
        else
        {
            try
            {
                // Only Microsoft 365 connections serve SharePoint document libraries with versions and columns to import.
                if ((await repositories.SourcesAsync(ct)).Any(s => s.ConnectionId == connection && s.Kind == ConnectionKinds.Microsoft365))
                {
                    container = (await repositories.ContainersAsync(connection, ct)).FirstOrDefault(c => c.Id == request.ContainerId);
                }
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
        if (request.FolderId is { Length: 0 or > Libraries.MaxIdLength })
        {
            errors.Add("folderId", "validation.documents.connected_folder");
        }
        errors.ThrowIfAny();
        var name = container!.Name;
        if (request.FolderId is { } folderId)
        {
            RepositoryItem? folder;
            try
            {
                folder = await repositories.GetAsync(request.ConnectionId!.Value, folderId, ct);
            }
            catch (RepositoryUnavailableException)
            {
                throw AppException.Conflict("conflict.documents.connected_unavailable");
            }
            if (folder is not { Folder: true } || folder.ContainerId != container.Id)
            {
                throw AppException.Validation("folderId", "validation.documents.connected_folder");
            }
            name = $"{name} / {folder.Name}";
        }

        var options = new SharePointImportOptions(request.ConnectionId!.Value, container.Id, request.FolderId, request.AllVersions, type?.Id,
            new Dictionary<string, string>(columns, StringComparer.Ordinal));
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into documents.import (tenant_id, id, library_id, folder_id, object_key, file_name, size_bytes, status, total, created_by, created_at, source, source_options)
            values (platform.current_tenant_id(), @id, @libraryId, @folder, null, @name, 0, 'listing', 0, @user, @now, @source, @options)
            """, new { id, libraryId, folder = request.Folder, name = name.Length > 300 ? name[..300] : name, user, now = clock.UtcNow, source = Source,
                options = new Jsonb(DocJson.Write(options)) }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocImportStarted, "doc_import", id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { library = library.Id, folder = request.Folder, source = Source, connection = options.ConnectionId, container = container.Name,
                remoteFolder = request.FolderId, allVersions = request.AllVersions, type = type?.Id }), ct);
        await outbox.EnqueueAsync(new DocumentImportRequested(id), ct);
        return await imports.GetAsync(id, ct);
    }

    internal static SharePointImportOptions? Options(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<SharePointImportOptions>(json, JsonDefaults.Options);
}
