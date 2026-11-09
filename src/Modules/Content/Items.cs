using Npgsql;
using TeamBeats.Modules.Workflow.Contracts;
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
    int? Version,
    CriticalInput? Critical = null);

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
    int Version,
    bool InReview,
    CriticalSettings? Critical,
    bool OwnerReviewOpen = false,
    bool RetirePending = false);

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
    bool Featured,
    bool UnderReview = false,
    IReadOnlyList<string>? Anchors = null);

public sealed record VersionSummary(int Number, ContentPerson Author, ContentPerson? Approver, string? ChangeNote, bool Material, IReadOnlyList<string> Locales,
    DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, bool Current, bool Draft, int? RestoredFrom = null);

/// <summary>One version with its full text, for history and compare (US4 scenario 1).</summary>
public sealed record VersionDetail(int Number, IReadOnlyDictionary<string, ContentVariant> Variants, ContentPerson Author, ContentPerson? Approver,
    string? ChangeNote, bool Material, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, int? RestoredFrom);

/// <summary>Two versions side by side in one locale, as runs of kept, added and removed text.</summary>
public sealed record VersionCompare(int From, int To, string Locale, IReadOnlyList<DiffSegment> Title, IReadOnlyList<DiffSegment> Summary,
    IReadOnlyList<DiffSegment> Body);

public sealed record RestoreRequest(string? ChangeNote);

internal sealed record ItemRow(
    Guid Id, Guid SiteId, string Type, string Classification, string PrimaryLocale, Guid? OwnerId, Guid? BackupOwnerId, DateTimeOffset? ReviewDueAt,
    ContentAudience Audience, bool Pinned, bool Featured, string Status, int? PublishedVersion, int? DraftVersion, DateTimeOffset? PublishAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? PublishedAt, DateTimeOffset? ExpiredAt, Guid CreatedBy, DateTimeOffset UpdatedAt, int Version,
    Guid? ReviewInstanceId = null, Guid? SubmittedBy = null, CriticalSettings? Critical = null, Guid? OwnerReviewInstanceId = null,
    Guid? RetireInstanceId = null)
{
    /// <summary>The draft waits for a decision. A live item stays published (and readable) while its next version is reviewed.</summary>
    public bool InReview => ReviewInstanceId is not null || Status == ContentStatuses.InReview;

    public bool Live => Status is ContentStatuses.Published or ContentStatuses.Expired;
}

internal sealed record VersionRow(int Number, IReadOnlyDictionary<string, ContentVariant> Variants, string? ChangeNote, bool Material, Guid Author,
    Guid? Approver, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt, int? RestoredFrom = null);

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
    IProcessEngine engine,
    Sites sites,
    ContentReviews reviews,
    IOrganizationHierarchy hierarchy)
{
    public static readonly IReadOnlySet<string> Classifications = new HashSet<string>(StringComparer.Ordinal) { "public", "internal", "confidential", "restricted" };

    internal const string Columns =
        """
        id, site_id, type, classification, primary_locale, owner_user_id, backup_owner_user_id, review_due_at, audience::text, pinned, featured, status,
        published_version, draft_version, publish_at, expires_at, published_at, expired_at, created_by, updated_at, row_version, review_instance_id, submitted_by, critical::text,
        owner_review_instance_id, retire_instance_id
        """;

    internal static ItemRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Get<Guid?>("owner_user_id"), r.Get<Guid?>("backup_owner_user_id"),
        r.Get<DateTimeOffset?>("review_due_at"), ContentJson.Read<ContentAudience>(r.GetString(8)), r.GetBoolean(9), r.GetBoolean(10), r.GetString(11),
        r.Get<int?>("published_version"), r.Get<int?>("draft_version"), r.Get<DateTimeOffset?>("publish_at"), r.Get<DateTimeOffset?>("expires_at"),
        r.Get<DateTimeOffset?>("published_at"), r.Get<DateTimeOffset?>("expired_at"), r.GetGuid(18), r.GetFieldValue<DateTimeOffset>(19), r.GetInt32(20),
        r.Get<Guid?>("review_instance_id"), r.Get<Guid?>("submitted_by"), r.Get<string?>("critical") is { } c ? ContentJson.Read<CriticalSettings>(c) : null,
        r.Get<Guid?>("owner_review_instance_id"), r.Get<Guid?>("retire_instance_id"));

    private const string VersionColumns = "number, variants::text, change_note, material, author_user_id, approver_user_id, created_at, updated_at, published_at, restored_from";

    private static VersionRow ReadVersion(NpgsqlDataReader r) => new(r.GetInt32(0), ContentJson.Read<Dictionary<string, ContentVariant>>(r.GetString(1)),
        r.Get<string?>("change_note"), r.GetBoolean(3), r.GetGuid(4), r.Get<Guid?>("approver_user_id"), r.GetFieldValue<DateTimeOffset>(6),
        r.GetFieldValue<DateTimeOffset>(7), r.Get<DateTimeOffset?>("published_at"), r.Get<int?>("restored_from"));

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
    internal async Task<bool> MayReadAsync(ItemRow item, SiteRow site, CancellationToken ct)
    {
        var visibility = item.Classification is "public" or "internal" ? ResourceVisibility.Tenant : ResourceVisibility.Restricted;
        var descriptor = new ResourceDescriptor(ResourceType, item.Id, site.OrgUnitId, visibility, item.Classification);
        return (await authz.CheckAsync(Permissions.ContentView, AuthzTarget.Resource(descriptor), ct)).Allowed;
    }

    /// <summary>Knowledge articles are written by knowledge editors (<c>kb.edit</c> at the site, spec 011 US4), on top of the authoring rights.</summary>
    private async Task DemandKnowledgeEditorAsync(string type, SiteRow site, CancellationToken ct)
    {
        if (type == ContentTypes.Knowledge && !await HoldsAsync(Permissions.KbEdit, site, ct))
        {
            throw AppException.Denied();
        }
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

    /// <summary>
    /// Critical settings from the editor (FR-006): only on announcements, a modal or a banner, "remind me later" up to
    /// <see cref="CriticalSettings.MaxRemindLater"/> times (3 by default, section 6), and a deadline in the future given
    /// as a local time in the site's zone. "none" clears them; leaving them out keeps what the item has.
    /// </summary>
    private async Task<CriticalSettings?> CriticalAsync(CriticalInput? input, CriticalSettings? current, string type, SiteRow site, ValidationErrors errors,
        CancellationToken ct)
    {
        if (input is null)
        {
            return current;
        }
        if (input.Presentation == "none")
        {
            return null;
        }
        if (input.Presentation is not (CriticalSettings.Modal or CriticalSettings.Banner))
        {
            errors.Add("critical.presentation", "validation.content.critical_presentation");
            return null;
        }
        if (type != ContentTypes.Announcement)
        {
            errors.Add("critical", "validation.content.critical_type");
        }
        var remind = input.RemindLaterMax ?? CriticalSettings.DefaultRemindLater;
        if (remind is < 0 or > CriticalSettings.MaxRemindLater)
        {
            errors.Add("critical.remindLaterMax", "validation.content.critical_remind");
        }
        DateTimeOffset? deadline = input.Deadline is { } d ? Utc(d, TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone)) : null;
        if (deadline is { } at && at <= clock.UtcNow)
        {
            errors.Add("critical.deadline", "validation.content.critical_deadline");
        }
        await entitlements.DemandAsync(Capabilities.ContentCriticalAck, ct);
        var modal = input.Presentation == CriticalSettings.Modal;
        return new CriticalSettings(input.Presentation, modal || (input.RequireAck ?? false), modal ? remind : 0, deadline, !modal && (input.Dismissible ?? false));
    }

    /// <summary>
    /// Sending a critical announcement needs <c>content.publish_critical</c> at the site, and at tenant scope when the
    /// audience reaches past the site's own units (section 6). A refusal is audited (US2 scenario 5) and kept even
    /// though the request fails.
    /// </summary>
    private async Task DemandCriticalAsync(ItemRow item, SiteRow site, CancellationToken ct)
    {
        if (item.Critical is null)
        {
            return;
        }
        var allowed = await HoldsAsync(Permissions.ContentPublishCritical, site, ct)
            && (await WithinSiteAsync(item.Audience, site, ct) || (await authz.CheckAsync(Permissions.ContentPublishCritical, AuthzTarget.Tenant, ct)).Allowed);
        if (allowed)
        {
            return;
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentCriticalDenied, "content_item", item.Id.ToString(), AuditOutcome.Denied, site.OrgUnitId,
            Changes: new { item.Critical.Presentation }), ct);
        await session.CommitAsync(ct);
        throw AppException.Denied();
    }

    private async Task<bool> WithinSiteAsync(ContentAudience audience, SiteRow site, CancellationToken ct)
    {
        if (site.OrgUnitId is not { } unit)
        {
            return true;
        }
        if (audience.IsEveryone)
        {
            return false;
        }
        foreach (var clause in audience.AnyOf!)
        {
            if (clause.OrgUnits is not { Count: > 0 } units)
            {
                return false;
            }
            foreach (var target in units)
            {
                if (!await hierarchy.IsAncestorOrSelfAsync(unit, target.Id, ct))
                {
                    return false;
                }
            }
        }
        return true;
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
        await DemandKnowledgeEditorAsync(type, site, ct);
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
        var critical = await CriticalAsync(request.Critical, null, type, site, errors, ct);
        var variants = await CleanVariantsAsync(request.Variants, primary, null, errors, ct);
        errors.ThrowIfAny();

        var id = Ids.New();
        var now = clock.UtcNow;
        var reviewDue = request.ReviewDueOn is { } on ? ReviewAt(on, site) : settings!.ReviewMonths is { } months ? now.AddMonths(months) : (DateTimeOffset?)null;
        await session.ExecAsync(
            """
            insert into content.content_item (tenant_id, id, site_id, type, classification, primary_locale, owner_user_id, backup_owner_user_id, review_due_at,
              audience, pinned, featured, critical, status, draft_version, created_at, created_by, updated_at)
            values (platform.current_tenant_id(), @id, @site, @type, @classification, @primary, @owner, @backup, @reviewDue,
              @audience, @pinned, @featured, @critical, 'draft', 1, @now, @user, @now)
            """, new
            {
                id, site = site.Id, type, classification = request.Classification ?? "internal", primary, owner = request.OwnerId, backup = request.BackupOwnerId,
                reviewDue, audience = new Jsonb(ContentJson.Write(request.Audience ?? site.DefaultAudience)), pinned = request.Pinned ?? false,
                featured = request.Featured ?? false, critical = critical is null ? null : new Jsonb(ContentJson.Write(critical)), now, user,
            }, ct);
        await InsertVersionAsync(id, 1, variants, request.ChangeNote, user, now, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentCreated, "content_item", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { site = site.Id, type, version = 1 }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    internal static DateTimeOffset ReviewAt(DateOnly on, SiteRow site) =>
        new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(on.ToDateTime(new TimeOnly(9, 0)), TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone)), TimeSpan.Zero);

    internal Task InsertVersionAsync(Guid item, int number, IReadOnlyDictionary<string, ContentVariant> variants, string? note, Guid user, DateTimeOffset now,
        CancellationToken ct, int? restoredFrom = null) =>
        session.ExecAsync(
            """
            insert into content.content_version (tenant_id, item_id, number, variants, change_note, author_user_id, created_at, updated_at, restored_from)
            values (platform.current_tenant_id(), @item, @number, @variants, @note, @user, @now, @now, @restoredFrom)
            """, new { item, number, variants = new Jsonb(ContentJson.Write(variants)), note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), user, now, restoredFrom }, ct);

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
        await DemandKnowledgeEditorAsync(item.Type, site, ct);
        if (request.Version is { } expected && expected != item.Version)
        {
            throw AppException.Conflict("conflict.content.changed");
        }
        if (item.Status == ContentStatuses.Archived || item.InReview)
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
        var critical = await CriticalAsync(request.Critical, item.Critical, item.Type, site, errors, ct);
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
              review_due_at = @reviewDue, audience = @audience, pinned = @pinned, featured = @featured, critical = @critical, draft_version = @draft,
              deadline_reminded_at = case when critical is distinct from @critical then null else deadline_reminded_at end, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new
            {
                id, classification = request.Classification ?? item.Classification, owner = request.OwnerId ?? item.OwnerId,
                backup = request.BackupOwnerId ?? item.BackupOwnerId, reviewDue = request.ReviewDueOn is { } on ? ReviewAt(on, site) : item.ReviewDueAt,
                audience = new Jsonb(ContentJson.Write(request.Audience ?? item.Audience)), pinned = request.Pinned ?? item.Pinned,
                featured = request.Featured ?? item.Featured, critical = critical is null ? null : new Jsonb(ContentJson.Write(critical)), draft, now,
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
        var (version, publishAt, expiresAt) = await ReadyAsync(item, site, request.PublishAt, request.ExpiresAt, ct);
        if (item.DraftVersion is not null && site.RouteOf(item.Type).Approval)
        {
            // The site routes this type through approval: the draft goes live only through submit (FR-003).
            throw AppException.Conflict("conflict.content.approval_required");
        }
        await DemandCriticalAsync(item, site, ct);
        await session.ExecAsync("update content.content_version set material = @material, approver_user_id = @user where item_id = @id and number = @version",
            new { id, version, material = request.Material ?? true, user }, ct);
        await ReleaseAsync(item, site, version, publishAt, expiresAt, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>
    /// The checks every way to live shares: not archived or in review, something to publish, an owner when the type
    /// needs one, and an expiry after the publish time. Times are local in the site's zone.
    /// </summary>
    private async Task<(int Version, DateTimeOffset? PublishAt, DateTimeOffset? ExpiresAt)> ReadyAsync(ItemRow item, SiteRow site, DateTime? publishLocal,
        DateTime? expiresLocal, CancellationToken ct)
    {
        if (item.Status == ContentStatuses.Archived || item.InReview)
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
        DateTimeOffset? publishAt = publishLocal is { } p ? Utc(p, zone) : null;
        DateTimeOffset? expiresAt = expiresLocal is { } e ? Utc(e, zone) : item.ExpiresAt;
        if (expiresAt is { } ex && ex <= (publishAt is { } pa && pa > now ? pa : now))
        {
            errors.Add("expiresAt", "validation.content.expiry");
        }
        errors.ThrowIfAny();
        return (version, publishAt, expiresAt);
    }

    /// <summary>Schedules the version when its time is ahead, otherwise puts it live now. A live item stays readable while the next version waits.</summary>
    internal async Task ReleaseAsync(ItemRow item, SiteRow site, int version, DateTimeOffset? publishAt, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (publishAt is { } at && at > now)
        {
            await session.ExecAsync(
                """
                update content.content_item set status = case when status in ('published', 'expired') then status else 'scheduled' end, publish_at = @at,
                  expires_at = @expiresAt, updated_at = @now, row_version = row_version + 1
                where id = @id
                """, new { id = item.Id, at, expiresAt, now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ContentScheduled, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
                Changes: new { version, publishAt = at, expiresAt }), ct);
        }
        else
        {
            await session.ExecAsync("update content.content_item set expires_at = @expiresAt where id = @id", new { id = item.Id, expiresAt }, ct);
            await GoLiveAsync(item with { ExpiresAt = expiresAt }, site, version, now, ct);
        }
    }

    // ---- Review (FR-003) ----

    /// <summary>
    /// Sends the draft through the site's route for its type: one step on the process engine whose task shows in the
    /// spec 016 inbox (US1 scenario 1). The wanted schedule is kept and applied on approval. The person who submits and
    /// the people who wrote the item never decide (scenario 6); when every named approver is one of them, the site's
    /// publishers decide instead.
    /// </summary>
    public async Task<ItemView> SubmitAsync(Guid id, PublishRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (item, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (!await MayWriteAsync(site, item, user, ct))
        {
            throw AppException.Denied();
        }
        var route = site.RouteOf(item.Type);
        if (!route.Approval)
        {
            throw AppException.Conflict("conflict.content.no_route");
        }
        if (item.DraftVersion is null || item.PublishAt is not null)
        {
            throw AppException.Conflict(item.Status == ContentStatuses.Archived ? "conflict.content.archived"
                : item.InReview ? "conflict.content.in_review" : "conflict.content.nothing_to_publish");
        }
        var (version, publishAt, expiresAt) = await ReadyAsync(item, site, request.PublishAt, request.ExpiresAt, ct);
        await DemandCriticalAsync(item, site, ct);
        var author = (await VersionAsync(id, version, ct))!.Author;
        var writers = new[] { user, author, item.CreatedBy }.Distinct().ToArray();
        var key = await reviews.RouteKeyAsync(site, item.Type, route, writers, ct);
        var instance = await engine.StartAsync(key, $"{id:N}{Ids.New():N}", site.OrgUnitId, user, writers.Where(w => w != user).ToArray(), user, ct);
        var now = clock.UtcNow;
        await session.ExecAsync("update content.content_version set material = @material where item_id = @id and number = @version",
            new { id, version, material = request.Material ?? true }, ct);
        await session.ExecAsync(
            """
            update content.content_item set status = case when status in ('published', 'expired') then status else 'in_review' end,
              review_instance_id = @instance, submitted_by = @user, publish_at = @publishAt, expires_at = @expiresAt, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id, instance = instance.Id, user, publishAt, expiresAt, now }, ct);
        await reviews.EventAsync(id, version, "submitted", user, null, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentSubmitted, "content_item", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { version, publishAt, expiresAt, route = key }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>The review history of an item for the people who work on it, return comments included (US1 scenario 2).</summary>
    public async Task<IReadOnlyList<ReviewEvent>> ReviewHistoryAsync(Guid id, CancellationToken ct)
    {
        await ManagedAsync(id, false, ct);
        var list = new List<ReviewEvent>();
        foreach (var e in await reviews.HistoryAsync(id, ct))
        {
            list.Add(new ReviewEvent(e.Version, e.Event, e.Actor is { } a ? await PersonAsync(a, ct) : null,
                e.OnBehalfOf is { } o ? await PersonAsync(o, ct) : null, e.Comment, e.At));
        }
        return list;
    }

    /// <summary>The person who submitted, or an editor of the site, takes the draft back out of review.</summary>
    public async Task<ItemView> WithdrawAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (item, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (item.ReviewInstanceId is not { } instance)
        {
            throw AppException.Conflict("conflict.content.not_in_review");
        }
        if (item.SubmittedBy != user && !await HoldsAsync(Permissions.ContentEdit, site, ct))
        {
            throw AppException.Denied();
        }
        await engine.WithdrawAsync(instance, ct);
        await reviews.CloseAsync(item, ct);
        await reviews.EventAsync(id, item.DraftVersion ?? 0, "withdrawn", user, null, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentWithdrawn, "content_item", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { version = item.DraftVersion }), ct);
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
        if (item.Status == ContentStatuses.Archived || item.InReview)
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
            version?.Variants ?? new Dictionary<string, ContentVariant>(), version?.ChangeNote, await PersonAsync(item.CreatedBy, ct), item.UpdatedAt, item.Version, item.InReview, item.Critical,
            item.OwnerReviewInstanceId is not null, item.RetireInstanceId is not null);
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
    /// The live version for a reader, in their language or the fallback (US5 scenario 2), with the anchor of each heading
    /// in order, so citation links (spec 011 US4) land on the section. Drafts, scheduled and archived
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
            version.PublishedAt ?? version.CreatedAt, item.Status == ContentStatuses.Expired, item.Pinned, item.Featured,
            (await sites.TypeAsync(item.Type, ct))!.UnderReviewLabel && item.ReviewDueAt is { } due && due <= clock.UtcNow,
            BodyValidator.Sections(variant.Body).Select(s => s.Anchor).ToList());
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
                v.Variants.Keys.Order(StringComparer.Ordinal).ToList(), v.CreatedAt, v.PublishedAt, v.Number == item.PublishedVersion, v.Number == item.DraftVersion, v.RestoredFrom));
        }
        return list;
    }

    /// <summary>
    /// Who may see history: the people who work on content in the site see every version; a reader sees published
    /// versions only when the type shows history to readers (US4 scenario 3).
    /// </summary>
    private async Task<(ItemRow Item, bool Worker)> HistoryAccessAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        var item = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        var worker = await HoldsAsync(Permissions.ContentAuthor, site, ct) || await HoldsAsync(Permissions.ContentEdit, site, ct)
            || await HoldsAsync(Permissions.ContentPublish, site, ct);
        if (!worker && (!(await sites.TypeAsync(item.Type, ct))!.ShowHistoryToReaders || !ContentStatuses.Readable(item.Status) || !await MayReadAsync(item, site, ct)))
        {
            throw AppException.NotFound();
        }
        return (item, worker);
    }

    private async Task<VersionRow> VisibleVersionAsync(ItemRow item, bool worker, int number, CancellationToken ct)
    {
        var version = await VersionAsync(item.Id, number, ct);
        return version is null || (!worker && version.PublishedAt is null) ? throw AppException.NotFound() : version;
    }

    public async Task<VersionDetail> VersionDetailAsync(Guid id, int number, CancellationToken ct)
    {
        var (item, worker) = await HistoryAccessAsync(id, ct);
        var v = await VisibleVersionAsync(item, worker, number, ct);
        return new VersionDetail(v.Number, v.Variants, await PersonAsync(v.Author, ct), v.Approver is { } a ? await PersonAsync(a, ct) : null, v.ChangeNote,
            v.Material, v.CreatedAt, v.PublishedAt, v.RestoredFrom);
    }

    /// <summary>Compares two versions in one locale (the item's primary locale when not given): title, summary and the body's plain text.</summary>
    public async Task<VersionCompare> CompareAsync(Guid id, int from, int to, string? locale, CancellationToken ct)
    {
        var (item, worker) = await HistoryAccessAsync(id, ct);
        var a = await VisibleVersionAsync(item, worker, from, ct);
        var b = await VisibleVersionAsync(item, worker, to, ct);
        var wanted = locale ?? item.PrimaryLocale;
        if (!a.Variants.ContainsKey(wanted) && !b.Variants.ContainsKey(wanted))
        {
            throw AppException.Validation("locale", "validation.content.variant_locale");
        }
        var left = a.Variants.GetValueOrDefault(wanted);
        var right = b.Variants.GetValueOrDefault(wanted);
        return new VersionCompare(from, to, wanted, TextDiff.Words(left?.Title, right?.Title), TextDiff.Words(left?.Summary, right?.Summary),
            TextDiff.Words(left is null ? null : BodyValidator.PlainText(left.Body), right is null ? null : BodyValidator.PlainText(right.Body)));
    }

    /// <summary>
    /// Restores an earlier version as a new draft that follows the normal route (US4 scenario 2). History is never
    /// rewritten: the new draft gets the next number and remembers where it came from.
    /// </summary>
    public async Task<ItemView> RestoreAsync(Guid id, int number, RestoreRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (item, site) = await ManagedAsync(id, forUpdate: true, ct);
        if (!await MayWriteAsync(site, item, user, ct))
        {
            throw AppException.Denied();
        }
        if (item.Status == ContentStatuses.Archived || item.InReview)
        {
            throw AppException.Conflict(item.Status == ContentStatuses.Archived ? "conflict.content.archived" : "conflict.content.in_review");
        }
        if (item.DraftVersion is not null)
        {
            throw AppException.Conflict("conflict.content.draft_exists");
        }
        var source = await VersionAsync(id, number, ct) ?? throw AppException.NotFound();
        if (request.ChangeNote is { Length: > ContentLimits.ChangeNoteMax })
        {
            throw AppException.Validation("changeNote", "validation.content.change_note");
        }
        var current = item.PublishedVersion is { } p ? (await VersionAsync(id, p, ct))?.Variants : null;
        var variants = MarkOutOfDate(source.Variants.ToDictionary(v => v.Key, v => v.Value with { OutOfDate = false }, StringComparer.Ordinal),
            item.PrimaryLocale, current);
        var now = clock.UtcNow;
        var draft = await session.ScalarAsync<int>("select coalesce(max(number), 0) + 1 from content.content_version where item_id = @id", new { id }, ct);
        await InsertVersionAsync(id, draft, variants, request.ChangeNote, user, now, ct, number);
        await session.ExecAsync("update content.content_item set draft_version = @draft, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, draft, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentVersionRestored, "content_item", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { from = number, draft }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>A new draft copied from the live version, for the owner who chose "edit" on a review task (US3 scenario 2).</summary>
    internal async Task<int?> DraftFromLiveAsync(ItemRow item, Guid user, CancellationToken ct)
    {
        if (item.DraftVersion is { } existing)
        {
            return existing;
        }
        if (item.PublishedVersion is not { } live || await VersionAsync(item.Id, live, ct) is not { } version)
        {
            return null;
        }
        var now = clock.UtcNow;
        var draft = await session.ScalarAsync<int>("select coalesce(max(number), 0) + 1 from content.content_version where item_id = @id", new { id = item.Id }, ct);
        await InsertVersionAsync(item.Id, draft, version.Variants, null, user, now, ct, live);
        await session.ExecAsync("update content.content_item set draft_version = @draft, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id = item.Id, draft, now }, ct);
        return draft;
    }
}
