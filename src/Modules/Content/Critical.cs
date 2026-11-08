using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

/// <summary>One critical announcement as the shell shows it, in the reader's language or the fallback.</summary>
public sealed record CriticalNotice(
    Guid Id,
    int Version,
    string Presentation,
    bool RequireAck,
    bool Dismissible,
    int RemindLaterLeft,
    DateTimeOffset? DeadlineAt,
    string Locale,
    string Title,
    string? Summary,
    BodyDocument Body,
    DateTimeOffset PublishedAt);

/// <summary>What the shell interrupts with on this page load: at most one modal, and the banners above the content.</summary>
public sealed record PendingCritical(CriticalNotice? Modal, IReadOnlyList<CriticalNotice> Banners);

public sealed record AcknowledgeRequest(string? Locale, string? Channel);

public sealed record AcknowledgementView(Guid ItemId, int Version, DateTimeOffset At, string Channel);

public sealed record RemindLaterView(int RemindLaterLeft, DateTimeOffset RemindUntil);

/// <summary>Completion of a critical announcement: who it targets now and who confirmed the version that counts.</summary>
public sealed record AcknowledgementSummary(Guid ItemId, int CountedFrom, int Audience, int Confirmed, int Pending, IReadOnlyDictionary<string, int> ByChannel,
    DateTimeOffset? DeadlineAt);

public sealed record TeamMemberStatus(ContentPerson Person, bool Confirmed, DateTimeOffset? At, string? Channel);

public sealed record AckReportView(Guid Id, Guid ItemId, string Status, int? RowCount, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt);

/// <summary>A person of the audience with their state for the counted version.</summary>
internal sealed record AudienceStatus(Guid UserId, DateTimeOffset? At, string? Channel, int? Version);

/// <summary>
/// Critical announcements of spec 009 US2 (FR-006, FR-007): the page-load check, confirming, "remind me later",
/// dismissing a banner, completion counts, the manager's team list and the background completion report. Who an item
/// targets lives in <c>content.audience_member</c>, kept current by <see cref="CriticalAudience"/>. Confirmations are
/// insert only and count from the latest material version on (section 6).
/// </summary>
public sealed class CriticalAnnouncements(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    ITenantSettingsReader tenantSettings,
    ITenantMemberships memberships,
    IReportingLine reportingLine,
    IUserDirectory users,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    RequestLocale requestLocale,
    Items items,
    Sites sites)
{
    public const int MaxPending = 20;
    public static readonly IReadOnlySet<string> Channels = new HashSet<string>(StringComparer.Ordinal) { "web", "mobile" };

    /// <summary>The first version whose confirmations still count: the latest published version marked material.</summary>
    internal const string CountedFrom =
        """
        (select coalesce(max(v.number), 0) from content.content_version v
         where v.tenant_id = i.tenant_id and v.item_id = i.id and v.published_at is not null and v.material)
        """;

    private readonly Dictionary<Guid, SiteRow> siteCache = [];

    private async Task<SiteRow> SiteAsync(Guid id, CancellationToken ct)
    {
        if (!siteCache.TryGetValue(id, out var site))
        {
            siteCache[id] = site = (await sites.RowAsync(id, false, ct))!;
        }
        return site;
    }

    /// <summary>
    /// The page-load check (US2 scenarios 1 and 6). One indexed query on the person's audience rows; items they may
    /// not view are left out, as are items confirmed from the counted version on, dismissed banners and modals put off
    /// with "remind me later". Without the capability nothing interrupts.
    /// </summary>
    public async Task<PendingCritical> PendingAsync(CancellationToken ct)
    {
        var user = context.RequireUser();
        if (!await entitlements.IsEnabledAsync(Capabilities.ContentCore, ct) || !await entitlements.IsEnabledAsync(Capabilities.ContentCriticalAck, ct))
        {
            return new PendingCritical(null, []);
        }
        var now = clock.UtcNow;
        var rows = await session.QueryAsync(
            $"""
            select {Prefixed}, m.remind_count from content.audience_member m
            join content.content_item i on i.tenant_id = m.tenant_id and i.id = m.item_id
            where m.user_id = @user and i.status = 'published' and i.critical is not null and m.dismissed_at is null
              and (m.remind_until is null or m.remind_until <= @now)
              and not exists (select 1 from content.acknowledgement a where a.tenant_id = i.tenant_id and a.item_id = i.id and a.user_id = @user
                and a.version_number >= {CountedFrom})
            order by i.published_at, i.id limit @limit
            """, new { user, now, limit = MaxPending }, r => (Item: Items.Read(r), Reminded: r.GetInt32(24)), ct);
        CriticalNotice? modal = null;
        var banners = new List<CriticalNotice>();
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        foreach (var (item, reminded) in rows)
        {
            var settings = item.Critical!;
            if (settings.Presentation == CriticalSettings.Modal && modal is not null || !await items.MayReadAsync(item, await SiteAsync(item.SiteId, ct), ct))
            {
                continue;
            }
            var version = (await items.VersionAsync(item.Id, item.PublishedVersion!.Value, ct))!;
            var (locale, variant) = Items.Pick(version.Variants, requestLocale.Locale, locales.DefaultLocale, item.PrimaryLocale);
            var notice = new CriticalNotice(item.Id, version.Number, settings.Presentation, settings.Confirms, settings.Dismissible,
                Math.Max(0, settings.RemindLaterMax - reminded), settings.DeadlineAt, locale, variant.Title, variant.Summary, variant.Body,
                item.PublishedAt ?? now);
            if (settings.Presentation == CriticalSettings.Modal)
            {
                modal = notice;
            }
            else
            {
                banners.Add(notice);
            }
        }
        return new PendingCritical(modal, banners);
    }

    /// <summary>Item columns with the <c>i.</c> prefix, in the order <see cref="Items.Read"/> expects.</summary>
    private static readonly string Prefixed = string.Join(", ", Items.Columns.Split(',', StringSplitOptions.TrimEntries)
        .Select(c => "i." + c));

    /// <summary>A live critical item the person may read, or not found.</summary>
    private async Task<(ItemRow Item, SiteRow Site)> LiveAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        await entitlements.DemandAsync(Capabilities.ContentCriticalAck, ct);
        context.RequireUser();
        var item = await items.RowAsync(id, false, ct);
        if (item is not { Status: ContentStatuses.Published, Critical: not null, PublishedVersion: not null })
        {
            throw AppException.NotFound();
        }
        var site = await SiteAsync(item.SiteId, ct);
        if (!await items.MayReadAsync(item, site, ct))
        {
            throw AppException.NotFound();
        }
        return (item, site);
    }

    /// <summary>
    /// "I have read and understood" (US2 scenario 2, FR-007): records the person, the live version, the time, the
    /// channel and a hash of the exact text shown in the locale they read. A second confirmation of the same version
    /// returns the first one unchanged.
    /// </summary>
    public async Task<AcknowledgementView> AcknowledgeAsync(Guid id, AcknowledgeRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (item, site) = await LiveAsync(id, ct);
        if (!item.Critical!.Confirms)
        {
            throw AppException.Conflict("conflict.content.no_acknowledgement");
        }
        var channel = request.Channel ?? "web";
        if (!Channels.Contains(channel))
        {
            throw AppException.Validation("channel", "validation.content.channel");
        }
        var number = item.PublishedVersion!.Value;
        var version = (await items.VersionAsync(id, number, ct))!;
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        var (locale, variant) = Items.Pick(version.Variants, request.Locale ?? requestLocale.Locale, locales.DefaultLocale, item.PrimaryLocale);
        var hash = TextHash(variant);
        var now = clock.UtcNow;
        var inserted = await session.ExecAsync(
            """
            insert into content.acknowledgement (tenant_id, id, item_id, version_number, user_id, channel, text_hash, at)
            values (platform.current_tenant_id(), @ackId, @id, @number, @user, @channel, @hash, @now)
            on conflict (tenant_id, item_id, version_number, user_id) do nothing
            """, new { ackId = Ids.New(), id, number, user, channel, hash, now }, ct);
        if (inserted > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ContentAcknowledged, "content_item", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
                Changes: new { version = number, user, channel, locale }), ct);
            await outbox.EnqueueAsync(new ContentAcknowledged(id, number, user, channel), ct);
            return new AcknowledgementView(id, number, now, channel);
        }
        return (await session.QuerySingleOrDefaultAsync(
            "select version_number, at, channel from content.acknowledgement where item_id = @id and version_number = @number and user_id = @user",
            new { id, number, user }, r => new AcknowledgementView(id, r.GetInt32(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2)), ct))!;
    }

    /// <summary>SHA-256 of the title, summary and plain body text, so an auditor can match a confirmation to the words shown.</summary>
    internal static string TextHash(ContentVariant variant)
    {
        var text = string.Join('\n', variant.Title, variant.Summary ?? string.Empty, BodyValidator.PlainText(variant.Body));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Puts a modal off for a few hours, at most the number of times the publisher allowed for this version (US2 scenario 1).</summary>
    public async Task<RemindLaterView> RemindLaterAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (item, _) = await LiveAsync(id, ct);
        var settings = item.Critical!;
        if (settings.Presentation != CriticalSettings.Modal)
        {
            throw AppException.Conflict("conflict.content.remind_limit");
        }
        var until = clock.UtcNow + CriticalSettings.RemindLaterFor;
        var count = await session.QuerySingleOrDefaultAsync(
            """
            update content.audience_member set remind_count = remind_count + 1, remind_until = @until
            where item_id = @id and user_id = @user and remind_count < @max
            returning remind_count
            """, new { id, user, until, max = settings.RemindLaterMax }, r => (int?)r.GetInt32(0), ct);
        if (count is not { } done)
        {
            await MemberAsync(id, user, ct);
            throw AppException.Conflict("conflict.content.remind_limit");
        }
        return new RemindLaterView(settings.RemindLaterMax - done, until);
    }

    /// <summary>Hides a dismissible banner for this person (US2 scenario 6).</summary>
    public async Task DismissAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (item, _) = await LiveAsync(id, ct);
        if (item.Critical is not { Presentation: CriticalSettings.Banner, Dismissible: true })
        {
            throw AppException.Conflict("conflict.content.not_dismissible");
        }
        await MemberAsync(id, user, ct);
        await session.ExecAsync("update content.audience_member set dismissed_at = @now where item_id = @id and user_id = @user and dismissed_at is null",
            new { id, user, now = clock.UtcNow }, ct);
    }

    private async Task MemberAsync(Guid id, Guid user, CancellationToken ct)
    {
        if (await session.ScalarAsync<int>("select count(*)::int from content.audience_member where item_id = @id and user_id = @user", new { id, user }, ct) == 0)
        {
            throw AppException.NotFound(); // Not targeted: there is nothing to put off.
        }
    }

    // ---- Completion ----

    /// <summary>Completion and the report are personal data: <c>content.publish_critical</c> at the site or <c>content.admin</c> (section 9).</summary>
    private async Task<(ItemRow Item, SiteRow Site)> ReportableAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        await entitlements.DemandAsync(Capabilities.ContentCriticalAck, ct);
        context.RequireUser();
        var item = await items.RowAsync(id, false, ct) ?? throw AppException.NotFound();
        var site = await SiteAsync(item.SiteId, ct);
        if (!(await authz.CheckAsync(Permissions.ContentPublishCritical, site.Target, ct)).Allowed
            && !(await authz.CheckAsync(Permissions.ContentAdmin, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        if (item.Critical is not { Confirms: true })
        {
            throw AppException.Conflict("conflict.content.no_acknowledgement");
        }
        return (item, site);
    }

    /// <summary>Every active person the item targets now, with their confirmation of the counted version if any (edge case: the audience follows membership).</summary>
    internal async Task<(int CountedFrom, List<AudienceStatus> People)> StatusAsync(Guid id, CancellationToken ct)
    {
        var counted = await session.ScalarAsync<int>($"select {CountedFrom} from content.content_item i where i.id = @id", new { id }, ct);
        var rows = await session.QueryAsync(
            """
            select m.user_id, a.at, a.channel, a.version_number from content.audience_member m
            left join lateral (select at, channel, version_number from content.acknowledgement a
              where a.tenant_id = m.tenant_id and a.item_id = m.item_id and a.user_id = m.user_id and a.version_number >= @counted
              order by a.version_number desc limit 1) a on true
            where m.item_id = @id
            order by m.user_id
            """, new { id, counted }, r => new AudienceStatus(r.GetGuid(0), r.Get<DateTimeOffset?>("at"), r.Get<string?>("channel"), r.Get<int?>("version_number")), ct);
        var active = (await memberships.ListAsync(ct)).Where(m => m.Status == MembershipStatus.Active).Select(m => m.UserId).ToHashSet();
        return (counted, rows.Where(r => active.Contains(r.UserId)).ToList());
    }

    public async Task<AcknowledgementSummary> SummaryAsync(Guid id, CancellationToken ct)
    {
        var (item, _) = await ReportableAsync(id, ct);
        var (counted, people) = await StatusAsync(id, ct);
        var confirmed = people.Where(p => p.At is not null).ToList();
        var byChannel = Channels.Order(StringComparer.Ordinal).ToDictionary(c => c, c => confirmed.Count(p => p.Channel == c));
        return new AcknowledgementSummary(id, counted, people.Count, confirmed.Count, people.Count - confirmed.Count, byChannel, item.Critical!.DeadlineAt);
    }

    /// <summary>A line manager's direct reports in the audience and whether each confirmed (US2: managers see who in their team has not).</summary>
    public async Task<IReadOnlyList<TeamMemberStatus>> TeamAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        await entitlements.DemandAsync(Capabilities.ContentCriticalAck, ct);
        var user = context.RequireUser();
        if (await items.RowAsync(id, false, ct) is not { Critical.Confirms: true })
        {
            throw AppException.NotFound();
        }
        var reports = (await reportingLine.DirectReportsAsync(user, ct)).ToHashSet();
        var (_, people) = await StatusAsync(id, ct);
        var list = new List<TeamMemberStatus>();
        foreach (var p in people.Where(p => reports.Contains(p.UserId)))
        {
            list.Add(new TeamMemberStatus(new ContentPerson(p.UserId, (await users.GetAsync(p.UserId, ct))?.DisplayName ?? string.Empty), p.At is not null, p.At, p.Channel));
        }
        return list.OrderBy(p => p.Confirmed).ThenBy(p => p.Person.DisplayName, StringComparer.Ordinal).ToList();
    }

    /// <summary>Asks for the completion report (US2 scenario 7): audited now, written by the worker, kept for seven days.</summary>
    public async Task<AckReportView> RequestReportAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (_, site) = await ReportableAsync(id, ct);
        var report = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into content.ack_report (tenant_id, id, item_id, status, requested_by, created_at)
            values (platform.current_tenant_id(), @report, @id, 'pending', @user, @now)
            """, new { report, id, user, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentAckReportExported, "content_item", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { report }), ct);
        await outbox.EnqueueAsync(new AckReportRequested(report, id), ct);
        return new AckReportView(report, id, "pending", null, now, null, null);
    }

    private const string ReportColumns = "id, item_id, status, row_count, created_at, completed_at, expires_at";

    private static AckReportView ReadReport(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Get<int?>("row_count"),
        r.GetFieldValue<DateTimeOffset>(4), r.Get<DateTimeOffset?>("completed_at"), r.Get<DateTimeOffset?>("expires_at"));

    public async Task<AckReportView> ReportAsync(Guid report, CancellationToken ct)
    {
        var view = await session.QuerySingleOrDefaultAsync($"select {ReportColumns} from content.ack_report where id = @report", new { report }, ReadReport, ct)
            ?? throw AppException.NotFound();
        await ReportableAsync(view.ItemId, ct);
        return view;
    }

    public async Task<Stream> ReportFileAsync(Guid report, TenantObjectStorage storage, CancellationToken ct)
    {
        var view = await ReportAsync(report, ct);
        if (view.Status != "completed" || view.ExpiresAt < clock.UtcNow)
        {
            throw AppException.NotFound();
        }
        return await storage.OpenReadAsync(AckReportHandler.RelativeKey(report), ct) ?? throw AppException.NotFound();
    }

    // ---- Deadline (US2 scenario 4) ----

    public const string DeadlinePassed = "content.critical.deadline_passed";
    public const string TeamPending = "content.critical.team_pending";
    public const string Sent = "content.critical.sent";

    public static string ReadLink(Guid item) => $"/content/read/{item}";

    /// <summary>
    /// Once the deadline passes: a reminder to each person who has not confirmed, and to each of their line managers
    /// one notice with how many of their reports have not (spec 010 reporting line). Runs once per deadline.
    /// </summary>
    internal async Task<int> RemindAfterDeadlineAsync(ItemRow item, DateTimeOffset now, INotificationSender notifications, CancellationToken ct)
    {
        var (_, people) = await StatusAsync(item.Id, ct);
        var pending = people.Where(p => p.At is null).Select(p => p.UserId).ToArray();
        foreach (var chunk in pending.Chunk(500))
        {
            await notifications.SendAsync(new NotificationRequest(chunk, NotificationCategories.CriticalAnnouncement, DeadlinePassed, ReadLink(item.Id),
                Urgent: true, DedupeKey: $"content:{item.Id:N}:deadline"), ct);
        }
        var byManager = new Dictionary<Guid, int>();
        foreach (var person in pending)
        {
            if (await reportingLine.ManagerOfAsync(person, ct) is { } manager)
            {
                byManager[manager] = byManager.GetValueOrDefault(manager) + 1;
            }
        }
        foreach (var (manager, count) in byManager)
        {
            await notifications.SendAsync(new NotificationRequest([manager], NotificationCategories.CriticalAnnouncement, TeamPending,
                $"/content/items/{item.Id}/team", new Dictionary<string, string> { ["count"] = count.ToString(CultureInfo.InvariantCulture) },
                DedupeKey: $"content:{item.Id:N}:team"), ct);
        }
        await session.ExecAsync("update content.content_item set deadline_reminded_at = @now where id = @id", new { id = item.Id, now }, ct);
        return pending.Length;
    }
}

/// <summary>
/// Keeps the audience of live critical items current (spec 009 section 10, edge case "the audience changes after
/// publishing"): on publish it takes every active member the audience matches, and on an org membership change it
/// adds or removes that one person. Confirmations already given stay. On a material version the "remind me later"
/// count and banner dismissals start again (section 6), and the send is audited with the audience size.
/// </summary>
public sealed class CriticalAudience(
    IDbSession session,
    Items items,
    Sites sites,
    AudienceFactsLoader facts,
    ITenantMemberships memberships,
    IOrganizationHierarchy hierarchy,
    INotificationSender notifications,
    IAuditWriter audit) : IMessageHandler<ContentPublished>, IMessageHandler<OrganizationMembershipChanged>
{
    public async Task HandleAsync(ContentPublished message, CancellationToken ct)
    {
        if (await items.RowAsync(message.ItemId, false, ct) is not { Status: ContentStatuses.Published, Critical: { } settings } item
            || item.PublishedVersion != message.Version)
        {
            return;
        }
        var targets = await TargetsAsync(item.Audience, ct);
        var users = targets.ToArray();
        await session.ExecAsync(
            """
            insert into content.audience_member (tenant_id, item_id, user_id)
            select platform.current_tenant_id(), @id, u from unnest(@users) u
            on conflict (tenant_id, item_id, user_id) do nothing
            """, new { id = item.Id, users }, ct);
        await session.ExecAsync("delete from content.audience_member where item_id = @id and not (user_id = any(@users))", new { id = item.Id, users }, ct);
        var version = (await items.VersionAsync(item.Id, message.Version, ct))!;
        if (!version.Material)
        {
            return;
        }
        await session.ExecAsync("update content.audience_member set remind_count = 0, remind_until = null, dismissed_at = null where item_id = @id",
            new { id = item.Id }, ct);
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentCriticalSent, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new
            {
                version = message.Version, audience = users.Length, settings.Presentation, settings.RequireAck, settings.RemindLaterMax, settings.DeadlineAt,
                settings.Dismissible, approver = version.Approver,
            }), ct);
        foreach (var chunk in users.Chunk(500))
        {
            // No title: the text stays behind the read check of the item.
            await notifications.SendAsync(new NotificationRequest(chunk, NotificationCategories.CriticalAnnouncement, CriticalAnnouncements.Sent,
                CriticalAnnouncements.ReadLink(item.Id), Urgent: true, DedupeKey: $"content:{item.Id:N}:critical:{message.Version}"), ct);
        }
    }

    public async Task HandleAsync(OrganizationMembershipChanged message, CancellationToken ct)
    {
        var live = await session.QueryAsync($"select {Items.Columns} from content.content_item where status = 'published' and critical is not null",
            null, Items.Read, ct);
        if (live.Count == 0)
        {
            return;
        }
        var active = (await memberships.GetAsync(message.UserId, ct))?.Status == MembershipStatus.Active;
        foreach (var item in live)
        {
            var match = active && (item.Audience.IsEveryone || AudienceRules.Matches(item.Audience, await facts.ForAsync(message.UserId, ct)));
            await session.ExecAsync(match
                ? "insert into content.audience_member (tenant_id, item_id, user_id) values (platform.current_tenant_id(), @id, @user) on conflict do nothing"
                : "delete from content.audience_member where item_id = @id and user_id = @user", new { id = item.Id, user = message.UserId }, ct);
        }
    }

    /// <summary>
    /// Active members the audience matches. When every clause names org units, only members of those units are
    /// evaluated; otherwise every active member is.
    /// </summary>
    private async Task<HashSet<Guid>> TargetsAsync(ContentAudience audience, CancellationToken ct)
    {
        var active = (await memberships.ListAsync(ct)).Where(m => m.Status == MembershipStatus.Active).Select(m => m.UserId).ToHashSet();
        if (audience.IsEveryone)
        {
            return active;
        }
        IEnumerable<Guid> candidates = active;
        if (audience.AnyOf!.All(c => c.OrgUnits is { Count: > 0 }))
        {
            var units = audience.AnyOf!.SelectMany(c => c.OrgUnits!).ToList();
            var subtrees = await hierarchy.GetSubtreesAsync(units.Where(u => u.Subtree).Select(u => u.Id), ct);
            var all = subtrees.Concat(units.Where(u => !u.Subtree).Select(u => u.Id)).Distinct().ToList();
            var members = await hierarchy.GetMemberUserIdsAsync(all, ct);
            candidates = active.Where(members.Contains);
        }
        var result = new HashSet<Guid>();
        foreach (var user in candidates)
        {
            if (AudienceRules.Matches(audience, await facts.ForAsync(user, ct)))
            {
                result.Add(user);
            }
        }
        return result;
    }
}

/// <summary>Writes the completion report (US2 scenario 7) to tenant storage: each targeted person, status, time, channel and version.</summary>
public sealed class AckReportHandler(IDbSession session, CriticalAnnouncements critical, IUserDirectory users, TenantObjectStorage storage, IClock clock)
    : IMessageHandler<AckReportRequested>
{
    public static string RelativeKey(Guid report) => $"exports/content/acknowledgements/{report}.csv";

    public async Task HandleAsync(AckReportRequested message, CancellationToken ct)
    {
        if (await session.ScalarAsync<int>("select count(*)::int from content.ack_report where id = @id and status = 'pending'", new { id = message.ReportId }, ct) == 0)
        {
            return;
        }
        var (_, people) = await critical.StatusAsync(message.ItemId, ct);
        var directory = (await users.ListAllAsync(ct)).ToDictionary(u => u.Id);
        using var buffer = new MemoryStream();
        await using (var writer = new StreamWriter(buffer, new UTF8Encoding(false), leaveOpen: true))
        {
            await writer.WriteLineAsync("user_id,display_name,email,status,acknowledged_at,channel,version");
            foreach (var p in people)
            {
                var info = directory.GetValueOrDefault(p.UserId);
                await writer.WriteLineAsync(string.Join(',', p.UserId, Csv(info?.DisplayName), Csv(info?.Email), p.At is null ? "pending" : "confirmed",
                    p.At?.ToString("O", CultureInfo.InvariantCulture), Csv(p.Channel), p.Version?.ToString(CultureInfo.InvariantCulture)));
            }
        }
        buffer.Position = 0;
        await storage.PutAsync(RelativeKey(message.ReportId), buffer, "text/csv", ct);
        var now = clock.UtcNow;
        await session.ExecAsync(
            "update content.ack_report set status = 'completed', row_count = @n, object_key = @key, completed_at = @now, expires_at = @expires where id = @id",
            new { n = people.Count, key = storage.FullKey(RelativeKey(message.ReportId)), now, expires = now.AddDays(7), id = message.ReportId }, ct);
    }

    /// <summary>CSV escaping with formula-injection protection for spreadsheet tools.</summary>
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        if (value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
