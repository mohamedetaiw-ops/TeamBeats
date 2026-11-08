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

    public static readonly IReadOnlyDictionary<string, ContentTypeSettings> Defaults = new Dictionary<string, ContentTypeSettings>(StringComparer.Ordinal)
    {
        [News] = new(News, RequiresOwner: false, ReviewMonths: null, ShowHistoryToReaders: false, UnderReviewLabel: true),
        [Announcement] = new(Announcement, RequiresOwner: false, ReviewMonths: null, ShowHistoryToReaders: false, UnderReviewLabel: true),
        [Page] = new(Page, RequiresOwner: true, ReviewMonths: 12, ShowHistoryToReaders: false, UnderReviewLabel: true),
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
