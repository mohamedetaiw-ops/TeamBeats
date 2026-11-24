using TeamBeats.Primitives;

namespace TeamBeats.Modules.Notifications.Contracts;

/// <summary>
/// One notification for a set of users in the current tenant (spec 015 FR-001, FR-002). The owning module decides who
/// is told and what is said; this module delivers it. Text comes from resource keys
/// <c>notifications.template.{TemplateKey}.title</c> and <c>.body</c>, rendered in each recipient's language with
/// <paramref name="Parameters"/>. Parameters must hold only what every recipient may see, never secrets (FR-007).
/// </summary>
/// <param name="Link">A path inside the tenant host, such as <c>/requests/123</c>. Absolute URLs are refused.</param>
/// <param name="DedupeKey">Repeats with the same key for the same user within 1 hour are dropped (FR-009).</param>
/// <param name="GroupKey">Unread notifications with the same key within 15 minutes become one entry with a count (FR-013).</param>
public sealed record NotificationRequest(
    IReadOnlyCollection<Guid> Recipients,
    string Category,
    string TemplateKey,
    string Link,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool Urgent = false,
    string? DedupeKey = null,
    string? GroupKey = null);

/// <summary>What happened to a send: created per recipient, folded into an existing group, dropped as a repeat, or skipped.</summary>
public sealed record SendResult(int Created, int Grouped, int Duplicates, int Skipped);

/// <summary>The only way a module tells people something (spec 015 NOT-001). Joins the caller's transaction.</summary>
public interface INotificationSender
{
    Task<SendResult> SendAsync(NotificationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Resolves notifications that asked people to act on something that is now done or no longer theirs (spec 015
    /// FR-025): every unread notification of <paramref name="category"/> in the current tenant whose link starts with
    /// <paramref name="linkPrefix"/> is marked read, and its emails that have not gone out are dropped. Returns how many
    /// notifications were resolved. Joins the caller's transaction.
    /// </summary>
    Task<int> ResolveAsync(string category, string linkPrefix, CancellationToken ct = default);
}

/// <summary>
/// How many people a set of notification links reached, per channel (spec 021 FR-009: the campaign funnel by channel).
/// Counts only, in the current tenant: the in-app count is the notifications created with the link, the others the
/// deliveries that went out. The caller decides who may see the counts; a link with no notification is left out.
/// </summary>
public interface INotificationReach
{
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> DeliveredByChannelAsync(IReadOnlyCollection<string> links, CancellationToken ct = default);
}

/// <summary>
/// One email to an address that is not a TeamBeats account (spec 022 FR-016: pre-boarding to the personal email HR
/// holds). Text comes from the same template keys as <see cref="NotificationRequest"/>, in the tenant's default
/// language unless <paramref name="Locale"/> names an enabled one. It is sent now, outside the queue and without an
/// inbox entry; the caller keeps its own state and retries on <see cref="ExternalEmailOutcomes.Retry"/>.
/// </summary>
/// <param name="Link">A path inside the tenant host. Absolute URLs are refused.</param>
public sealed record ExternalEmailRequest(string To, string TemplateKey, string Link, IReadOnlyDictionary<string, string>? Parameters = null, string? Locale = null);

public static class ExternalEmailOutcomes
{
    public const string Sent = "sent";

    /// <summary>The address was refused permanently.</summary>
    public const string Bounced = "bounced";

    /// <summary>The relay was down, busy or not set: worth trying again later.</summary>
    public const string Retry = "retry";
}

public interface IExternalEmail
{
    Task<string> SendAsync(ExternalEmailRequest request, CancellationToken ct = default);
}

/// <summary>A notification category. Mandatory categories ignore channel preferences and are labelled (FR-005).</summary>
public sealed record NotificationCategory(string Key, bool Mandatory, bool EmailByDefault);

/// <summary>
/// Platform categories (spec 015 section 6). Tenants may make more categories mandatory, never fewer. Owning modules
/// add their categories here when they start sending, so preferences always list every category.
/// </summary>
public static class NotificationCategories
{
    public const string SecurityNotice = "security_notice";
    public const string CriticalAnnouncement = "critical_announcement";
    public const string AttestationDue = "attestation_due";
    public const string ApprovalEscalation = "approval_escalation";
    public const string SiteEmergency = "site_emergency";
    public const string ApprovalWaiting = "approval_waiting";
    public const string RequestUpdate = "request_update";
    public const string Mention = "mention";
    public const string Comment = "comment";
    public const string Announcement = "announcement";
    public const string DocumentUpdate = "document_update";
    public const string Recognition = "recognition";
    public const string IntegrationAlert = "integration_alert";
    public const string System = "system";

    /// <summary>Spec 029: tasks given to the person, their due dates, and changes in spaces they belong to.</summary>
    public const string SpaceActivity = "space_activity";

    /// <summary>Spec 031: confirmations, reminders, releases and cancellations of the person's bookings.</summary>
    public const string Bookings = "bookings";

    /// <summary>Spec 012: new items that match a search the person saved with alerts on.</summary>
    public const string SearchAlerts = "search_alerts";

    /// <summary>Spec 013: an AI budget reached 80% or 100% of its limit, for the AI Policy Administrators and the budget owner.</summary>
    public const string AiBudgets = "ai_budgets";

    /// <summary>Spec 020: daily briefings from agents the person subscribed to.</summary>
    public const string AgentBriefings = "agent_briefings";

    public static readonly IReadOnlyList<NotificationCategory> All =
    [
        new(SecurityNotice, Mandatory: true, EmailByDefault: true),
        new(CriticalAnnouncement, Mandatory: true, EmailByDefault: true),
        new(AttestationDue, Mandatory: true, EmailByDefault: true),
        new(ApprovalEscalation, Mandatory: true, EmailByDefault: true),
        new(SiteEmergency, Mandatory: true, EmailByDefault: true),
        new(ApprovalWaiting, Mandatory: false, EmailByDefault: true),
        new(RequestUpdate, Mandatory: false, EmailByDefault: true),
        new(Mention, Mandatory: false, EmailByDefault: true),
        new(Comment, Mandatory: false, EmailByDefault: false),
        new(Announcement, Mandatory: false, EmailByDefault: false),
        new(DocumentUpdate, Mandatory: false, EmailByDefault: false),
        new(Recognition, Mandatory: false, EmailByDefault: false),
        new(IntegrationAlert, Mandatory: false, EmailByDefault: true),
        new(System, Mandatory: false, EmailByDefault: false),
        new(SpaceActivity, Mandatory: false, EmailByDefault: true),
        new(Bookings, Mandatory: false, EmailByDefault: true),
        new(SearchAlerts, Mandatory: false, EmailByDefault: false),
        new(AiBudgets, Mandatory: false, EmailByDefault: true),
        new(AgentBriefings, Mandatory: false, EmailByDefault: false),
    ];

    public static NotificationCategory? Find(string key) => All.FirstOrDefault(c => c.Key == key);
}

/// <summary>Delivery channels. R1 has in-app and email; Teams, push, Slack and SMS follow in R2.</summary>
public static class NotificationChannels
{
    public const string InApp = "in_app";
    public const string Email = "email";
    public const string Slack = "slack";
    public const string Push = "push";
    public const string Teams = "teams";
    public const string Sms = "sms";

    public static readonly IReadOnlyList<string> All = [InApp, Email, Slack, Push, Teams, Sms];
}

/// <summary>A device allowed push notifications for this person (spec 030 section 12). Only the platform family is told, never the device.</summary>
public sealed record PushSubscriptionCreated(Guid UserId, string Platform) : IIntegrationEvent;

/// <summary>A push subscription was removed: signed out, revoked, turned off, or the push service said it is gone.</summary>
public sealed record PushSubscriptionRemoved(Guid UserId, string Reason) : IIntegrationEvent;

public sealed record NotificationDelivered(Guid NotificationId, Guid UserId, string Channel) : IIntegrationEvent;

public sealed record NotificationBounced(Guid UserId, string Channel) : IIntegrationEvent;
