using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>What became of a write tool call of an external AI client (spec 035 FR-022).</summary>
public static class ExternalActionStatuses
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";
    public const string Expired = "expired";

    /// <summary>Confirmed, but the gateway or the owning module refused it when it ran.</summary>
    public const string Failed = "failed";
}

/// <summary>A stored pending action, as the gateway runs it once confirmed.</summary>
internal sealed record ExternalActionRow(Guid Id, Guid AppId, string ClientId, string AppName, Guid UserId, string Tool, string[] ScopedTools, string Hash,
    string Parameters, string Status, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DecidedAt);

/// <summary>
/// The card the employee confirms or rejects (spec 035 US3 scenario 5): the AI client that asked, the tool, what it
/// changes, every parameter as the client sent it, the hash the decision is bound to and when it expires. It always
/// says the request came from an external AI client, so values in it may come from content the client read.
/// </summary>
public sealed record ExternalActionView(Guid Id, string AppName, string ClientId, string Tool, string SideEffect, JsonElement Parameters, string Hash,
    string Status, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DecidedAt, JsonElement? Result);

public sealed record ExternalActionDecision(bool? Confirm, string? Hash);

/// <summary>
/// The employee's side of external AI client actions: their pending actions, one action, and the decision. Only the
/// employee the client acted for sees or decides an action; to anyone else it does not exist. Confirming runs exactly
/// the stored parameters through the gateway, which checks again that the client, the tool and the scopes still allow
/// it; rejecting or letting it expire does nothing. The decision resolves the notification that asked for it.
/// </summary>
public sealed class ExternalActions(IDbSession session, ITenantContext context, ExternalToolGateway gateway, ToolRegistry tools,
    INotificationSender notifications, IClock clock)
{
    /// <summary>The notification template of a pending action (spec 015), in the approval waiting category.</summary>
    public const string Template = "agents.external_action";

    public static string Link(Guid id) => $"/ai-actions/{id}";

    private const string Columns =
        "id, app_id, client_id, app_name, user_id, tool, scoped_tools, parameter_hash, parameters, status, reason, created_at, expires_at, decided_at";

    private static ExternalActionRow Read(System.Data.Common.DbDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetFieldValue<string[]>(6), r.GetString(7), r.GetString(8),
        r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetFieldValue<DateTimeOffset>(11), r.GetFieldValue<DateTimeOffset>(12),
        r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13));

    private Guid User() => context.ActorType == ActorType.User && context.ActorId is { } user ? user : throw AppException.Unauthenticated();

    /// <summary>The employee's actions still waiting for them, oldest first.</summary>
    public async Task<IReadOnlyList<ExternalActionView>> PendingAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            $"select {Columns} from agents.external_action where user_id = @user and status = 'pending' and expires_at > @now order by created_at limit 50",
            new { user = User(), now = clock.UtcNow }, Read, ct);
        return rows.Select(r => View(r, null)).ToList();
    }

    public async Task<ExternalActionView> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await FindAsync(id, ct);
        var result = await session.ScalarAsync<string>("select result from agents.external_action where id = @id", new { id }, ct);
        return View(row, result);
    }

    private async Task<ExternalActionRow> FindAsync(Guid id, CancellationToken ct, bool forUpdate = false)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            $"select {Columns} from agents.external_action where id = @id and user_id = @user{(forUpdate ? " for update" : "")}", new { id, user = User() }, Read, ct);
        return row ?? throw AppException.NotFound();
    }

    private ExternalActionView View(ExternalActionRow r, string? result)
    {
        using var doc = JsonDocument.Parse(r.Parameters);
        JsonElement? parsed = null;
        if (result is not null)
        {
            try
            {
                using var output = JsonDocument.Parse(result);
                parsed = output.RootElement.Clone();
            }
            catch (JsonException)
            {
                parsed = JsonSerializer.SerializeToElement(result);
            }
        }
        // A pending action past its time shows as expired even before the clean-up marks it.
        var status = r.Status == ExternalActionStatuses.Pending && r.ExpiresAt <= clock.UtcNow ? ExternalActionStatuses.Expired : r.Status;
        var reason = r.Reason?.Split('|')[0] is { Length: > 0 } key ? key : null;
        return new ExternalActionView(r.Id, r.AppName, r.ClientId, r.Tool, tools.Find(r.Tool)?.Definition.SideEffect ?? ToolSideEffects.WriteSelf,
            doc.RootElement.Clone(), r.Hash, status, reason, r.CreatedAt, r.ExpiresAt, r.DecidedAt, parsed);
    }

    /// <summary>
    /// Confirms or rejects a pending action. Confirming needs the hash on the card, so the decision covers exactly what
    /// the employee saw. A decided or expired action is refused with 409; the card then shows how it ended.
    /// </summary>
    public async Task<ExternalActionView> DecideAsync(Guid id, ExternalActionDecision decision, CancellationToken ct)
    {
        if (decision.Confirm is not { } confirm)
        {
            throw AppException.Validation("confirm", "validation.agent_run.confirm");
        }
        var row = await FindAsync(id, ct, forUpdate: true);
        var now = clock.UtcNow;
        if (row.Status != ExternalActionStatuses.Pending)
        {
            throw AppException.Conflict("conflict.agent.external_action_closed");
        }
        if (row.ExpiresAt <= now)
        {
            await session.ExecAsync("update agents.external_action set status = 'expired', decided_at = expires_at where id = @id", new { id }, ct);
            await notifications.ResolveAsync(NotificationCategories.ApprovalWaiting, Link(id), ct);
            await session.CommitAsync(ct);
            throw AppException.Conflict("conflict.agent.external_action_closed");
        }
        if (confirm && !string.Equals(decision.Hash, row.Hash, StringComparison.Ordinal))
        {
            throw AppException.Conflict("conflict.agent.external_action_changed");
        }
        if (!confirm)
        {
            await session.ExecAsync("update agents.external_action set status = 'rejected', decided_at = @now where id = @id", new { id, now }, ct);
        }
        else
        {
            var outcome = await gateway.ExecuteConfirmedAsync(row, ct);
            await session.ExecAsync(
                """
                update agents.external_action set status = @status, reason = @reason, result = @result, classification = @classification,
                  sources = @sources::jsonb, decided_at = @now where id = @id
                """, new
                {
                    id, now, status = outcome.Ok ? ExternalActionStatuses.Confirmed : ExternalActionStatuses.Failed, reason = outcome.ErrorKey,
                    result = outcome.Ok ? outcome.Content : null, classification = outcome.Ok ? outcome.Classification : null,
                    sources = outcome.Ok ? JsonSerializer.Serialize(outcome.Sources, ToolArgs.Options) : null,
                }, ct);
        }
        await notifications.ResolveAsync(NotificationCategories.ApprovalWaiting, Link(id), ct);
        return await GetAsync(id, ct);
    }
}

/// <summary>Pending external AI client actions in the employee's task inbox (spec 016), each opening its card.</summary>
public sealed class ExternalActionTasks(IDbSession session, IEntitlementService entitlements, IClock clock) : IPersonalTaskSource
{
    public const string TaskSource = "external_action";

    public async Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.ApiMcpWriteTools, ct))
        {
            return [];
        }
        var rows = await session.QueryAsync(
            "select id, app_name, created_at, expires_at from agents.external_action where user_id = @userId and status = 'pending' and expires_at > @now order by created_at limit 50",
            new { userId, now = clock.UtcNow }, r => (Id: r.GetGuid(0), App: r.GetString(1), At: r.GetFieldValue<DateTimeOffset>(2), Expires: r.GetFieldValue<DateTimeOffset>(3)), ct);
        return rows.Select(r => new PersonalTask(TaskSource, r.Id, new Dictionary<string, string> { ["en"] = r.App, ["ar"] = r.App }, ExternalActions.Link(r.Id),
            r.At, r.Expires)).ToList();
    }
}
