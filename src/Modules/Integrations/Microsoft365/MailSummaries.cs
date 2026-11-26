using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>Whether the tenant offers mail summaries by the assistant and whether the employee said yes (spec 018 R3, FR-022).</summary>
public sealed record MailSummaryStatus(bool Offered, bool OptedIn);

public sealed record MailSummaryChoice(bool? OptedIn);

/// <summary>
/// Mail summaries by the assistant (spec 018 US6 scenario 4, FR-022): offered only while the tenant's Mail feature has
/// its <c>assistantSummaries</c> switch on, and used only after the employee's own opt-in. Each choice is audited.
/// </summary>
public sealed class MailSummaryConsent(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAuditWriter audit)
{
    public async Task<bool> OfferedAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationMicrosoft365, ct))
        {
            return false;
        }
        var options = await session.QueryAsync(
            """
            select f.options::text from integrations.connection c join integrations.connection_feature f on f.connection_id = c.id
            where c.kind = @k and c.enabled and c.status <> 'disabled' and f.enabled and f.feature = @f
            order by c.created_at limit 1
            """, new { k = ConnectionKinds.Microsoft365, f = Microsoft365Features.Mail }, r => r.GetString(0), ct);
        return options.Count == 1 && FeatureOptions.FromStored(options[0]).AssistantSummaries;
    }

    public async Task<bool> OptedInAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select true from integrations.mail_summary_optin where user_id = @u",
            new { u = context.RequireUser() }, r => r.GetBoolean(0), ct);

    public async Task<MailSummaryStatus> StatusAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
        return new MailSummaryStatus(await OfferedAsync(ct), await OptedInAsync(ct));
    }

    public async Task<MailSummaryStatus> SetAsync(MailSummaryChoice choice, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.IntegrationMicrosoft365, ct);
        if (choice.OptedIn is not { } yes)
        {
            throw AppException.Validation("optedIn", "validation.integrations.mail_summaries_choice");
        }
        var offered = await OfferedAsync(ct);
        if (yes && !offered)
        {
            // Saying yes to something the tenant does not offer would turn it on later without asking again.
            throw AppException.Conflict("conflict.integrations.mail_summaries_not_offered");
        }
        var user = context.RequireUser();
        var changed = yes
            ? await session.ExecAsync(
                "insert into integrations.mail_summary_optin (tenant_id, user_id) values (@t, @u) on conflict do nothing",
                new { t = context.RequireTenant(), u = user }, ct)
            : await session.ExecAsync("delete from integrations.mail_summary_optin where user_id = @u", new { u = user }, ct);
        if (changed > 0)
        {
            await audit.WriteAsync(new AuditEntry(yes ? AuditActions.MailSummariesOptedIn : AuditActions.MailSummariesOptedOut, "user", user.ToString()), ct);
        }
        return new MailSummaryStatus(offered, yes);
    }
}

/// <summary>A disabled membership loses its opt-in with its tokens (spec 018 FR-005).</summary>
public sealed class DropMailSummaryOptInOnUserDeprovisioned(IDbSession session) : TeamBeats.Messaging.IMessageHandler<UserDeprovisioned>
{
    public Task HandleAsync(UserDeprovisioned message, CancellationToken ct) =>
        session.ExecAsync("delete from integrations.mail_summary_optin where user_id = @u", new { u = message.UserId }, ct);
}

/// <summary>
/// The mail read tool for agents (spec 018 US6 scenario 4, FR-022): the latest messages of the employee's own Inbox, read
/// as the employee, with sender, subject, time, flags and the plain-text preview, so the assistant can summarise them.
/// Only while the tenant switch is on and the employee opted in. Message text is written by others, so the runtime passes
/// it to the model inside the untrusted envelope; the result is Confidential, so spec 013 provider rules apply.
/// </summary>
public sealed class MailInboxTool(MailSummaryConsent consent, DelegatedAccessService access, IMicrosoftGraph graph, IEntitlementService entitlements) : IAgentTool
{
    public const string Name = "mail.inbox";
    public const int MaxMessages = 25;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ToolDefinition Definition { get; } = new(
        Name, "integrations", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        Schema("""{"type":"object","additionalProperties":false,"properties":{"top":{"type":"integer","minimum":1,"maximum":25},"unreadOnly":{"type":"boolean"}}}"""),
        "confidential", Idempotent: true, TimeoutSeconds: 15,
        "List the latest messages in the employee's own Outlook Inbox (default 10, at most 25; optionally unread only), newest first, with sender, subject, time received, whether read, importance, attachments and a short plain-text preview, to summarise them.",
        Capabilities.IntegrationMicrosoft365);

    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationMicrosoft365, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        var top = 10;
        var unreadOnly = false;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("top", out var t) && (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out top) || top is < 1 or > MaxMessages))
            {
                return ToolResult.Failed("tool.invalid_arguments");
            }
            if (arguments.TryGetProperty("unreadOnly", out var u))
            {
                if (u.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return ToolResult.Failed("tool.invalid_arguments");
                }
                unreadOnly = u.GetBoolean();
            }
        }
        if (!await consent.OfferedAsync(ct))
        {
            return ToolResult.Failed("tool.mail_summaries_off");
        }
        if (!await consent.OptedInAsync(ct))
        {
            return ToolResult.Failed("tool.mail_summaries_not_opted_in");
        }
        var grant = await access.AcquireAsync(Microsoft365Features.Mail, ct);
        if (grant.State != M365States.Ok)
        {
            return ToolResult.Failed("tool.mail_" + grant.State);
        }
        if (!grant.Scopes.Contains("Mail.Read"))
        {
            // The token predates the switch: connecting Outlook again asks for Mail.Read.
            return ToolResult.Failed("tool.mail_reconnect");
        }
        JsonElement list;
        try
        {
            var path = $"me/mailFolders/inbox/messages?$top={top}&$select=id,subject,from,receivedDateTime,isRead,importance,hasAttachments,bodyPreview,webLink"
                + (unreadOnly ? "&$filter=isRead%20eq%20false" : string.Empty) + "&$orderby=receivedDateTime%20desc";
            list = await graph.DelegatedSendAsync(grant.AccessToken!, HttpMethod.Get, path, null, ct);
        }
        catch (GraphCallException ex)
        {
            return ToolResult.Failed(ex.Kind is "unauthorized" ? "tool.mail_reconnect" : ex.Kind is "no_mailbox" ? "tool.mail_no_mailbox" : "tool.mail_unavailable");
        }
        catch (GraphUnavailableException)
        {
            return ToolResult.Failed("tool.mail_unavailable");
        }
        var messages = list.GetProperty("value").EnumerateArray().Select(m => GraphMailbox.Map(m, true))
            .Where(m => !unreadOnly || !m.IsRead)
            .OrderByDescending(m => m.ReceivedAt).Take(top).ToList();
        var rows = messages.Select(m => new
        {
            from = m.Sender,
            subject = m.Subject,
            receivedAt = m.ReceivedAt,
            isRead = m.IsRead,
            importance = m.Importance,
            hasAttachments = m.HasAttachments,
            preview = m.Preview,
        });
        var sources = messages.Where(m => !string.IsNullOrEmpty(m.WebLink))
            .Select(m => new ToolSource("mail_message", CalendarEventsTool.ItemId(m.Id), m.Subject, m.WebLink, "confidential"))
            .ToList();
        return new ToolResult(true, JsonSerializer.Serialize(new { messages = rows }, Json), "confidential", sources);
    }
}
