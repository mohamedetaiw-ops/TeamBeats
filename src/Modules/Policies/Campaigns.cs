using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

/// <summary>Notices of one campaign to a batch of its members, sent apart so a large launch commits at once.</summary>
public sealed record AttestationNoticeBatch(Guid CampaignId, string Template, IReadOnlyList<Guid> Recipients, string Dedupe) : IIntegrationEvent;

internal sealed record CampaignRow(
    Guid Id,
    Guid PolicyId,
    int Version,
    ContentAudience Audience,
    DateTimeOffset Deadline,
    Dictionary<string, string> Text,
    int[] ReminderDays,
    int LateJoinerDays,
    string[] Excluded,
    string Status,
    Guid LaunchedBy,
    DateTimeOffset LaunchedAt,
    DateTimeOffset? ClosedAt)
{
    public const string Columns =
        "id, policy_id, version, audience::text, deadline, attestation_text::text, reminder_days, late_joiner_days, excluded_membership_types, status, launched_by, launched_at, closed_at";

    public static CampaignRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), PolicyJson.Read<ContentAudience>(r.GetString(3)),
        r.GetFieldValue<DateTimeOffset>(4), PolicyJson.Read<Dictionary<string, string>>(r.GetString(5)), r.GetFieldValue<int[]>(6), r.GetInt32(7), r.GetFieldValue<string[]>(8),
        r.GetString(9), r.GetGuid(10), r.GetFieldValue<DateTimeOffset>(11), r.Get<DateTimeOffset?>("closed_at"));
}

/// <summary>
/// Attestation campaigns (spec 025 US2, FR-006 to FR-008). A campaign asks everyone in its audience, inside the
/// policy's own audience and outside the excluded membership types, to attest to one version by a deadline. Each
/// person gets an inbox task and a notice, reminders before their due date, and is overdue after it, when their line
/// manager hears of it (escalation, with <c>policy.campaigns</c>). People who join the audience later get their own
/// due date; people who leave it are marked so. Without <c>policy.campaigns</c> one campaign is open at a time.
/// Attestations are written only from the attesting person's own session and never change.
/// </summary>
public sealed class PolicyCampaigns(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    IEntitlementService entitlements,
    IAuthorizationProvisioning grants,
    IAudiences audiences,
    ITenantMemberships memberships,
    IReportingLine reportingLine,
    ITenantSettingsReader tenantSettings,
    ILocalizer localizer,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
    : IPersonalTaskSource, IMessageHandler<AttestationNoticeBatch>, IMessageHandler<OrganizationMembershipChanged>, IMessageHandler<TenantMembershipDisabled>,
        IMessageHandler<PolicyPublished>
{
    public const int DefaultDeadlineDays = 30;
    public const int DefaultLateJoinerDays = 14;
    public static readonly IReadOnlyList<int> DefaultReminderDays = [7, 2];
    public const int MaxReminders = 5;
    public const int MaxTextLength = 1000;
    public const int NoticeBatch = 500;
    public const string DefaultTextKey = "policies.attestation.default_text";

    public const string AttestationDueTemplate = "policies.attestation_due";
    public const string ReminderTemplate = "policies.attestation_reminder";
    public const string OverdueTemplate = "policies.attestation_overdue";
    public const string TeamOverdueTemplate = "policies.team_overdue";
    public const string OutdatedTemplate = "policies.campaign_outdated";

    public static string AttestLink(Guid policyId) => $"/policies/{policyId}?attest=1";

    public static string CampaignLink(Guid policyId, Guid campaignId) => $"/policies/{policyId}/campaigns/{campaignId}";

    private async Task<DateTimeOffset> EndOfAsync(DateOnly day, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
    }

    private async Task<DateOnly> TodayAsync(CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow.UtcDateTime, zone));
    }

    /// <summary>The owner and compliance officers run a policy's campaigns.</summary>
    private async Task<PolicyRow> RunsAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        var policy = await register.VisibleAsync(id, forUpdate, ct);
        if (policy.Owner != context.RequireUser() && !await register.ManagesAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        return policy;
    }

    internal async Task<CampaignRow?> RowAsync(Guid campaignId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {CampaignRow.Columns} from policies.campaign where id = @campaignId", new { campaignId }, CampaignRow.Read, ct);

    private async Task<CampaignCounts> CountsAsync(Guid campaignId, CancellationToken ct) =>
        (await session.QuerySingleOrDefaultAsync(
            """
            select count(*)::int, count(*) filter (where status = 'attested')::int, count(*) filter (where status = 'open')::int,
              count(*) filter (where status = 'overdue')::int, count(*) filter (where status = 'left_audience')::int
            from policies.campaign_member where campaign_id = @campaignId
            """, new { campaignId }, r => new CampaignCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)), ct))!;

    private async Task<CampaignView> ViewAsync(CampaignRow c, CancellationToken ct) =>
        new(c.Id, c.PolicyId, c.Version, c.Audience, c.Deadline, c.Text, c.ReminderDays, c.LateJoinerDays, c.Excluded, c.Status, await register.PersonAsync(c.LaunchedBy, ct),
            c.LaunchedAt, c.ClosedAt, await CountsAsync(c.Id, ct));

    /// <summary>Who a campaign asks: its audience, inside the policy's audience, active, and not of an excluded membership type.</summary>
    private async Task<HashSet<Guid>> TargetsAsync(PolicyRow policy, ContentAudience? narrowed, IReadOnlyCollection<string> excluded, CancellationToken ct)
    {
        var targets = (await audiences.MembersAsync(narrowed ?? policy.AppliesTo, ct)).ToHashSet();
        if (narrowed is not null)
        {
            targets.IntersectWith(await audiences.MembersAsync(policy.AppliesTo, ct));
        }
        if (excluded.Count > 0)
        {
            var types = (await memberships.ListAsync(ct)).ToDictionary(m => m.UserId, m => m.MembershipType);
            targets.RemoveWhere(u => types.TryGetValue(u, out var type) && excluded.Contains(type));
        }
        return targets;
    }

    private async Task<bool> TargetsUserAsync(PolicyRow policy, CampaignRow campaign, Guid user, CancellationToken ct)
    {
        if (await memberships.GetAsync(user, ct) is not { Status: MembershipStatus.Active } membership || campaign.Excluded.Contains(membership.MembershipType))
        {
            return false;
        }
        return await audiences.IncludesAsync(campaign.Audience, user, ct) && await audiences.IncludesAsync(policy.AppliesTo, user, ct);
    }

    // ---- Launch, list, remind and close ----

    /// <summary>Launches a campaign for the version in force (US2 scenario 1).</summary>
    public async Task<CampaignView> LaunchAsync(Guid id, CampaignRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var policy = await RunsAsync(id, forUpdate: true, ct);
        if (policy.Status == PolicyStatuses.Retired)
        {
            throw AppException.Conflict("conflict.policies.retired");
        }
        if (policy.CurrentVersion is not { } number || await register.VersionByNumberAsync(id, number, ct) is not { } version)
        {
            throw AppException.Conflict("conflict.policies.not_published");
        }
        if (await session.ScalarAsync<int>("select count(*)::int from policies.campaign where policy_id = @id and version = @number and status = 'open'", new { id, number }, ct) > 0)
        {
            throw AppException.Conflict("conflict.policies.campaign_open");
        }
        // policy.core allows one open campaign at a time in the tenant; policy.campaigns lifts the limit (section 9).
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyCampaigns, ct)
            && await session.ScalarAsync<int>("select count(*)::int from policies.campaign where status = 'open'", null, ct) > 0)
        {
            throw AppException.NotInPackage(Capabilities.PolicyCampaigns);
        }
        var errors = new ValidationErrors();
        var today = await TodayAsync(ct);
        var day = request.Deadline ?? today.AddDays(DefaultDeadlineDays);
        if (day <= today)
        {
            errors.Add("deadline", "validation.policies.deadline");
        }
        var reminders = (request.ReminderDays ?? DefaultReminderDays).ToArray();
        if (reminders.Length > MaxReminders || reminders.Any(d => d is < 1 or > 90) || reminders.Distinct().Count() != reminders.Length)
        {
            errors.Add("reminderDays", "validation.policies.reminder_days");
        }
        var late = request.LateJoinerDays ?? DefaultLateJoinerDays;
        if (late is < 1 or > 365)
        {
            errors.Add("lateJoinerDays", "validation.policies.late_joiner_days");
        }
        var excluded = (request.ExcludedMembershipTypes ?? []).Distinct().ToArray();
        if (excluded.Any(t => !MembershipTypes.All.Contains(t)))
        {
            errors.Add("excludedMembershipTypes", "validation.policies.membership_type");
        }
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var locale in version.Locales)
        {
            var given = request.AttestationText?.GetValueOrDefault(locale)?.Trim();
            text[locale] = string.IsNullOrEmpty(given) ? localizer.Get(DefaultTextKey, locale) : given;
        }
        if (request.AttestationText?.Keys.Any(l => !version.Locales.Contains(l)) == true || text.Values.Any(t => t.Length > MaxTextLength))
        {
            errors.Add("attestationText", "validation.policies.attestation_text");
        }
        audiences.Validate(request.Audience, "audience", errors);
        errors.ThrowIfAny();

        var narrowed = request.Audience is { IsEveryone: false } requested ? requested : null;
        var audience = narrowed ?? policy.AppliesTo;
        var deadline = await EndOfAsync(day, ct);
        var targets = (await TargetsAsync(policy, narrowed, excluded, ct)).ToArray();
        var campaignId = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into policies.campaign (tenant_id, id, policy_id, version, audience, deadline, attestation_text, reminder_days, late_joiner_days,
              excluded_membership_types, status, launched_by, launched_at)
            values (platform.current_tenant_id(), @campaignId, @id, @number, @audience, @deadline, @text, @reminders, @late, @excluded, 'open', @user, @now)
            """,
            new
            {
                campaignId, id, number, audience = new Jsonb(PolicyJson.Write(audience)), deadline, text = new Jsonb(PolicyJson.Write(text)), reminders, late, excluded, user, now,
            }, ct);
        await session.ExecAsync(
            """
            insert into policies.campaign_member (tenant_id, campaign_id, user_id, joined_at, due_at, status)
            select platform.current_tenant_id(), @campaignId, u, @now, @deadline, 'open' from unnest(@targets) u
            """, new { campaignId, targets, now, deadline }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyCampaignLaunched, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { campaign = campaignId, version = number, audience = targets.Length, deadline, reminders, lateJoinerDays = late, excluded }), ct);
        // The notices go out in batches of their own, so the members commit at once however large the audience.
        foreach (var chunk in targets.Chunk(NoticeBatch))
        {
            await outbox.EnqueueAsync(new AttestationNoticeBatch(campaignId, AttestationDueTemplate, chunk, "launch"), ct);
        }
        return await ViewAsync((await RowAsync(campaignId, ct))!, ct);
    }

    public async Task<IReadOnlyList<CampaignView>> ListAsync(Guid id, CancellationToken ct)
    {
        await RunsAsync(id, false, ct);
        var list = new List<CampaignView>();
        foreach (var c in await session.QueryAsync($"select {CampaignRow.Columns} from policies.campaign where policy_id = @id order by launched_at desc, id", new { id },
            CampaignRow.Read, ct))
        {
            list.Add(await ViewAsync(c, ct));
        }
        return list;
    }

    private async Task<(PolicyRow Policy, CampaignRow Campaign)> CampaignAsync(Guid id, Guid campaignId, bool forUpdate, CancellationToken ct)
    {
        var policy = await RunsAsync(id, forUpdate, ct);
        var campaign = await RowAsync(campaignId, ct);
        if (campaign is null || campaign.PolicyId != id)
        {
            throw AppException.NotFound();
        }
        return (policy, campaign);
    }

    public async Task<CampaignView> GetAsync(Guid id, Guid campaignId, CancellationToken ct) => await ViewAsync((await CampaignAsync(id, campaignId, false, ct)).Campaign, ct);

    /// <summary>Reminds everyone who has not attested yet, now (US2: compliance sends a reminder to the rest).</summary>
    public async Task<CampaignView> RemindAsync(Guid id, Guid campaignId, CancellationToken ct)
    {
        var (policy, campaign) = await CampaignAsync(id, campaignId, false, ct);
        if (campaign.Status != "open")
        {
            throw AppException.Conflict("conflict.policies.campaign_closed");
        }
        var pending = (await session.QueryAsync("select user_id from policies.campaign_member where campaign_id = @campaignId and status in ('open', 'overdue')",
            new { campaignId }, r => r.GetGuid(0), ct)).ToArray();
        var stamp = clock.UtcNow.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var chunk in pending.Chunk(NoticeBatch))
        {
            await outbox.EnqueueAsync(new AttestationNoticeBatch(campaignId, ReminderTemplate, chunk, "manual:" + stamp), ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyCampaignReminded, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { campaign = campaignId, recipients = pending.Length }), ct);
        return await ViewAsync(campaign, ct);
    }

    /// <summary>Closes a campaign; its tasks leave the inbox. Attestations given stay; open members stay as they were.</summary>
    public async Task<CampaignView> CloseAsync(Guid id, Guid campaignId, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (policy, campaign) = await CampaignAsync(id, campaignId, true, ct);
        if (campaign.Status != "open")
        {
            throw AppException.Conflict("conflict.policies.campaign_closed");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update policies.campaign set status = 'closed', closed_by = @user, closed_at = @now where id = @campaignId", new { campaignId, user, now }, ct);
        var counts = await CountsAsync(campaignId, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyCampaignClosed, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { campaign = campaignId, version = campaign.Version, counts.Members, counts.Attested, counts.Overdue }), ct);
        return await ViewAsync((await RowAsync(campaignId, ct))!, ct);
    }

    // ---- The person ----

    /// <summary>Open or overdue memberships of the user in open campaigns, earliest due first.</summary>
    private async Task<List<(CampaignRow Campaign, DateTimeOffset JoinedAt, DateTimeOffset DueAt, string Status)>> OwedAsync(Guid user, Guid? policyId, CancellationToken ct) =>
        await session.QueryAsync(
            $"""
            select {string.Join(", ", CampaignRow.Columns.Split(", ").Select(c => "c." + c))}, m.joined_at, m.due_at, m.status
            from policies.campaign_member m join policies.campaign c on c.id = m.campaign_id join policies.policy p on p.id = c.policy_id
            where m.user_id = @user and m.status in ('open', 'overdue') and c.status = 'open' and p.status <> 'retired' and (@policyId::uuid is null or c.policy_id = @policyId)
            order by m.due_at, c.id
            """, new { user, policyId }, r => (CampaignRow.Read(r), r.GetFieldValue<DateTimeOffset>(13), r.GetFieldValue<DateTimeOffset>(14), r.GetString(15)), ct);

    private static AttestationDue Due(CampaignRow c, DateTimeOffset dueAt, string status) => new(c.Id, c.Version, dueAt, status == MemberStatuses.Overdue, c.Text);

    /// <summary>The person's own attestations in the inbox (US2 scenario 1). They never go to a delegate (section 4).</summary>
    public async Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyCore, ct))
        {
            return [];
        }
        var list = new List<PersonalTask>();
        foreach (var (campaign, joined, due, _) in await OwedAsync(userId, null, ct))
        {
            var policy = (await register.RowAsync(campaign.PolicyId, false, ct))!;
            list.Add(new PersonalTask("policy_attestation", campaign.Id, policy.Names, AttestLink(policy.Id), joined, due));
        }
        return list;
    }

    private async Task<(int Version, DateTimeOffset At)?> LastAttestationAsync(Guid user, Guid policyId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync<(int, DateTimeOffset)?>(
            "select version, attested_at from policies.attestation where user_id = @user and policy_id = @policyId order by attested_at desc, id limit 1",
            new { user, policyId }, r => (r.GetInt32(0), r.GetFieldValue<DateTimeOffset>(1)), ct);

    /// <summary>The current user's attestation state for one policy, for the attest panel on its page.</summary>
    public async Task<MyAttestation> MineAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var policy = await register.VisibleAsync(id, false, ct);
        var owed = (await OwedAsync(user, policy.Id, ct)).FirstOrDefault();
        var last = await LastAttestationAsync(user, policy.Id, ct);
        return new MyAttestation(owed.Campaign is null ? null : Due(owed.Campaign, owed.DueAt, owed.Status), last?.Version, last?.At);
    }

    /// <summary>
    /// My policies (section 10): the published policies that apply to the person and any they still owe an attestation
    /// for, with what is due and the last version they attested to.
    /// </summary>
    public async Task<IReadOnlyList<MyPolicyItem>> MyPoliciesAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        var user = context.RequireUser();
        var owed = (await OwedAsync(user, null, ct)).GroupBy(o => o.Campaign.PolicyId).ToDictionary(g => g.Key, g => g.First());
        var rows = await session.QueryAsync($"select {PolicyRow.Columns} from policies.policy where (current_version is not null and status <> 'retired') or id = any(@owed) order by lower(code), id",
            new { owed = owed.Keys.ToArray() }, PolicyRow.Read, ct);
        var last = (await session.QueryAsync(
            "select distinct on (policy_id) policy_id, version, attested_at from policies.attestation where user_id = @user order by policy_id, attested_at desc, id",
            new { user }, r => (Policy: r.GetGuid(0), Version: r.GetInt32(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct)).ToDictionary(a => a.Policy);
        var list = new List<MyPolicyItem>();
        foreach (var p in rows)
        {
            var due = owed.TryGetValue(p.Id, out var o) ? Due(o.Campaign, o.DueAt, o.Status) : null;
            if (due is null && !await register.AppliesToMeAsync(p, ct))
            {
                continue;
            }
            var attested = last.TryGetValue(p.Id, out var a) ? a : default;
            list.Add(new MyPolicyItem(await register.SummaryAsync(p, ct), p.CurrentVersion, p.EffectiveFrom, due, attested.Policy == Guid.Empty ? null : attested.Version,
                attested.Policy == Guid.Empty ? null : attested.At));
        }
        return list.OrderBy(i => i.Due is null).ThenBy(i => i.Due?.DueAt).ToList();
    }

    /// <summary>
    /// The person attests (US2 scenario 2) to the version their campaign asks for. The record keeps the text shown,
    /// the policy text and attestation text in the chosen language with their hash, the channel and the time.
    /// </summary>
    public async Task<AttestationView> AttestAsync(Guid id, AttestRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var policy = await register.VisibleAsync(id, false, ct);
        var owed = (await OwedAsync(user, policy.Id, ct)).Where(o => request.CampaignId is null || o.Campaign.Id == request.CampaignId).ToList();
        if (owed.Count == 0)
        {
            throw AppException.Conflict("conflict.policies.nothing_to_attest");
        }
        var (campaign, _, _, _) = owed[0];
        var version = await register.VersionByNumberAsync(policy.Id, campaign.Version, ct) ?? throw AppException.NotFound();
        var channel = request.Channel ?? AttestationChannels.Web;
        var errors = new ValidationErrors();
        if (!AttestationChannels.Online.Contains(channel))
        {
            errors.Add("channel", "validation.policies.channel");
        }
        var locale = request.Locale ?? policy.BindingLocale ?? version.Locales[0];
        if (!version.Locales.Contains(locale))
        {
            errors.Add("locale", "validation.policies.locale");
        }
        errors.ThrowIfAny();
        var text = campaign.Text.GetValueOrDefault(locale) ?? localizer.Get(DefaultTextKey, locale);
        var shown = string.Join("\n\n", version.Titles.GetValueOrDefault(locale) ?? string.Empty,
            string.Join("\n\n", version.Clauses.Select(c => ClauseRules.Text(c, locale))), text);
        var hash = ClauseRules.Hash(shown);
        var attestationId = Ids.New();
        var now = clock.UtcNow;
        var inserted = await session.ExecAsync(
            """
            insert into policies.attestation (tenant_id, id, user_id, policy_id, version, campaign_id, attestation_text, locale, text_hash, channel, source, attested_at)
            values (platform.current_tenant_id(), @attestationId, @user, @policy, @version, @campaign, @text, @locale, @hash, @channel, 'online', @now)
            on conflict do nothing
            """, new { attestationId, user, policy = policy.Id, version = campaign.Version, campaign = campaign.Id, text, locale, hash, channel, now }, ct);
        if (inserted == 0)
        {
            throw AppException.Conflict("conflict.policies.already_attested");
        }
        await session.ExecAsync("update policies.campaign_member set status = 'attested', attested_at = @now where campaign_id = @campaign and user_id = @user",
            new { campaign = campaign.Id, user, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyAttested, Securables.Policy, policy.Id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { version = campaign.Version, campaign = campaign.Id, channel, locale, hash }), ct);
        await outbox.EnqueueAsync(new PolicyAttested(policy.Id, campaign.Version, user, campaign.Id, channel), ct);
        return new AttestationView(attestationId, policy.Id, campaign.Version, campaign.Id, locale, text, hash, channel, now);
    }

    // ---- Background work and events ----

    public async Task HandleAsync(AttestationNoticeBatch message, CancellationToken ct)
    {
        if (await RowAsync(message.CampaignId, ct) is not { Status: "open" } campaign)
        {
            return;
        }
        // No title: the policy's names stay behind its read check.
        await notifications.SendAsync(new NotificationRequest(message.Recipients, NotificationCategories.AttestationDue, message.Template, AttestLink(campaign.PolicyId),
            DedupeKey: $"policies.campaign:{campaign.Id:N}:{message.Template}:{message.Dedupe}"), ct);
    }

    /// <summary>
    /// For one tenant: reminders the configured days before each person's due date (US2 scenario 3), then overdue
    /// members, who hear it, and with <c>policy.campaigns</c> their line managers get the list of their people.
    /// </summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var moved = 0;
        var reminders = await session.QueryAsync(
            """
            select m.campaign_id, m.user_id, array_agg(d order by d) from policies.campaign_member m
            join policies.campaign c on c.id = m.campaign_id
            cross join lateral unnest(c.reminder_days) d
            where c.status = 'open' and m.status = 'open' and m.due_at > @now and m.due_at - make_interval(days => d) <= @now and not (d = any(m.reminders_sent))
            group by m.campaign_id, m.user_id
            order by m.campaign_id, m.user_id limit @limit
            """, new { now, limit }, r => (Campaign: r.GetGuid(0), User: r.GetGuid(1), Days: r.GetFieldValue<int[]>(2)), ct);
        foreach (var group in reminders.GroupBy(r => (r.Campaign, Day: r.Days.Min())))
        {
            var users = group.Select(g => g.User).ToArray();
            await session.ExecAsync(
                """
                update policies.campaign_member m set reminders_sent = array(select x from unnest(c.reminder_days) x where m.due_at - make_interval(days => x) <= @now)
                from policies.campaign c where c.id = m.campaign_id and m.campaign_id = @campaign and m.user_id = any(@users)
                """, new { campaign = group.Key.Campaign, users, now }, ct);
            await outbox.EnqueueAsync(new AttestationNoticeBatch(group.Key.Campaign, ReminderTemplate, users, "day:" + group.Key.Day), ct);
            moved += users.Length;
        }

        var overdue = await session.QueryAsync(
            """
            update policies.campaign_member m set status = 'overdue'
            from policies.campaign c
            where c.id = m.campaign_id and c.status = 'open' and m.status = 'open' and m.due_at <= @now
              and (m.campaign_id, m.user_id) in (
                select m2.campaign_id, m2.user_id from policies.campaign_member m2 join policies.campaign c2 on c2.id = m2.campaign_id
                where c2.status = 'open' and m2.status = 'open' and m2.due_at <= @now order by m2.due_at limit @limit)
            returning m.campaign_id, m.user_id, c.policy_id, c.version
            """, new { now, limit }, r => (Campaign: r.GetGuid(0), User: r.GetGuid(1), Policy: r.GetGuid(2), Version: r.GetInt32(3)), ct);
        if (overdue.Count == 0)
        {
            return moved;
        }
        var escalate = await entitlements.IsEnabledAsync(Capabilities.PolicyCampaigns, ct);
        var day = now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var campaign in overdue.GroupBy(o => (o.Campaign, o.Policy, o.Version)))
        {
            var users = campaign.Select(o => o.User).ToArray();
            foreach (var user in users)
            {
                await outbox.EnqueueAsync(new AttestationOverdue(campaign.Key.Policy, campaign.Key.Version, campaign.Key.Campaign, user), ct);
            }
            await outbox.EnqueueAsync(new AttestationNoticeBatch(campaign.Key.Campaign, OverdueTemplate, users, "overdue:" + day), ct);
            moved += users.Length;
            if (!escalate)
            {
                continue;
            }
            var managers = new HashSet<Guid>();
            foreach (var user in users)
            {
                if (await reportingLine.ManagerOfAsync(user, ct) is { } manager)
                {
                    managers.Add(manager);
                }
            }
            if (managers.Count > 0)
            {
                await notifications.SendAsync(new NotificationRequest(managers, NotificationCategories.AttestationDue, TeamOverdueTemplate,
                    $"/policies/{campaign.Key.Policy}/compliance", DedupeKey: $"policies.campaign:{campaign.Key.Campaign:N}:team_overdue:{day}"), ct);
            }
        }
        return moved;
    }

    /// <summary>Late joiners get their own due date; leavers are marked with the date (US2 scenarios 4 and 5).</summary>
    public async Task HandleAsync(OrganizationMembershipChanged message, CancellationToken ct) => await AudienceChangedAsync(message.UserId, ct);

    public async Task HandleAsync(TenantMembershipDisabled message, CancellationToken ct) => await AudienceChangedAsync(message.UserId, ct);

    private async Task AudienceChangedAsync(Guid user, CancellationToken ct)
    {
        var open = await session.QueryAsync($"select {CampaignRow.Columns} from policies.campaign where status = 'open'", null, CampaignRow.Read, ct);
        var now = clock.UtcNow;
        foreach (var campaign in open)
        {
            if (await register.RowAsync(campaign.PolicyId, false, ct) is not { } policy || policy.Status == PolicyStatuses.Retired)
            {
                continue;
            }
            var status = await session.ScalarAsync<string>("select status from policies.campaign_member where campaign_id = @c and user_id = @user", new { c = campaign.Id, user }, ct);
            var targeted = await TargetsUserAsync(policy, campaign, user, ct);
            if (targeted && status is null or MemberStatuses.LeftAudience)
            {
                await session.ExecAsync(
                    """
                    insert into policies.campaign_member (tenant_id, campaign_id, user_id, joined_at, due_at, status)
                    values (platform.current_tenant_id(), @c, @user, @now, @due, 'open')
                    on conflict (tenant_id, campaign_id, user_id) do update set status = 'open', joined_at = @now, due_at = @due, left_at = null, reminders_sent = '{}'
                    """, new { c = campaign.Id, user, now, due = now.AddDays(campaign.LateJoinerDays) }, ct);
                await outbox.EnqueueAsync(new AttestationNoticeBatch(campaign.Id, AttestationDueTemplate, [user], "joined:" + now.ToUnixTimeSeconds()), ct);
            }
            else if (!targeted && status is MemberStatuses.Open or MemberStatuses.Overdue)
            {
                await session.ExecAsync("update policies.campaign_member set status = 'left_audience', left_at = @now where campaign_id = @c and user_id = @user",
                    new { c = campaign.Id, user, now }, ct);
            }
        }
    }

    /// <summary>
    /// A material revision came into force while a campaign for an older version is open (US2 scenario 6): the owner
    /// and compliance officers choose to close it and launch a new one, or keep it for the old version.
    /// </summary>
    public async Task HandleAsync(PolicyPublished message, CancellationToken ct)
    {
        if (!message.Material)
        {
            return;
        }
        var older = await session.QueryAsync("select id from policies.campaign where policy_id = @policy and status = 'open' and version < @version order by launched_at",
            new { policy = message.PolicyId, version = message.Version }, r => r.GetGuid(0), ct);
        if (older.Count == 0 || await register.RowAsync(message.PolicyId, false, ct) is not { } policy)
        {
            return;
        }
        var recipients = (await grants.TenantHoldersAsync(Permissions.PolicyManage, ct)).Append(policy.Owner).Distinct().ToArray();
        foreach (var campaign in older)
        {
            await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.DocumentUpdate, OutdatedTemplate, CampaignLink(policy.Id, campaign),
                DedupeKey: $"policies.campaign:{campaign:N}:outdated:{message.Version}"), ct);
        }
    }
}
