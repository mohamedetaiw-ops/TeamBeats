using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

public sealed record ConversationSummary(Guid Id, string Title, int Turns, DateTimeOffset CreatedAt, DateTimeOffset LastTurnAt);

/// <summary>A conversation with its turns. <paramref name="Scope"/> is the item it is kept to, if any (spec 025 FR-011).</summary>
public sealed record ConversationView(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset LastTurnAt, IReadOnlyList<TurnView> Turns,
    AssistantScope? Scope = null);

public sealed record FeedbackRequest(bool? Helpful, string? Reason);

public sealed record AssistantSettingsView(int RetentionDays, DateTimeOffset? UpdatedAt);

public sealed record AssistantSettingsRequest(int? RetentionDays);

/// <summary>
/// Conversation history, feedback and retention (spec 014 T006, US4, FR-008, FR-009). A person sees and deletes only
/// their own conversations; every query names the user as well as the tenant RLS. Feedback outlives the conversation
/// for 25 months with the turn ID as its reference, and holds no conversation text (section 8).
/// </summary>
public sealed class AssistantHistory(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAuthorizationService authz,
    IAuditWriter audit, IClock clock)
{
    public const int DefaultRetentionDays = 30;
    public const int MaxRetentionDays = 365;
    public const int FeedbackRetentionMonths = 25;
    public const int MaxConversations = 100;
    public static readonly IReadOnlyList<string> FeedbackReasons = ["incorrect", "not_relevant", "incomplete", "unclear", "other"];

    private async Task<Guid> UserAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAssistant, ct);
        return context.RequireUser();
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct)
    {
        var user = await UserAsync(ct);
        return await session.QueryAsync(
            "select id, title, turns, created_at, last_turn_at from agents.conversation where user_id = @user order by last_turn_at desc, id limit @n",
            new { user, n = MaxConversations },
            r => new ConversationSummary(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4)), ct);
    }

    public async Task<ConversationView> GetAsync(Guid id, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var head = await session.QueryAsync(
            "select title, created_at, last_turn_at, scope_source, scope_item_id from agents.conversation where id = @id and user_id = @user",
            new { id, user }, r => (Title: r.GetString(0), Created: r.GetFieldValue<DateTimeOffset>(1), Last: r.GetFieldValue<DateTimeOffset>(2),
                Scope: r.Get<string?>("scope_source") is { } source ? new AssistantScope(source, r.GetGuid(4)) : null), ct);
        if (head.Count == 0)
        {
            throw AppException.NotFound();
        }
        var turns = await session.QueryAsync(
            """
            select t.id, t.question, t.answer, t.outcome, t.reason, t.language, t.agent, t.citations::text, t.withheld, t.retrieval_degraded, t.created_at, f.helpful
            from agents.turn t
            left join agents.feedback f on f.turn_id = t.id and f.user_id = t.user_id
            where t.conversation_id = @id and t.user_id = @user
            order by t.position
            """, new { id, user },
            r => new TurnView(r.GetGuid(0), id, r.GetString(1), r.Get<string?>("answer"), r.GetString(3), r.Get<string?>("reason"), r.GetString(5),
                r.Get<string?>("agent"), JsonSerializer.Deserialize<List<CitationView>>(r.GetString(7), AssistantService.Json) ?? [], r.GetInt32(8) > 0,
                r.GetBoolean(9), r.Get<string?>("answer") is not null, r.GetFieldValue<DateTimeOffset>(10), r.Get<bool?>("helpful")), ct);
        return new ConversationView(id, head[0].Title, head[0].Created, head[0].Last, turns, head[0].Scope);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var removed = await session.ExecAsync("delete from agents.conversation where id = @id and user_id = @user", new { id, user }, ct);
        if (removed == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AssistantHistoryDeleted, "assistant_conversation", id.ToString(),
            Changes: new { scope = "conversation", conversations = 1 }), ct);
    }

    public async Task<int> DeleteAllAsync(CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var removed = await session.ExecAsync("delete from agents.conversation where user_id = @user", new { user }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AssistantHistoryDeleted, "assistant_history", user.ToString(),
            Changes: new { scope = "all", conversations = removed }), ct);
        return removed;
    }

    public async Task GiveFeedbackAsync(Guid turnId, FeedbackRequest request, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var errors = new ValidationErrors();
        if (request.Helpful is null)
        {
            errors.Add("helpful", "validation.assistant.feedback");
        }
        if (request.Reason is not null && !FeedbackReasons.Contains(request.Reason))
        {
            errors.Add("reason", "validation.assistant.feedback_reason");
        }
        errors.ThrowIfAny();
        var saved = await session.ExecAsync(
            """
            insert into agents.feedback (tenant_id, turn_id, user_id, helpful, reason, agent, outcome, model, prompt_version, created_at)
            select platform.current_tenant_id(), t.id, t.user_id, @helpful, @reason, t.agent, t.outcome, t.model, t.prompt_version, @now
            from agents.turn t where t.id = @turn and t.user_id = @user
            on conflict (tenant_id, turn_id, user_id) do update set helpful = excluded.helpful, reason = excluded.reason, created_at = excluded.created_at
            """, new { turn = turnId, user, helpful = request.Helpful, reason = request.Reason, now = clock.UtcNow }, ct);
        if (saved == 0)
        {
            throw AppException.NotFound();
        }
    }

    private async Task<int> RetentionAsync(CancellationToken ct) =>
        await session.ScalarAsync<int?>("select retention_days from agents.assistant_settings", null, ct) ?? DefaultRetentionDays;

    public async Task<AssistantSettingsView> SettingsAsync(CancellationToken ct)
    {
        if (!(await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed &&
            !(await authz.CheckAsync(Permissions.AiPolicyView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var row = await session.QueryAsync("select retention_days, updated_at from agents.assistant_settings", null,
            r => new AssistantSettingsView(r.GetInt32(0), r.GetFieldValue<DateTimeOffset>(1)), ct);
        return row.Count == 0 ? new AssistantSettingsView(DefaultRetentionDays, null) : row[0];
    }

    public async Task<AssistantSettingsView> ChangeSettingsAsync(AssistantSettingsRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        var by = context.RequireUser();
        if (request.RetentionDays is not { } days || days < 1 || days > MaxRetentionDays)
        {
            throw AppException.Validation("retentionDays", "validation.assistant.retention");
        }
        var old = await RetentionAsync(ct);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into agents.assistant_settings (tenant_id, retention_days, updated_at, updated_by) values (platform.current_tenant_id(), @days, @now, @by)
            on conflict (tenant_id) do update set retention_days = excluded.retention_days, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, new { days, now, by }, ct);
        if (old != days)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AssistantRetentionChanged, "assistant_settings", "retention",
                Changes: new { old, @new = days }), ct);
        }
        return new AssistantSettingsView(days, now);
    }

    /// <summary>
    /// The tenant retention job (T001): conversations idle longer than the retention period go with their turns, and
    /// feedback older than 25 months. Runs in a tenant context with no user.
    /// </summary>
    public async Task<(int Conversations, int Feedback)> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        context.RequireTenant();
        var days = await RetentionAsync(ct);
        var conversations = await session.ExecAsync("delete from agents.conversation where last_turn_at < @cutoff",
            new { cutoff = now.AddDays(-days) }, ct);
        var feedback = await session.ExecAsync("delete from agents.feedback where created_at < @cutoff",
            new { cutoff = now.AddMonths(-FeedbackRetentionMonths) }, ct);
        return (conversations, feedback);
    }
}
