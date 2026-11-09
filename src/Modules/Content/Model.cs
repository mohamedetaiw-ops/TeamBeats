namespace TeamBeats.Modules.Content;

/// <summary>Item lifecycle (FR-002). Every move between them is audited.</summary>
public static class ContentStatuses
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Approved = "approved";
    public const string Scheduled = "scheduled";
    public const string Published = "published";
    public const string Expired = "expired";
    public const string Archived = "archived";

    public static readonly IReadOnlyList<string> All = [Draft, InReview, Approved, Scheduled, Published, Expired, Archived];

    /// <summary>States a reader can open by link: live, or expired and still inside the grace period.</summary>
    public static bool Readable(string status) => status is Published or Expired;
}

/// <summary>
/// Built-in content types of R1 with the defaults of spec 009 section 6: pages need an owner and a review every 12
/// months; news and announcements need neither. Tenants change the settings per type; custom types come later.
/// </summary>
public sealed record ContentTypeSettings(string Key, bool RequiresOwner, int? ReviewMonths, bool ShowHistoryToReaders, bool UnderReviewLabel);

public static class ContentTypes
{
    public const string News = "news";
    public const string Announcement = "announcement";
    public const string Page = "page";

    /// <summary>Knowledge articles (spec 009 content types, spec 011 US4), created from the knowledge base templates.</summary>
    public const string Knowledge = "knowledge";

    public static readonly IReadOnlyDictionary<string, ContentTypeSettings> Defaults = new Dictionary<string, ContentTypeSettings>(StringComparer.Ordinal)
    {
        [News] = new(News, RequiresOwner: false, ReviewMonths: null, ShowHistoryToReaders: false, UnderReviewLabel: true),
        [Announcement] = new(Announcement, RequiresOwner: false, ReviewMonths: null, ShowHistoryToReaders: false, UnderReviewLabel: true),
        [Page] = new(Page, RequiresOwner: true, ReviewMonths: 12, ShowHistoryToReaders: false, UnderReviewLabel: true),
        [Knowledge] = new(Knowledge, RequiresOwner: true, ReviewMonths: 12, ShowHistoryToReaders: false, UnderReviewLabel: true),
    };
}

/// <summary>
/// The approval route of a content type in a site (FR-003). Without approval, publishers publish directly. With it,
/// items go through one review step on the process engine (more steps with <c>content.multistep_approval</c> in R2): the
/// named approvers decide, or the site's publishers when no one is named or every named approver wrote the item.
/// </summary>
public sealed record ContentRoute(bool Approval, IReadOnlyList<Guid>? Approvers = null)
{
    public const int MaxApprovers = 20;
}

/// <summary>
/// How a critical announcement interrupts (FR-006): a modal on the next page, closed only by confirming or by "remind
/// me later" up to <see cref="RemindLaterMax"/> times, or a banner above the content until it expires, is confirmed
/// or, when <see cref="Dismissible"/>, dismissed. <see cref="RequireAck"/> counts it in the completion report and the
/// deadline reminders; a modal always asks for confirmation.
/// </summary>
public sealed record CriticalSettings(string Presentation, bool RequireAck, int RemindLaterMax, DateTimeOffset? DeadlineAt, bool Dismissible)
{
    public const string Modal = "modal";
    public const string Banner = "banner";
    public const int MaxRemindLater = 10;
    public const int DefaultRemindLater = 3;
    public static readonly TimeSpan RemindLaterFor = TimeSpan.FromHours(4);

    /// <summary>Whether confirmations are recorded and counted.</summary>
    public bool Confirms => RequireAck || Presentation == Modal;
}

/// <summary>Critical settings as the editor sends them; the deadline is a local time in the site's zone. Presentation "none" clears them.</summary>
public sealed record CriticalInput(string? Presentation, bool? RequireAck, int? RemindLaterMax, DateTime? Deadline, bool? Dismissible);

/// <summary>One locale of a version. <see cref="OutOfDate"/> marks a variant that fell behind the primary locale (US5 scenario 5).</summary>
public sealed record ContentVariant(string Title, string? Summary, BodyDocument Body, bool OutOfDate = false);

/// <summary>A person as the content pages show them.</summary>
public sealed record ContentPerson(Guid Id, string DisplayName);

public static class ContentLimits
{
    public const int TitleMax = 200;
    public const int SummaryMax = 500;
    public const int ChangeNoteMax = 500;
    public const int SiteNameMax = 100;

    /// <summary>Expired items stay readable by link with a label for this long, then archive (section 6).</summary>
    public static readonly TimeSpan ExpiredGrace = TimeSpan.FromDays(30);
}
