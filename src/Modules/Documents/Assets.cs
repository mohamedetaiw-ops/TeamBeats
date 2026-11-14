using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

public sealed record AssetPortalView(bool CanManage);

public sealed record AssetCollectionRequest(IReadOnlyDictionary<string, string>? Names, IReadOnlyDictionary<string, string>? Descriptions, Guid? RestrictedTo,
    string? Status = null, int? Version = null);

/// <summary>A collection as the caller sees it: whether they read it, have asked for access, and may curate it.</summary>
public sealed record AssetCollectionView(Guid Id, IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string> Descriptions, Guid? RestrictedTo,
    string Status, int Version, bool CanRead, bool CanManage, bool AccessRequested, int AssetCount);

public sealed record AssetRequest(IReadOnlyDictionary<string, string>? Names, IReadOnlyDictionary<string, string>? UsageNotes, DateTimeOffset? RightsExpireAt,
    bool ClearRightsExpiry = false, string? Status = null, int? Version = null);

public sealed record AssetRenditionView(string Key, string ContentType, long? SizeBytes, bool Ready);

public sealed record AssetView(Guid Id, Guid CollectionId, IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string> UsageNotes, string FileName,
    string ContentType, long SizeBytes, int? Width, int? Height, DateTimeOffset? RightsExpireAt, bool Expired, string Status, int Version, DateTimeOffset CreatedAt,
    IReadOnlyList<AssetRenditionView> Renditions);

public sealed record AccessRequestBody(string? Reason);

public sealed record AccessDecision(string? Decision);

public sealed record AssetAccessRequestView(Guid Id, Guid CollectionId, IReadOnlyDictionary<string, string> CollectionNames, Guid UserId, string UserName, string Reason,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt);

public sealed record AssetDownloadRow(Guid AssetId, IReadOnlyDictionary<string, string> AssetNames, string Rendition, Guid UserId, string UserName, DateTimeOffset At);

/// <summary>Downloads in a period: every download, newest first, and the count per asset and rendition.</summary>
public sealed record AssetDownloadReport(IReadOnlyList<AssetDownloadRow> Downloads, IReadOnlyList<AssetDownloadCount> Totals);

public sealed record AssetDownloadCount(Guid AssetId, IReadOnlyDictionary<string, string> AssetNames, string Rendition, int Downloads, int People);

/// <summary>An asset offered to the page builder's image picker: readable, current and an image.</summary>
public sealed record AssetPick(Guid Id, IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string> CollectionNames, int? Width, int? Height);

public static class AssetRenditions
{
    public const string Original = "original";
    public const string Svg = "svg";
    public const string Pdf = "pdf";

    /// <summary>The PNG widths made for every image (spec 011 US5 scenario 1: PNG at set sizes).</summary>
    public static readonly IReadOnlyList<int> PngWidths = [256, 512, 1024];

    public static string Png(int width) => $"png_{width}";

    public static bool Known(string key) =>
        key is Original or Svg or Pdf || PngWidths.Any(w => Png(w) == key);
}

/// <summary>
/// The brand asset portal (spec 011 US5, FR-012). Brand managers (<c>assets.manage</c>, tenant wide) curate collections
/// and assets with usage notes and usage rights; every image gets renditions on upload (PNG at 256, 512 and 1024 pixels
/// wide, the SVG itself when the source is one, and a PDF). Open collections are read by every member; a collection
/// restricted to an org unit is read by that unit's members and by people whose access request a brand manager approved.
/// When usage rights end the asset leaves the portal and the page builder picker for everyone but brand managers, and
/// <see cref="AssetRightsExpired"/> lets pages that use it be listed to their owners. Every download is recorded.
/// </summary>
public sealed class Assets(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning acl,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IUserDirectory users,
    Libraries libraries,
    DocumentFiles files,
    TenantObjectStorage storage,
    IDocumentConverter converter,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock) : IAssetImages
{
    public const string ResourceType = "asset_collection";
    public const long MaxAssetBytes = 50L * 1024 * 1024;
    public const int MaxNameLength = 120;
    public const int MaxNotesLength = 2000;
    public const int MaxReasonLength = 500;
    public const string AccessRequestedTemplate = "assets.access_requested";
    public const string AccessApprovedTemplate = "assets.access_approved";
    public const string AccessDeclinedTemplate = "assets.access_declined";

    /// <summary>What the portal stores: images (which get renditions) and the usual template formats.</summary>
    public static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
        ["svg"] = "image/svg+xml",
        ["pdf"] = "application/pdf",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["potx"] = "application/vnd.openxmlformats-officedocument.presentationml.template",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    private static bool IsImage(string contentType) => contentType.StartsWith("image/", StringComparison.Ordinal);

    // ---- Collections ----

    private sealed record CollectionRow(Guid Id, Dictionary<string, string> Names, Dictionary<string, string> Descriptions, Guid? RestrictedTo, string Status, int Version);

    private const string CollectionColumns = "id, names::text, descriptions::text, restricted_to, status, row_version";

    private static CollectionRow ReadCollection(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), DocJson.Read<Dictionary<string, string>>(r.GetString(1)),
        DocJson.Read<Dictionary<string, string>>(r.GetString(2)), r.Get<Guid?>("restricted_to"), r.GetString(4), r.GetInt32(5));

    internal static ResourceDescriptor Describe(Guid id, Guid? restrictedTo) =>
        new(ResourceType, id, restrictedTo, restrictedTo is null ? ResourceVisibility.Tenant : ResourceVisibility.Restricted);

    private async Task<bool> ManagerAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct)).Allowed;

    private async Task<bool> ReadsAsync(CollectionRow c, CancellationToken ct) =>
        (await authz.CheckAsync(Permissions.AssetsView, AuthzTarget.Resource(Describe(c.Id, c.RestrictedTo)), ct)).Allowed;

    /// <summary>Guests see only what is shared into their space (spec 026 FR-009), so not even that a collection exists.</summary>
    private async Task<bool> GuestAsync(CancellationToken ct) =>
        await memberships.GetAsync(context.RequireUser(), ct) is { MembershipType: MembershipTypes.Guest };

    private async Task DemandPortalAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsAssetPortal, ct);
        context.RequireUser();
    }

    private async Task<CollectionRow> CollectionAsync(Guid id, CancellationToken ct, bool forUpdate = false) =>
        await session.QuerySingleOrDefaultAsync($"select {CollectionColumns} from documents.asset_collection where id = @id" + (forUpdate ? " for update" : ""),
            new { id }, ReadCollection, ct) ?? throw AppException.NotFound();

    /// <summary>
    /// Every collection that is in use, so people find a restricted one and ask for it; what is inside shows only to
    /// those who read it. Retired collections show to brand managers only.
    /// </summary>
    public async Task<IReadOnlyList<AssetCollectionView>> CollectionsAsync(CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        var manager = await ManagerAsync(ct);
        var user = context.RequireUser();
        var rows = await session.QueryAsync($"select {CollectionColumns} from documents.asset_collection order by created_at, id", null, ReadCollection, ct);
        var requested = (await session.QueryAsync("select collection_id from documents.asset_access_request where user_id = @user and status = 'pending'",
            new { user }, r => r.GetGuid(0), ct)).ToHashSet();
        var counts = (await session.QueryAsync(
            "select collection_id, count(*)::int from documents.asset where status = 'active' and (expired_at is null or @manager) group by collection_id",
            new { manager }, r => (Id: r.GetGuid(0), Count: r.GetInt32(1)), ct)).ToDictionary(x => x.Id, x => x.Count);
        var guest = await GuestAsync(ct);
        var list = new List<AssetCollectionView>();
        foreach (var c in rows.Where(c => manager || c.Status == "active"))
        {
            var reads = await ReadsAsync(c, ct);
            if (!reads && guest)
            {
                continue;
            }
            list.Add(new AssetCollectionView(c.Id, c.Names, reads ? c.Descriptions : new Dictionary<string, string>(), c.RestrictedTo, c.Status, c.Version, reads, manager,
                requested.Contains(c.Id), reads ? counts.GetValueOrDefault(c.Id) : 0));
        }
        return list;
    }

    /// <summary>What the caller may do in the portal, so a brand manager can create the first collection.</summary>
    public async Task<AssetPortalView> PortalAsync(CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        return new AssetPortalView(await ManagerAsync(ct));
    }

    private async Task<AssetCollectionView> CollectionViewAsync(Guid id, CancellationToken ct) =>
        (await CollectionsAsync(ct)).Single(c => c.Id == id);

    private static void Texts(IReadOnlyDictionary<string, string>? texts, string field, int max, bool required, ValidationErrors errors)
    {
        if (texts is null || texts.Count == 0)
        {
            if (required)
            {
                errors.Add(field, "validation.assets.name");
            }
            return;
        }
        if (texts.Any(t => t.Key is not ("en" or "ar" or "fr" or "ur" or "hi") || (required && string.IsNullOrWhiteSpace(t.Value)) || t.Value.Trim().Length > max)
            || (required && !texts.Values.Any(v => !string.IsNullOrWhiteSpace(v))))
        {
            errors.Add(field, required ? "validation.assets.name" : "validation.assets.text");
        }
    }

    private static Dictionary<string, string> Trim(IReadOnlyDictionary<string, string>? texts) =>
        (texts ?? new Dictionary<string, string>()).Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToDictionary(t => t.Key, t => t.Value.Trim());

    public async Task<AssetCollectionView> CreateCollectionAsync(AssetCollectionRequest request, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        await authz.DemandAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct);
        var errors = new ValidationErrors();
        Texts(request.Names, "names", MaxNameLength, true, errors);
        Texts(request.Descriptions, "descriptions", MaxNotesLength, false, errors);
        if (request.RestrictedTo is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("restrictedTo", "validation.assets.unit");
        }
        errors.ThrowIfAny();
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into documents.asset_collection (tenant_id, id, names, descriptions, restricted_to, created_at, created_by, updated_at)
            values (platform.current_tenant_id(), @id, @names, @descriptions, @unit, @now, @user, @now)
            """, new { id, names = new Jsonb(DocJson.Write(Trim(request.Names))), descriptions = new Jsonb(DocJson.Write(Trim(request.Descriptions))),
                unit = request.RestrictedTo, now, user = context.RequireUser() }, ct);
        await RestrictAsync(id, request.RestrictedTo, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AssetCollectionCreated, ResourceType, id.ToString(), TargetOrgUnitId: request.RestrictedTo,
            Changes: new { names = request.Names, restrictedTo = request.RestrictedTo }), ct);
        return await CollectionViewAsync(id, ct);
    }

    /// <summary>A restricted collection is read by its unit and everything under it, and by people given access.</summary>
    private async Task RestrictAsync(Guid id, Guid? unit, CancellationToken ct)
    {
        await acl.ClearAclAsync(ResourceType, id, ct);
        if (unit is not { } u)
        {
            return;
        }
        await acl.AddAclEntryAsync(ResourceType, id, "org_subtree", u, Permissions.AssetsView, ct);
        var granted = await session.QueryAsync("select distinct user_id from documents.asset_access_request where collection_id = @id and status = 'approved'",
            new { id }, r => r.GetGuid(0), ct);
        foreach (var user in granted)
        {
            await acl.AddAclEntryAsync(ResourceType, id, "user", user, Permissions.AssetsView, ct);
        }
    }

    public async Task<AssetCollectionView> UpdateCollectionAsync(Guid id, AssetCollectionRequest request, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        await authz.DemandAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct);
        var current = await CollectionAsync(id, ct, forUpdate: true);
        if (request.Version != current.Version)
        {
            throw AppException.Conflict("conflict.assets.stale");
        }
        var errors = new ValidationErrors();
        Texts(request.Names, "names", MaxNameLength, true, errors);
        Texts(request.Descriptions, "descriptions", MaxNotesLength, false, errors);
        if (request.RestrictedTo is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("restrictedTo", "validation.assets.unit");
        }
        var status = request.Status ?? current.Status;
        if (status is not ("active" or "retired"))
        {
            errors.Add("status", "validation.assets.status");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            update documents.asset_collection set names = @names, descriptions = @descriptions, restricted_to = @unit, status = @status, updated_at = @now,
              row_version = row_version + 1
            where id = @id
            """, new { id, names = new Jsonb(DocJson.Write(Trim(request.Names))), descriptions = new Jsonb(DocJson.Write(Trim(request.Descriptions))),
                unit = request.RestrictedTo, status, now = clock.UtcNow }, ct);
        if (request.RestrictedTo != current.RestrictedTo)
        {
            await RestrictAsync(id, request.RestrictedTo, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AssetCollectionChanged, ResourceType, id.ToString(), TargetOrgUnitId: request.RestrictedTo ?? current.RestrictedTo,
            Changes: new
            {
                before = new { current.Names, current.RestrictedTo, current.Status },
                after = new { names = request.Names, restrictedTo = request.RestrictedTo, status },
            }), ct);
        return await CollectionViewAsync(id, ct);
    }

    /// <summary>The collection for a reader, or not found; a restricted collection's content is denied to the rest.</summary>
    private async Task<(CollectionRow Collection, bool Manager)> ReadableAsync(Guid id, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        var collection = await CollectionAsync(id, ct);
        var manager = await ManagerAsync(ct);
        if (manager)
        {
            return (collection, true);
        }
        if (collection.Status != "active")
        {
            throw AppException.NotFound();
        }
        if (!await ReadsAsync(collection, ct))
        {
            throw await GuestAsync(ct) ? AppException.NotFound() : AppException.Denied("authz.denied.assets_restricted");
        }
        return (collection, false);
    }

    // ---- Assets ----

    private sealed record AssetRow(Guid Id, Guid Collection, Dictionary<string, string> Names, Dictionary<string, string> Notes, string Key, string ContentType,
        string FileName, long Size, int? Width, int? Height, DateTimeOffset? RightsExpireAt, DateTimeOffset? ExpiredAt, string Status, int Version, DateTimeOffset CreatedAt);

    private const string AssetColumns =
        "id, collection_id, names::text, usage_notes::text, object_key, content_type, file_name, size_bytes, width, height, rights_expire_at, expired_at, status, row_version, created_at";

    private static AssetRow ReadAsset(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), DocJson.Read<Dictionary<string, string>>(r.GetString(2)),
        DocJson.Read<Dictionary<string, string>>(r.GetString(3)), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7), r.Get<int?>("width"), r.Get<int?>("height"),
        r.Get<DateTimeOffset?>("rights_expire_at"), r.Get<DateTimeOffset?>("expired_at"), r.GetString(12), r.GetInt32(13), r.GetFieldValue<DateTimeOffset>(14));

    private async Task<AssetView> ViewAsync(AssetRow a, CancellationToken ct)
    {
        var renditions = await session.QueryAsync("select key, content_type, size_bytes, state from documents.asset_rendition where asset_id = @id order by key",
            new { id = a.Id }, r => new AssetRenditionView(r.GetString(0), r.GetString(1), r.Get<long?>("size_bytes"), r.GetString(3) == "ready"), ct);
        var all = new List<AssetRenditionView> { new(AssetRenditions.Original, a.ContentType, a.Size, true) };
        all.AddRange(renditions);
        return new AssetView(a.Id, a.Collection, a.Names, a.Notes, a.FileName, a.ContentType, a.Size, a.Width, a.Height, a.RightsExpireAt, a.ExpiredAt is not null,
            a.Status, a.Version, a.CreatedAt, all);
    }

    /// <summary>The collection's assets. Readers see current ones only; brand managers also see expired and retired ones.</summary>
    public async Task<IReadOnlyList<AssetView>> AssetsAsync(Guid collectionId, CancellationToken ct)
    {
        var (_, manager) = await ReadableAsync(collectionId, ct);
        var rows = await session.QueryAsync(
            $"select {AssetColumns} from documents.asset where collection_id = @collectionId and (@manager or (status = 'active' and expired_at is null)) order by created_at desc, id",
            new { collectionId, manager }, ReadAsset, ct);
        var list = new List<AssetView>();
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    private async Task<(AssetRow Asset, CollectionRow Collection, bool Manager)> AssetForReaderAsync(Guid id, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        var asset = await session.QuerySingleOrDefaultAsync($"select {AssetColumns} from documents.asset where id = @id", new { id }, ReadAsset, ct)
            ?? throw AppException.NotFound();
        var (collection, manager) = await ReadableAsync(asset.Collection, ct);
        if (!manager && (asset.Status != "active" || asset.ExpiredAt is not null))
        {
            throw AppException.NotFound();
        }
        return (asset, collection, manager);
    }

    public async Task<AssetView> GetAsync(Guid id, CancellationToken ct) => await ViewAsync((await AssetForReaderAsync(id, ct)).Asset, ct);

    /// <summary>
    /// Stores a new asset from the request body: an allowed type within 50 MB and the tenant's upload limit, scanned clean,
    /// with its usage notes and rights expiry. Images get their renditions straight away; a rendition the conversion
    /// service cannot make is marked failed and can be made again later.
    /// </summary>
    public async Task<AssetView> UploadAsync(Guid collectionId, string? fileName, AssetRequest request, Stream body, long? declared, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        await authz.DemandAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct);
        var collection = await CollectionAsync(collectionId, ct);
        if (collection.Status != "active")
        {
            throw AppException.Conflict("conflict.assets.collection_retired");
        }
        var name = DocumentFiles.CleanName(fileName);
        var extension = FileTypes.ExtensionOf(name);
        var rules = await libraries.TenantRulesAsync(ct);
        var errors = new ValidationErrors();
        if (!Types.TryGetValue(extension, out var contentType) || (extension != "svg" && !rules.AllowedExtensions.Contains(extension)))
        {
            errors.Add("file", "validation.assets.file_type");
        }
        var maxBytes = Math.Min(MaxAssetBytes, rules.MaxFileMb * 1024L * 1024L);
        if (declared > maxBytes)
        {
            errors.Add("file", "validation.documents.file_size");
        }
        var names = request.Names is { Count: > 0 } ? request.Names : new Dictionary<string, string> { ["en"] = Path.GetFileNameWithoutExtension(name) };
        Texts(names, "names", MaxNameLength, true, errors);
        Texts(request.UsageNotes, "usageNotes", MaxNotesLength, false, errors);
        if (request.RightsExpireAt is { } expiry && expiry <= clock.UtcNow)
        {
            errors.Add("rightsExpireAt", "validation.assets.rights_expiry");
        }
        errors.ThrowIfAny();
        await files.QuotaAsync(declared ?? 0, ct);

        var id = Ids.New();
        var key = $"assets/{collectionId:N}/{id:N}/original";
        var (hash, size) = await files.StoreAsync(key, body, contentType!, maxBytes, ct);
        byte[] bytes;
        try
        {
            await files.QuotaAsync(size, ct);
            if (await files.ScanStoredAsync(key, ct) != ScanStates.Clean)
            {
                throw AppException.Conflict("conflict.assets.not_clean");
            }
            await using var stored = await storage.OpenReadAsync(key, ct) ?? throw AppException.NotFound();
            using var buffer = new MemoryStream();
            await stored.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
            if (extension == "svg" && !ImageSizes.LooksLikeSvg(bytes))
            {
                throw AppException.Validation("file", "validation.assets.file_type");
            }
        }
        catch
        {
            await storage.DeleteAsync(key, CancellationToken.None);
            throw;
        }
        var (width, height) = ImageSizes.Read(bytes, contentType!);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into documents.asset (tenant_id, id, collection_id, names, usage_notes, object_key, content_type, file_name, size_bytes, sha256, width, height,
              scan_state, rights_expire_at, created_at, created_by, updated_at)
            values (platform.current_tenant_id(), @id, @collectionId, @names, @notes, @key, @contentType, @name, @size, @hash, @width, @height, 'clean', @expiry,
              @now, @user, @now)
            """, new
            {
                id, collectionId, names = new Jsonb(DocJson.Write(Trim(names))), notes = new Jsonb(DocJson.Write(Trim(request.UsageNotes))), key, contentType, name,
                size, hash, width, height, expiry = request.RightsExpireAt, now, user = context.RequireUser(),
            }, ct);
        if (IsImage(contentType!))
        {
            await RenderAsync(id, collectionId, bytes, contentType!, width, height, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AssetUploaded, "asset", id.ToString(), TargetOrgUnitId: collection.RestrictedTo,
            Changes: new { collection = collectionId, contentType, size, rightsExpireAt = request.RightsExpireAt }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Makes the image's renditions through the conversion service: PNG at each set width that is not wider than the
    /// source (the smallest always), the SVG itself for an SVG source, and a PDF with the image on a page. The service
    /// draws in RGB, so no CMYK PDF is made.
    /// </summary>
    private async Task RenderAsync(Guid id, Guid collectionId, byte[] bytes, string contentType, int? width, int? height, CancellationToken ct)
    {
        await session.ExecAsync("delete from documents.asset_rendition where asset_id = @id", new { id }, ct);
        var source = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
        foreach (var w in AssetRenditions.PngWidths.Where((w, i) => i == 0 || width is not { } sw || w <= sw))
        {
            var h = width is { } sw2 && height is { } sh && sw2 > 0 ? Math.Max(1, (int)Math.Round((double)w * sh / sw2)) : w;
            var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{{margin:0;background:transparent}}img{{display:block;width:{w}px;height:{h}px;object-fit:contain}}</style></head><body><img src=\"{source}\" alt=\"\"></body></html>";
            await using var png = await converter.HtmlToPngAsync(html, w, h, ct);
            await KeepAsync(id, collectionId, AssetRenditions.Png(w), "image/png", png, ct);
        }
        if (contentType == "image/svg+xml")
        {
            await session.ExecAsync(
                """
                insert into documents.asset_rendition (tenant_id, asset_id, key, object_key, content_type, size_bytes, state)
                values (platform.current_tenant_id(), @id, 'svg', null, 'image/svg+xml', @size, 'ready')
                """, new { id, size = (long)bytes.Length }, ct);
        }
        var page = $"<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{{margin:0;height:100%}}body{{display:flex;align-items:center;justify-content:center}}img{{max-width:100%;max-height:100%}}</style></head><body><img src=\"{source}\" alt=\"\"></body></html>";
        await using var pdf = await converter.HtmlToPdfAsync(page, ct);
        await KeepAsync(id, collectionId, AssetRenditions.Pdf, "application/pdf", pdf, ct);
    }

    private async Task KeepAsync(Guid id, Guid collectionId, string rendition, string contentType, Stream? content, CancellationToken ct)
    {
        string? key = null;
        long? size = null;
        if (content is not null)
        {
            key = $"assets/{collectionId:N}/{id:N}/{rendition}";
            await using var counted = new HashingStream(content, MaxAssetBytes);
            await storage.PutAsync(key, counted, contentType, ct);
            size = counted.Length;
        }
        await session.ExecAsync(
            """
            insert into documents.asset_rendition (tenant_id, asset_id, key, object_key, content_type, size_bytes, state)
            values (platform.current_tenant_id(), @id, @rendition, @key, @contentType, @size, @state)
            """, new { id, rendition, key, contentType, size, state = key is null ? "failed" : "ready" }, ct);
    }

    /// <summary>Makes an image's renditions again, after the conversion service was not available.</summary>
    public async Task<AssetView> RenderAgainAsync(Guid id, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        await authz.DemandAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct);
        var asset = await session.QuerySingleOrDefaultAsync($"select {AssetColumns} from documents.asset where id = @id for update", new { id }, ReadAsset, ct)
            ?? throw AppException.NotFound();
        if (!IsImage(asset.ContentType))
        {
            throw AppException.Conflict("conflict.assets.no_renditions");
        }
        await using var stored = await storage.OpenReadAsync(asset.Key, ct) ?? throw AppException.NotFound();
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer, ct);
        await RenderAsync(id, asset.Collection, buffer.ToArray(), asset.ContentType, asset.Width, asset.Height, ct);
        return await ViewAsync(asset, ct);
    }

    /// <summary>Changes names, usage notes, rights expiry or status. A new expiry in the future brings an expired asset back.</summary>
    public async Task<AssetView> UpdateAsync(Guid id, AssetRequest request, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        await authz.DemandAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct);
        var asset = await session.QuerySingleOrDefaultAsync($"select {AssetColumns} from documents.asset where id = @id for update", new { id }, ReadAsset, ct)
            ?? throw AppException.NotFound();
        if (request.Version != asset.Version)
        {
            throw AppException.Conflict("conflict.assets.stale");
        }
        var errors = new ValidationErrors();
        Texts(request.Names, "names", MaxNameLength, true, errors);
        Texts(request.UsageNotes, "usageNotes", MaxNotesLength, false, errors);
        var expiry = request.ClearRightsExpiry ? null : request.RightsExpireAt ?? asset.RightsExpireAt;
        if (request.RightsExpireAt is { } e && e <= clock.UtcNow)
        {
            errors.Add("rightsExpireAt", "validation.assets.rights_expiry");
        }
        var status = request.Status ?? asset.Status;
        if (status is not ("active" or "retired"))
        {
            errors.Add("status", "validation.assets.status");
        }
        errors.ThrowIfAny();
        var expired = expiry is { } x && x <= clock.UtcNow ? asset.ExpiredAt : null;
        await session.ExecAsync(
            """
            update documents.asset set names = @names, usage_notes = @notes, rights_expire_at = @expiry, expired_at = @expired, status = @status, updated_at = @now,
              row_version = row_version + 1
            where id = @id
            """, new { id, names = new Jsonb(DocJson.Write(Trim(request.Names))), notes = new Jsonb(DocJson.Write(Trim(request.UsageNotes))), expiry, expired, status,
                now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AssetChanged, "asset", id.ToString(), Changes: new
        {
            before = new { asset.Names, asset.RightsExpireAt, asset.Status },
            after = new { names = request.Names, rightsExpireAt = expiry, status },
        }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// A rendition as a file, for anyone who reads the collection while the asset is current (brand managers always).
    /// Every download is recorded with the person, asset and rendition (US5 scenario 4).
    /// </summary>
    public async Task<(Stream Content, string ContentType, string FileName)> DownloadAsync(Guid id, string rendition, CancellationToken ct)
    {
        var (asset, _, _) = await AssetForReaderAsync(id, ct);
        var (content, type, name) = await OpenAsync(asset, rendition, ct);
        await session.ExecAsync(
            "insert into documents.asset_download (tenant_id, id, asset_id, rendition, user_id, at) values (platform.current_tenant_id(), @did, @id, @rendition, @user, @now)",
            new { did = Ids.New(), id, rendition, user = context.RequireUser(), now = clock.UtcNow }, ct);
        return (content, type, name);
    }

    /// <summary>The small PNG for the portal's grid and the picker; not a download, so not recorded.</summary>
    public async Task<(Stream Content, string ContentType)> ThumbnailAsync(Guid id, CancellationToken ct)
    {
        var (asset, _, _) = await AssetForReaderAsync(id, ct);
        var (content, type, _) = await OpenAsync(asset, AssetRenditions.Png(AssetRenditions.PngWidths[0]), ct);
        return (content, type);
    }

    private async Task<(Stream Content, string ContentType, string FileName)> OpenAsync(AssetRow asset, string rendition, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(asset.FileName);
        if (rendition == AssetRenditions.Original)
        {
            return (await storage.OpenReadAsync(asset.Key, ct) ?? throw AppException.NotFound(), asset.ContentType, asset.FileName);
        }
        if (!AssetRenditions.Known(rendition))
        {
            throw AppException.NotFound();
        }
        var row = await session.QuerySingleOrDefaultAsync(
            "select object_key, content_type from documents.asset_rendition where asset_id = @id and key = @rendition and state = 'ready'",
            new { id = asset.Id, rendition }, r => (Key: r.Get<string>("object_key"), Type: r.GetString(1)), ct);
        if (row.Type is null)
        {
            throw AppException.NotFound();
        }
        var key = row.Key ?? asset.Key;
        var extension = row.Type switch { "image/png" => "png", "application/pdf" => "pdf", "image/svg+xml" => "svg", _ => "bin" };
        return (await storage.OpenReadAsync(key, ct) ?? throw AppException.NotFound(), row.Type, $"{baseName}-{rendition}.{extension}");
    }

    /// <summary>Current images the caller reads, for the page builder's picker; expired and retired ones never show.</summary>
    public async Task<IReadOnlyList<AssetPick>> PickerAsync(string? q, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        var collections = await CollectionsAsync(ct);
        var readable = collections.Where(c => c.CanRead && c.Status == "active").ToDictionary(c => c.Id);
        if (readable.Count == 0)
        {
            return [];
        }
        var search = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
        var rows = await session.QueryAsync(
            """
            select id, collection_id, names::text, width, height from documents.asset
            where collection_id = any(@ids) and status = 'active' and expired_at is null and content_type like 'image/%'
              and (@search::text is null or names::text ilike @search)
            order by created_at desc, id limit 100
            """, new { ids = readable.Keys.ToArray(), search }, r => (Id: r.GetGuid(0), Collection: r.GetGuid(1), Names: DocJson.Read<Dictionary<string, string>>(r.GetString(2)),
                Width: r.Get<int?>("width"), Height: r.Get<int?>("height")), ct);
        return rows.Select(r => new AssetPick(r.Id, r.Names, readable[r.Collection].Names, r.Width, r.Height)).ToList();
    }

    /// <summary>
    /// Whether the asset is a current image the caller reads, for modules that show it (a page's image block): the
    /// reader then gets its rendition through the portal's own checks.
    /// </summary>
    public async Task<bool> UsableAsync(Guid assetId, CancellationToken ct)
    {
        try
        {
            var (asset, _, _) = await AssetForReaderAsync(assetId, ct);
            return Usable(asset);
        }
        catch (AppException)
        {
            return false;
        }
    }

    private static bool Usable(AssetRow asset) => IsImage(asset.ContentType) && asset.ExpiredAt is null && asset.Status == "active";

    /// <summary>
    /// The image a page shows: the largest PNG rendition of a current image the reader reads. Not a download, so not
    /// recorded; an asset whose rights ended is gone from pages for everyone, brand managers included.
    /// </summary>
    public async Task<(Stream Content, string ContentType)> ImageAsync(Guid id, CancellationToken ct)
    {
        var (asset, _, _) = await AssetForReaderAsync(id, ct);
        if (!Usable(asset))
        {
            throw AppException.NotFound();
        }
        var ready = (await session.QueryAsync("select key from documents.asset_rendition where asset_id = @id and state = 'ready'", new { id }, r => r.GetString(0), ct))
            .ToHashSet(StringComparer.Ordinal);
        var key = AssetRenditions.PngWidths.Reverse().Select(AssetRenditions.Png).FirstOrDefault(ready.Contains) ?? throw AppException.NotFound();
        var (content, type, _) = await OpenAsync(asset, key, ct);
        return (content, type);
    }

    // ---- Access requests (US5 scenario 3) ----

    public async Task<AssetCollectionView> RequestAccessAsync(Guid collectionId, AccessRequestBody body, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        var user = context.RequireUser();
        var collection = await CollectionAsync(collectionId, ct);
        if (collection.Status != "active")
        {
            throw AppException.NotFound();
        }
        if (await GuestAsync(ct))
        {
            throw AppException.NotFound();
        }
        if (await ReadsAsync(collection, ct))
        {
            throw AppException.Conflict("conflict.assets.already_readable");
        }
        var reason = body.Reason?.Trim() ?? "";
        if (reason.Length is 0 or > MaxReasonLength)
        {
            throw AppException.Validation("reason", "validation.assets.reason");
        }
        if (await session.ScalarAsync<bool>("select exists (select 1 from documents.asset_access_request where collection_id = @collectionId and user_id = @user and status = 'pending')",
                new { collectionId, user }, ct))
        {
            throw AppException.Conflict("conflict.assets.already_requested");
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into documents.asset_access_request (tenant_id, id, collection_id, user_id, reason, created_at)
            values (platform.current_tenant_id(), @id, @collectionId, @user, @reason, @now)
            """, new { id, collectionId, user, reason, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AssetAccessRequested, ResourceType, collectionId.ToString(), TargetOrgUnitId: collection.RestrictedTo,
            Changes: new { request = id }), ct);
        var managers = await acl.TenantHoldersAsync(Permissions.AssetsManage, ct);
        if (managers.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(managers, NotificationCategories.ApprovalWaiting, AccessRequestedTemplate, "/assets/requests",
                new Dictionary<string, string> { ["collection"] = collection.Names.GetValueOrDefault("en") ?? collection.Names.Values.FirstOrDefault() ?? "" },
                DedupeKey: $"assets.access_requested:{id}"), ct);
        }
        return await CollectionViewAsync(collectionId, ct);
    }

    /// <summary>Pending requests first, then the last 100 decided, for brand managers.</summary>
    public async Task<IReadOnlyList<AssetAccessRequestView>> RequestsAsync(CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        await authz.DemandAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct);
        var rows = await session.QueryAsync(
            """
            select r.id, r.collection_id, c.names::text, r.user_id, r.reason, r.status, r.created_at, r.decided_at
            from documents.asset_access_request r join documents.asset_collection c on c.id = r.collection_id
            order by (r.status = 'pending') desc, r.created_at desc limit 200
            """, null, r => (Id: r.GetGuid(0), Collection: r.GetGuid(1), Names: DocJson.Read<Dictionary<string, string>>(r.GetString(2)), User: r.GetGuid(3),
                Reason: r.GetString(4), Status: r.GetString(5), At: r.GetFieldValue<DateTimeOffset>(6), Decided: r.Get<DateTimeOffset?>("decided_at")), ct);
        var people = await users.GetManyAsync(rows.Select(r => r.User).ToList(), ct);
        return rows.Select(r => new AssetAccessRequestView(r.Id, r.Collection, r.Names, r.User, people.TryGetValue(r.User, out var u) ? u.DisplayName : "", r.Reason, r.Status,
            r.At, r.Decided)).ToList();
    }

    internal async Task<IReadOnlyList<(Guid Id, Dictionary<string, string> Names, DateTimeOffset At)>> PendingAsync(CancellationToken ct) =>
        await session.QueryAsync(
            """
            select r.id, c.names::text, r.created_at from documents.asset_access_request r join documents.asset_collection c on c.id = r.collection_id
            where r.status = 'pending' order by r.created_at limit 50
            """, null, r => (r.GetGuid(0), DocJson.Read<Dictionary<string, string>>(r.GetString(1)), r.GetFieldValue<DateTimeOffset>(2)), ct);

    /// <summary>Approves (the person reads the collection from now on) or declines a pending request; the person is told.</summary>
    public async Task<AssetAccessRequestView> DecideAsync(Guid requestId, AccessDecision decision, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        await authz.DemandAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct);
        if (decision.Decision is not ("approve" or "decline"))
        {
            throw AppException.Validation("decision", "validation.assets.decision");
        }
        var row = await session.QuerySingleOrDefaultAsync(
            "select collection_id, user_id, status from documents.asset_access_request where id = @requestId for update", new { requestId },
            r => (Collection: r.GetGuid(0), User: r.GetGuid(1), Status: r.GetString(2)), ct);
        if (row.Status is null)
        {
            throw AppException.NotFound();
        }
        if (row.Status != "pending")
        {
            throw AppException.Conflict("conflict.assets.request_decided");
        }
        var collection = await CollectionAsync(row.Collection, ct);
        var status = decision.Decision == "approve" ? "approved" : "declined";
        await session.ExecAsync("update documents.asset_access_request set status = @status, decided_at = @now, decided_by = @by where id = @requestId",
            new { requestId, status, now = clock.UtcNow, by = context.RequireUser() }, ct);
        if (status == "approved" && collection.RestrictedTo is not null)
        {
            await acl.AddAclEntryAsync(ResourceType, collection.Id, "user", row.User, Permissions.AssetsView, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AssetAccessDecided, ResourceType, collection.Id.ToString(), TargetOrgUnitId: collection.RestrictedTo,
            Changes: new { request = requestId, user = row.User, decision = status }), ct);
        await notifications.SendAsync(new NotificationRequest([row.User], NotificationCategories.RequestUpdate,
            status == "approved" ? AccessApprovedTemplate : AccessDeclinedTemplate, "/assets",
            new Dictionary<string, string> { ["collection"] = collection.Names.GetValueOrDefault("en") ?? collection.Names.Values.FirstOrDefault() ?? "" },
            DedupeKey: $"assets.access_decided:{requestId}"), ct);
        return (await RequestsAsync(ct)).Single(r => r.Id == requestId);
    }

    // ---- Download report (US5 scenario 4) ----

    public async Task<AssetDownloadReport> DownloadsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        await DemandPortalAsync(ct);
        await authz.DemandAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct);
        var end = to ?? clock.UtcNow;
        var start = from ?? end.AddDays(-90);
        if (start > end || end - start > TimeSpan.FromDays(366))
        {
            throw AppException.Validation("from", "validation.assets.period");
        }
        var rows = await session.QueryAsync(
            """
            select d.asset_id, a.names::text, d.rendition, d.user_id, d.at
            from documents.asset_download d join documents.asset a on a.id = d.asset_id
            where d.at >= @start and d.at < @end order by d.at desc limit 1000
            """, new { start, end }, r => (Asset: r.GetGuid(0), Names: DocJson.Read<Dictionary<string, string>>(r.GetString(1)), Rendition: r.GetString(2),
                User: r.GetGuid(3), At: r.GetFieldValue<DateTimeOffset>(4)), ct);
        var totals = await session.QueryAsync(
            """
            select d.asset_id, a.names::text, d.rendition, count(*)::int, count(distinct d.user_id)::int
            from documents.asset_download d join documents.asset a on a.id = d.asset_id
            where d.at >= @start and d.at < @end group by d.asset_id, a.names::text, d.rendition order by count(*) desc, d.asset_id, d.rendition limit 500
            """, new { start, end }, r => new AssetDownloadCount(r.GetGuid(0), DocJson.Read<Dictionary<string, string>>(r.GetString(1)), r.GetString(2), r.GetInt32(3),
                r.GetInt32(4)), ct);
        var people = await users.GetManyAsync(rows.Select(r => r.User).Distinct().ToList(), ct);
        return new AssetDownloadReport(
            rows.Select(r => new AssetDownloadRow(r.Asset, r.Names, r.Rendition, r.User, people.TryGetValue(r.User, out var u) ? u.DisplayName : "", r.At)).ToList(), totals);
    }

    // ---- Usage rights expiry (US5 scenario 2) ----

    /// <summary>
    /// Marks assets whose usage rights ended: they leave the portal and the picker for everyone but brand managers, and
    /// <see cref="AssetRightsExpired"/> lets the modules that show them tell the owners of what uses them.
    /// </summary>
    public async Task<int> ExpireAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.DocsAssetPortal, ct))
        {
            return 0;
        }
        var due = await session.QueryAsync(
            "select id, collection_id from documents.asset where expired_at is null and rights_expire_at <= @now order by rights_expire_at limit @limit for update skip locked",
            new { now, limit }, r => (Id: r.GetGuid(0), Collection: r.GetGuid(1)), ct);
        foreach (var (id, collection) in due)
        {
            await session.ExecAsync("update documents.asset set expired_at = @now where id = @id", new { id, now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.AssetExpired, "asset", id.ToString(), Changes: new { collection }), ct);
            await outbox.EnqueueAsync(new AssetRightsExpired(id), ct);
        }
        return due.Count;
    }
}

/// <summary>Width and height from an image's own header, without decoding it; null when the format does not say.</summary>
internal static partial class ImageSizes
{
    public static (int? Width, int? Height) Read(byte[] bytes, string contentType)
    {
        try
        {
            return contentType switch
            {
                "image/png" when bytes.Length >= 24 => (BigEndian(bytes, 16), BigEndian(bytes, 20)),
                "image/gif" when bytes.Length >= 10 => (bytes[6] | bytes[7] << 8, bytes[8] | bytes[9] << 8),
                "image/jpeg" => Jpeg(bytes),
                "image/svg+xml" => Svg(bytes),
                _ => (null, null),
            };
        }
        catch (IndexOutOfRangeException)
        {
            return (null, null);
        }
    }

    private static int BigEndian(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];

    private static (int?, int?) Jpeg(byte[] b)
    {
        var at = 2;
        while (at + 9 < b.Length)
        {
            if (b[at] != 0xFF)
            {
                return (null, null);
            }
            var marker = b[at + 1];
            var length = b[at + 2] << 8 | b[at + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return (b[at + 7] << 8 | b[at + 8], b[at + 5] << 8 | b[at + 6]);
            }
            at += 2 + length;
        }
        return (null, null);
    }

    private static (int?, int?) Svg(byte[] b)
    {
        var head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 4096));
        var tag = SvgTag().Match(head);
        if (!tag.Success)
        {
            return (null, null);
        }
        var w = Number(WidthAttr().Match(tag.Value));
        var h = Number(HeightAttr().Match(tag.Value));
        if (w is not null && h is not null)
        {
            return (w, h);
        }
        var box = ViewBox().Match(tag.Value);
        return box.Success && double.TryParse(box.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vw)
            && double.TryParse(box.Groups[4].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vh)
            ? ((int)Math.Round(vw), (int)Math.Round(vh)) : (null, null);
    }

    private static int? Number(Match m) =>
        m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? (int)Math.Round(v) : null;

    /// <summary>An SVG file is text whose first element is <c>svg</c>.</summary>
    public static bool LooksLikeSvg(byte[] b) => SvgTag().IsMatch(System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 4096)));

    [GeneratedRegex(@"<svg\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SvgTag();

    [GeneratedRegex(@"\swidth\s*=\s*[""']\s*([0-9.]+)(px)?\s*[""']", RegexOptions.IgnoreCase)]
    private static partial Regex WidthAttr();

    [GeneratedRegex(@"\sheight\s*=\s*[""']\s*([0-9.]+)(px)?\s*[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HeightAttr();

    [GeneratedRegex(@"viewBox\s*=\s*[""']\s*(-?[0-9.]+)[\s,]+(-?[0-9.]+)[\s,]+([0-9.]+)[\s,]+([0-9.]+)\s*[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ViewBox();
}

/// <summary>Brand managers see pending access requests in their task inbox (US5 scenario 3), linking to the requests page.</summary>
internal sealed class AssetAccessTasks(Assets assets, IAuthorizationService authz, ITenantContext context, IEntitlementService entitlements)
    : TeamBeats.Modules.Workflow.Contracts.IPersonalTaskSource
{
    public async Task<IReadOnlyList<TeamBeats.Modules.Workflow.Contracts.PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default)
    {
        if (context.ActorId != userId || !await entitlements.IsEnabledAsync(Capabilities.DocsAssetPortal, ct)
            || !(await authz.CheckAsync(Permissions.AssetsManage, AuthzTarget.Tenant, ct)).Allowed)
        {
            return [];
        }
        return (await assets.PendingAsync(ct)).Select(p => new TeamBeats.Modules.Workflow.Contracts.PersonalTask("asset_access", p.Id,
            p.Names.ToDictionary(n => n.Key, n => n.Value), "/assets/requests", p.At, null)).ToList();
    }
}

/// <summary>Authorization reads an asset collection's owning unit and visibility from here (constitution III).</summary>
internal sealed class AssetCollectionDescriptorProvider(IDbSession session) : IResourceDescriptorProvider
{
    public string ResourceType => Assets.ResourceType;

    public string ManagePermission => Permissions.AssetsManage;

    public async Task<ResourceDescriptor?> DescribeAsync(Guid resourceId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select id, restricted_to from documents.asset_collection where id = @resourceId", new { resourceId },
            r => (Id: r.GetGuid(0), Unit: r.Get<Guid?>("restricted_to")), ct);
        return row.Id == Guid.Empty ? null : Assets.Describe(row.Id, row.Unit);
    }
}
