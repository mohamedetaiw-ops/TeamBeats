using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

public sealed record VariantInput(string? Title, string? Summary, BodyDocument? Body);

public sealed record ItemRequest(
    Guid? SiteId,
    string? Type,
    ContentAudience? Audience,
    Guid? OwnerId,
    Guid? BackupOwnerId,
    DateOnly? ReviewDueOn,
    string? Classification,
    bool? Pinned,
    bool? Featured,
    IReadOnlyDictionary<string, VariantInput>? Variants,
    string? ChangeNote,
    int? Version);

/// <summary>When to go live and when to expire, as local times in the site's time zone (US1 scenario 3).</summary>
public sealed record PublishRequest(DateTime? PublishAt, DateTime? ExpiresAt, bool? Material);

public sealed record ItemView(
    Guid Id,
    Guid SiteId,
    string Type,
    string Status,
    string Classification,
    string PrimaryLocale,
    ContentAudience Audience,
    ContentPerson? Owner,
    ContentPerson? BackupOwner,
    DateTimeOffset? ReviewDueAt,
    bool Pinned,
    bool Featured,
    int? PublishedVersion,
    int? DraftVersion,
    DateTimeOffset? PublishAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ExpiredAt,
    IReadOnlyDictionary<string, ContentVariant> Variants,
    string? ChangeNote,
    ContentPerson CreatedBy,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>What a reader sees: the published version in their language, or the fallback with a note (US5 scenario 2).</summary>
public sealed record ReaderView(
    Guid Id,
    Guid SiteId,
    string Type,
    string Locale,
    bool Fallback,
    IReadOnlyList<string> Locales,
    string Title,
    string? Summary,
    BodyDocument Body,
    int Version,
    DateTimeOffset PublishedAt,
    DateTimeOffset UpdatedAt,
    bool Expired,
    bool Pinned,
    bool Featured);

public sealed record VersionSummary(int Number, ContentPerson Author, ContentPerson? Approver, string? ChangeNote, bool Material, IReadOnlyList<string> Locales,
    DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, bool Current, bool Draft);

internal sealed record ItemRow(
    Guid Id, Guid SiteId, string Type, string Classification, string PrimaryLocale, Guid? OwnerId, Guid? BackupOwnerId, DateTimeOffset? ReviewDueAt,
    ContentAudience Audience, bool Pinned, bool Featured, string Status, int? PublishedVersion, int? DraftVersion, DateTimeOffset? PublishAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? PublishedAt, DateTimeOffset? ExpiredAt, Guid CreatedBy, DateTimeOffset UpdatedAt, int Version);

internal sealed record VersionRow(int Number, IReadOnlyDictionary<string, ContentVariant> Variants, string? ChangeNote, bool Material, Guid Author,
    Guid? Approver, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

/// <summary>
/// Content items of spec 009 R1: drafts with locale variants, publish now or on a schedule in the site's time zone,
/// expiry, unpublish and archive, with every transition audited (FR-002). A published version never changes; editing
/// a published item starts the next version while the published one stays live (US1 scenario 5, FR-009). Authors
/// write their own drafts (<c>content.author</c>), editors any draft of the site (<c>content.edit</c>), publishers
/// put them live (<c>content.publish</c>), all at the site's org unit through central authorization.
/// </summary>
public sealed class Items(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    ITenantSettingsReader tenantSettings,
    ITenantMemberships memberships,
    IUserDirectory users,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    RequestLocale requestLocale,
    Sites sites)
{
    public static readonly IReadOnlySet<string> Classifications = new HashSet<string>(StringComparer.Ordinal) { "public", "internal", "confidential", "restricted" };

    private const string Columns =
        """
        id, site_id, type, classification, primary_locale, owner_user_id, backup_owner_user_id, review_due_at, audience::text, pinned, featured, status,
        published_version, draft_version, publish_at, expires_at, published_at, expired_at, created_by, updated_at, row_version
        """;

    private static ItemRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Get<Guid?>("owner_user_id"), r.Get<Guid?>("backup_owner_user_id"),
        r.Get<DateTimeOffset?>("review_due_at"), ContentJson.Read<ContentAudience>(r.GetString(8)), r.GetBoolean(9), r.GetBoolean(10), r.GetString(11),
        r.Get<int?>("published_version"), r.Get<int?>("draft_version"), r.Get<DateTimeOffset?>("publish_at"), r.Get<DateTimeOffset?>("expires_at"),
        r.Get<DateTimeOffset?>("published_at"), r.Get<DateTimeOffset?>("expired_at"), r.GetGuid(18), r.GetFieldValue<DateTimeOffset>(19), r.GetInt32(20));

    private const string VersionColumns = "number, variants::text, change_note, material, author_user_id, approver_user_id, created_at, updated_at, published_at";

    private static VersionRow ReadVersion(NpgsqlDataReader r) => new(r.GetInt32(0), ContentJson.Read<Dictionary<string, ContentVariant>>(r.GetString(1)),
        r.Get<string?>("change_note"), r.GetBoolean(3), r.GetGuid(4), r.Get<Guid?>("approver_user_id"), r.GetFieldValue<DateTimeOffset>(6),
        r.GetFieldValue<DateTimeOffset>(7), r.Get<DateTimeOffset?>("published_at"));

    internal Task<ItemRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from content.content_item where id = @id" + (forUpdate ? " for update" : ""), new { id }, Read, ct);

    internal Task<VersionRow?> VersionAsync(Guid item, int number, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {VersionColumns} from content.content_version where item_id = @item and number = @number",
            new { item, number }, ReadVersion, ct);

    private async Task<bool> HoldsAsync(string permission, SiteRow site, CancellationToken ct) => (await authz.CheckAsync(permission, site.Target, ct)).Allowed;

    public const string ResourceType = "content.item";

    /// <summary>
    /// Reading is <c>content.view</c> on the item as a resource (spec 009 section 3). Public and internal items are
    /// visible to every active member of the tenant; confidential and restricted items need a covering role grant at the
    /// site's unit or an entry on the item. Targeting never enters this check.
    /// </summary>
    private async Task<bool> MayReadAsync(ItemRow item, SiteRow site, CancellationToken ct)
    {
        var visibility = item.Classification is "public" or "internal" ? ResourceVisibility.Tenant : ResourceVisibility.Restricted;
        var descriptor = new ResourceDescriptor(ResourceType, item.Id, site.OrgUnitId, visibility, item.Classification);
        return (await authz.CheckAsync(Permissions.ContentView, AuthzTarget.Resource(descriptor), ct)).Allowed;
    }

    private async Task<bool> MayWriteAsync(SiteRow site, ItemRow? item, Guid user, CancellationToken ct) =>
        await HoldsAsync(Permissions.ContentEdit, site, ct)
        || (await HoldsAsync(Permissions.ContentAuthor, site, ct) && (item is null || item.CreatedBy == user));

    /// <summary>Anyone who works on content in the site may open its items for editing; others get not found.</summary>
    private async Task<(ItemRow Item, SiteRow Site)> ManagedAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        var item = await RowAsync(id, forUpdate, ct) ?? throw AppException.NotFound();
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        if (!await HoldsAsync(Permissions.ContentAuthor, site, ct) && !await HoldsAsync(Permissions.ContentEdit, site, ct)
            && !await HoldsAsync(Permissions.ContentPublish, site, ct))
        {
            throw AppException.NotFound();
        }
        return (item, site);
    }

    // ---- Validation ----

    private async Task<Dictionary<string, ContentVariant>> CleanVariantsAsync(IReadOnlyDictionary<string, VariantInput>? input, string primary,
        IReadOnlyDictionary<string, ContentVariant>? previous, ValidationErrors errors, CancellationToken ct)
    {
        var enabled = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        var embeds = await sites.EmbedHostsAsync(ct);
        var result = new Dictionary<string, ContentVariant>(StringComparer.Ordinal);
        if (input is not { Count: > 0 })
        {
            errors.Add("variants", "validation.content.variants");
            return result;
        }
        foreach (var (locale, v) in input)
        {
            if (!enabled.Contains(locale) || v is null)
            {
                errors.Add("variants", "validation.content.variant_locale");
                continue;
            }
            var title = v.Title?.Trim() ?? string.Empty;
            if (title.Length is 0 or > ContentLimits.TitleMax)
            {
                errors.Add($"variants.{locale}.title", "validation.content.title");
            }
            var summary = string.IsNullOrWhiteSpace(v.Summary) ? null : v.Summary.Trim();
            if (summary is { Length: > ContentLimits.SummaryMax })
            {
                errors.Add($"variants.{locale}.summary", "validation.content.summary");
            }
            BodyValidator.Validate(v.Body, embeds, $"variants.{locale}.body", errors);
            result[locale] = new ContentVariant(title, summary, v.Body ?? BodyDocument.Empty);
        }
        if (!result.ContainsKey(primary))
        {
            errors.Add($"variants.{primary}", "validation.content.primary_variant");
        }
        return MarkOutOfDate(result, primary, previous);
    }

    /// <summary>
    /// A variant falls behind when the primary locale changed and it did not (US5 scenario 5). Changing the variant
    /// clears the flag; leaving both alone keeps it.
    /// </summary>
    internal static Dictionary<string, ContentVariant> MarkOutOfDate(Dictionary<string, ContentVariant> next, string primary,
        IReadOnlyDictionary<string, ContentVariant>? previous)
    {
        if (previous is null || !next.TryGetValue(primary, out var source))
        {
            return next;
        }
        var primaryChanged = !previous.TryGetValue(primary, out var before) || !SameText(before, source);
        foreach (var locale in next.Keys.Where(l => l != primary).ToList())
        {
            var variant = next[locale];
            var old = previous.GetValueOrDefault(locale);
            var changed = old is null || !SameText(old, variant);
            next[locale] = variant with { OutOfDate = !changed && (primaryChanged || old!.OutOfDate) };
        }
        next[primary] = source with { OutOfDate = false };
        return next;
    }

    private static bool SameText(ContentVariant a, ContentVariant b) =>
        a.Title == b.Title && a.Summary == b.Summary && ContentJson.Write(a.Body) == ContentJson.Write(b.Body);

    private async Task ValidatePeopleAsync(ItemRequest request, ValidationErrors errors, CancellationToken ct)
    {
        foreach (var (field, person) in new[] { ("ownerId", request.OwnerId), ("backupOwnerId", request.BackupOwnerId) })
        {
            if (person is { } p && (await memberships.GetAsync(p, ct))?.Status != MembershipStatus.Active)
            {
                errors.Add(field, "validation.content.owner");
            }
        }
        if (request.OwnerId is { } o && o == request.BackupOwnerId)
        {
            errors.Add("backupOwnerId", "validation.content.owner");
        }
    }

    private static void ValidateSettings(ItemRequest request, ValidationErrors errors)
    {
        if (request.Classification is { } c && !Classifications.Contains(c))
        {
            errors.Add("classification", "validation.content.classification");
        }
        if (request.ChangeNote is { Length: > ContentLimits.ChangeNoteMax })
        {
            errors.Add("changeNote", "validation.content.change_note");
        }
        AudienceRules.Validate(request.Audience, "audience", errors);
    }

    // ---- Create and edit ----

    public async Task<ItemView> CreateAsync(ItemRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var user = context.RequireUser();
        var site = request.SiteId is { } siteId ? await sites.RowAsync(siteId, false, ct) ?? throw AppException.NotFound() : await sites.DefaultAsync(ct);
        if (!await MayWriteAsync(site, null, user, ct))
        {
            throw AppException.Denied();
        }
        if (site.Status != "active")
        {
            throw AppException.Conflict("conflict.content.site_archived");
        }
        var errors = new ValidationErrors();
        var type = request.Type ?? string.Empty;
        var settings = await sites.TypeAsync(type, ct);
        if (settings is null)
        {
            errors.Add("type", "validation.content.type");
        }
        var primary = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        if (request.Variants is { Count: > 0 } given && !given.ContainsKey(primary))
        {
            primary = given.Keys.First();
        }
        ValidateSettings(request, errors);
        await ValidatePeopleAsync(request, errors, ct);
        var variants = await CleanVariantsAsync(request.Variants, primary, null, errors, ct);
        errors.ThrowIfAny();

        var id = Ids.New();
        var now = clock.UtcNow;
        var reviewDue = request.ReviewDueOn is { } on ? ReviewAt(on, site) : settings!.ReviewMonths is { } months ? now.AddMonths(months) : (DateTimeOffset?)null;
        await session.ExecAsync(
            """
            insert into content.content_item (tenant_id, id, site_id, type, classification, primary_locale, owner_user_id, backup_owner_user_id, review_due_at,
              audience, pinned, featured, status, draft_version, created_at, created_by, updated_at)
            values (platform.current_tenant_id(), @id, @site, @type, @classification, @primary, @owner, @backup, @reviewDue,
              @audience, @pinned, @featured, 'draft', 1, @now, @user, @now)
            """, new
            {
                id, site = site.Id, type, classification = request.Classification ?? "internal", primary, owner = request.OwnerId, backup = request.BackupOwnerId,
                reviewDue, audience = new Jsonb(ContentJson.Write(request.Audience ?? site.DefaultAudience)), pinned = request.Pinned ?? false,
                featured = request.Featured ?? false, now, user,
            }, ct);
        await InsertVersionAsync(id, 1, variants, request.ChangeNote, user, now, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentCreated, "content_item", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { site = site.Id, type, version = 1 }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    private static DateTimeOffset ReviewAt(DateOnly on, SiteRow site) =>
        new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(on.ToDateTime(new TimeOnly(9, 0)), TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone)), TimeSpan.Zero);

    private Task InsertVersionAsync(Guid item, int number, IReadOnlyDictionary<string, ContentVariant> variants, string? note, Guid user, DateTimeOffset now,
        CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into content.content_version (tenant_id, item_id, number, variants, change_note, author_user_id, created_at, updated_at)
            values (platform.current_tenant_id(), @item, @number, @variants, @note, @user, @now, @now)
            """, new { item, number, variants = new Jsonb(ContentJson.Write(variants)), note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), user, now }, ct);

    /// <summary>
    /// Saves the draft. On an item with a live version and no draft, this starts the next version; the live version
    /// stays as it is until the new one is published (US1 scenario 5).
    /// </summary>
    public async Task<ItemView> UpdateAsync(Guid id, ItemRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (item, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (!await MayWriteAsync(site, item, user, ct))
        {
            throw AppException.Denied();
        }
        if (request.Version is { } expected && expected != item.Version)
        {
            throw AppException.Conflict("conflict.content.changed");
        }
        if (item.Status is ContentStatuses.Archived or ContentStatuses.InReview)
        {
            throw AppException.Conflict(item.Status == ContentStatuses.Archived ? "conflict.content.archived" : "conflict.content.in_review");
        }
        var errors = new ValidationErrors();
        if (request.Type is { } type && type != item.Type || request.SiteId is { } s && s != item.SiteId)
        {
            errors.Add("type", "validation.content.fixed");
        }
        ValidateSettings(request, errors);
        await ValidatePeopleAsync(request, errors, ct);
        var basis = item.DraftVersion ?? item.PublishedVersion;
        var previous = basis is { } b ? (await VersionAsync(id, b, ct))?.Variants : null;
        var variants = await CleanVariantsAsync(request.Variants, item.PrimaryLocale, previous, errors, ct);
        errors.ThrowIfAny();

        var now = clock.UtcNow;
        int draft;
        if (item.DraftVersion is { } current)
        {
            draft = current;
            await session.ExecAsync(
                "update content.content_version set variants = @variants, change_note = @note, updated_at = @now where item_id = @id and number = @draft",
                new { id, draft, variants = new Jsonb(ContentJson.Write(variants)), note = string.IsNullOrWhiteSpace(request.ChangeNote) ? null : request.ChangeNote.Trim(), now }, ct);
        }
        else
        {
            draft = await session.ScalarAsync<int>("select coalesce(max(number), 0) + 1 from content.content_version where item_id = @id", new { id }, ct);
            await InsertVersionAsync(id, draft, variants, request.ChangeNote, user, now, ct);
        }
        await session.ExecAsync(
            """
            update content.content_item set classification = @classification, owner_user_id = @owner, backup_owner_user_id = @backup,
              review_due_at = @reviewDue, audience = @audience, pinned = @pinned, featured = @featured, draft_version = @draft,
              updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new
            {
                id, classification = request.Classification ?? item.Classification, owner = request.OwnerId ?? item.OwnerId,
                backup = request.BackupOwnerId ?? item.BackupOwnerId, reviewDue = request.ReviewDueOn is { } on ? ReviewAt(on, site) : item.ReviewDueAt,
                audience = new Jsonb(ContentJson.Write(request.Audience ?? item.Audience)), pinned = request.Pinned ?? item.Pinned,
                featured = request.Featured ?? item.Featured, draft, now,
            }, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    // ---- Publishing ----

    /// <summary>
    /// Publishes the draft now or at the given local time in the site's time zone. Without a draft, a page taken down
    /// with unpublish goes live again on its last version. An item whose type needs an owner cannot go live without one
    /// (US3 scenario 1).
    /// </summary>
    public async Task<ItemView> PublishAsync(Guid id, PublishRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (item, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (!await HoldsAsync(Permissions.ContentPublish, site, ct))
        {
            throw AppException.Denied();
        }
        if (item.Status is ContentStatuses.Archived or ContentStatuses.InReview)
        {
            throw AppException.Conflict(item.Status == ContentStatuses.Archived ? "conflict.content.archived" : "conflict.content.in_review");
        }
        var version = item.DraftVersion ?? item.PublishedVersion ?? throw AppException.Conflict("conflict.content.nothing_to_publish");
        if (item.DraftVersion is null && item.Status is ContentStatuses.Published or ContentStatuses.Scheduled)
        {
            throw AppException.Conflict("conflict.content.nothing_to_publish");
        }
        var settings = (await sites.TypeAsync(item.Type, ct))!;
        var errors = new ValidationErrors();
        if (settings.RequiresOwner && item.OwnerId is null)
        {
            errors.Add("ownerId", "validation.content.owner_required");
        }
        var zone = TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone);
        var now = clock.UtcNow;
        DateTimeOffset? publishAt = request.PublishAt is { } p ? Utc(p, zone) : null;
        DateTimeOffset? expiresAt = request.ExpiresAt is { } e ? Utc(e, zone) : item.ExpiresAt;
        if (expiresAt is { } ex && ex <= (publishAt is { } pa && pa > now ? pa : now))
        {
            errors.Add("expiresAt", "validation.content.expiry");
        }
        errors.ThrowIfAny();

        var material = request.Material ?? true;
        await session.ExecAsync("update content.content_version set material = @material, approver_user_id = @user where item_id = @id and number = @version",
            new { id, version, material, user }, ct);
        if (publishAt is { } at && at > now)
        {
            await session.ExecAsync(
                """
                update content.content_item set status = case when status = 'published' then 'published' else 'scheduled' end, publish_at = @at, expires_at = @expiresAt, updated_at = @now, row_version = row_version + 1
                where id = @id
                """, new { id, at, expiresAt, now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ContentScheduled, "content_item", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
                Changes: new { version, publishAt = at, expiresAt }), ct);
        }
        else
        {
            await session.ExecAsync("update content.content_item set expires_at = @expiresAt where id = @id", new { id, expiresAt }, ct);
            await GoLiveAsync(item with { ExpiresAt = expiresAt }, site, version, now, ct);
        }
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    private static DateTimeOffset Utc(DateTime local, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    /// <summary>Makes a version the live one. Used by publish now and by the scheduler; the actor is whoever is in context.</summary>
    internal async Task GoLiveAsync(ItemRow item, SiteRow site, int version, DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync(
            "update content.content_version set published_at = @now, updated_at = @now where item_id = @id and number = @version and published_at is null",
            new { id = item.Id, version, now }, ct);
        await session.ExecAsync(
            """
            update content.content_item set status = 'published', published_version = @version, draft_version = null, publish_at = null,
              published_at = @now, expired_at = null, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id = item.Id, version, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentPublished, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { version, site = site.Id, expiresAt = item.ExpiresAt }), ct);
        await outbox.EnqueueAsync(new ContentPublished(item.Id, site.Id, item.Type, version), ct);
    }

    /// <summary>Takes a live or scheduled item down. It keeps its versions and can be published again.</summary>
    public async Task<ItemView> UnpublishAsync(Guid id, CancellationToken ct)
    {
        var (item, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (!await HoldsAsync(Permissions.ContentPublish, site, ct))
        {
            throw AppException.Denied();
        }
        if (item.Status is not (ContentStatuses.Published or ContentStatuses.Scheduled or ContentStatuses.Expired))
        {
            throw AppException.Conflict("conflict.content.not_published");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            "update content.content_item set status = 'draft', publish_at = null, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentUnpublished, "content_item", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { from = item.Status, item.PublishedVersion }), ct);
        if (item.Status != ContentStatuses.Scheduled)
        {
            await outbox.EnqueueAsync(new ContentUnpublished(id, site.Id), ct);
        }
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>Archives an item: read-only, out of feeds, kept with its history.</summary>
    public async Task<ItemView> ArchiveAsync(Guid id, CancellationToken ct)
    {
        var (item, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (!await HoldsAsync(Permissions.ContentPublish, site, ct))
        {
            throw AppException.Denied();
        }
        if (item.Status is ContentStatuses.Archived or ContentStatuses.InReview)
        {
            throw AppException.Conflict(item.Status == ContentStatuses.Archived ? "conflict.content.archived" : "conflict.content.in_review");
        }
        await ArchiveRowAsync(item, site, "manual", ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    internal async Task ArchiveRowAsync(ItemRow item, SiteRow site, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await session.ExecAsync(
            "update content.content_item set status = 'archived', publish_at = null, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id = item.Id, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentArchived, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { from = item.Status, reason }), ct);
        if (ContentStatuses.Readable(item.Status))
        {
            await outbox.EnqueueAsync(new ContentUnpublished(item.Id, site.Id), ct);
        }
    }

    internal async Task ExpireRowAsync(ItemRow item, SiteRow site, DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync(
            "update content.content_item set status = 'expired', expired_at = @now, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id = item.Id, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentExpired, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { item.PublishedVersion, item.ExpiresAt }), ct);
        await outbox.EnqueueAsync(new ContentExpired(item.Id, site.Id), ct);
    }

    // ---- Reading ----

    private readonly Dictionary<Guid, ContentPerson> people = [];

    private async Task<ContentPerson> PersonAsync(Guid id, CancellationToken ct)
    {
        if (!people.TryGetValue(id, out var person))
        {
            people[id] = person = new ContentPerson(id, (await users.GetAsync(id, ct))?.DisplayName ?? string.Empty);
        }
        return person;
    }

    private async Task<ItemView> ViewAsync(ItemRow item, CancellationToken ct)
    {
        var number = item.DraftVersion ?? item.PublishedVersion;
        var version = number is { } n ? await VersionAsync(item.Id, n, ct) : null;
        return new ItemView(item.Id, item.SiteId, item.Type, item.Status, item.Classification, item.PrimaryLocale, item.Audience,
            item.OwnerId is { } o ? await PersonAsync(o, ct) : null, item.BackupOwnerId is { } b ? await PersonAsync(b, ct) : null, item.ReviewDueAt,
            item.Pinned, item.Featured, item.PublishedVersion, item.DraftVersion, item.PublishAt, item.ExpiresAt, item.PublishedAt, item.ExpiredAt,
            version?.Variants ?? new Dictionary<string, ContentVariant>(), version?.ChangeNote, await PersonAsync(item.CreatedBy, ct), item.UpdatedAt, item.Version);
    }

    public async Task<ItemView> GetAsync(Guid id, CancellationToken ct) => await ViewAsync((await ManagedAsync(id, false, ct)).Item, ct);

    /// <summary>The items of a site for the people who work on it, newest change first. Authors see their own drafts and everything live.</summary>
    public async Task<IReadOnlyList<ItemView>> ListAsync(Guid? siteId, string? status, string? type, int limit, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var user = context.RequireUser();
        var site = siteId is { } s ? await sites.RowAsync(s, false, ct) ?? throw AppException.NotFound() : await sites.DefaultAsync(ct);
        var editor = await HoldsAsync(Permissions.ContentEdit, site, ct) || await HoldsAsync(Permissions.ContentPublish, site, ct);
        if (!editor && !await HoldsAsync(Permissions.ContentAuthor, site, ct))
        {
            throw AppException.NotFound();
        }
        if (status is not null && !ContentStatuses.All.Contains(status))
        {
            throw AppException.Validation("status", "validation.content.status");
        }
        var rows = await session.QueryAsync(
            $"""
            select {Columns} from content.content_item
            where site_id = @site and (@status::text is null or status = @status) and (@type::text is null or type = @type)
              and (@editor or created_by = @user or status in ('published', 'scheduled', 'expired'))
            order by updated_at desc, id limit @limit
            """, new { site = site.Id, status, type, editor, user, limit = Math.Clamp(limit, 1, 200) }, Read, ct);
        var list = new List<ItemView>();
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    /// <summary>
    /// The live version for a reader, in their language or the fallback (US5 scenario 2). Drafts, scheduled and archived
    /// items, and items the reader may not view, are not found. Targeting is not access: anyone permitted may open it by
    /// link (section 3).
    /// </summary>
    public async Task<ReaderView> ReadAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        var item = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        if (!ContentStatuses.Readable(item.Status) || item.PublishedVersion is not { } number || !await MayReadAsync(item, site, ct))
        {
            throw AppException.NotFound();
        }
        var version = (await VersionAsync(id, number, ct))!;
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        var (locale, variant) = Pick(version.Variants, requestLocale.Locale, locales.DefaultLocale, item.PrimaryLocale);
        return new ReaderView(id, site.Id, item.Type, locale, locale != requestLocale.Locale, version.Variants.Keys.Order(StringComparer.Ordinal).ToList(),
            variant.Title, variant.Summary, variant.Body, number, item.PublishedAt ?? version.PublishedAt ?? version.CreatedAt,
            version.PublishedAt ?? version.CreatedAt, item.Status == ContentStatuses.Expired, item.Pinned, item.Featured);
    }

    /// <summary>The reader's locale, else the tenant default, else the item's primary locale.</summary>
    internal static (string Locale, ContentVariant Variant) Pick(IReadOnlyDictionary<string, ContentVariant> variants, string wanted, string tenantDefault, string primary)
    {
        foreach (var locale in new[] { wanted, tenantDefault, primary })
        {
            if (variants.TryGetValue(locale, out var v))
            {
                return (locale, v);
            }
        }
        var first = variants.OrderBy(v => v.Key, StringComparer.Ordinal).First();
        return (first.Key, first.Value);
    }

    /// <summary>Version history for the people who work on the item, or for readers when the content type shows it (US4 scenario 3).</summary>
    public async Task<IReadOnlyList<VersionSummary>> VersionsAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        var item = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        var worker = await HoldsAsync(Permissions.ContentAuthor, site, ct) || await HoldsAsync(Permissions.ContentEdit, site, ct)
            || await HoldsAsync(Permissions.ContentPublish, site, ct);
        if (!worker)
        {
            var settings = (await sites.TypeAsync(item.Type, ct))!;
            if (!settings.ShowHistoryToReaders || !ContentStatuses.Readable(item.Status) || !await MayReadAsync(item, site, ct))
            {
                throw AppException.NotFound();
            }
        }
        var versions = await session.QueryAsync($"select {VersionColumns} from content.content_version where item_id = @id order by number desc", new { id }, ReadVersion, ct);
        var list = new List<VersionSummary>();
        foreach (var v in versions.Where(v => worker || v.PublishedAt is not null))
        {
            list.Add(new VersionSummary(v.Number, await PersonAsync(v.Author, ct), v.Approver is { } a ? await PersonAsync(a, ct) : null, v.ChangeNote, v.Material,
                v.Variants.Keys.Order(StringComparer.Ordinal).ToList(), v.CreatedAt, v.PublishedAt, v.Number == item.PublishedVersion, v.Number == item.DraftVersion));
        }
        return list;
    }
}
