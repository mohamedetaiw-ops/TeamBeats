using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Search;

/// <summary>Whether the tenant shows an AI summary above search results.</summary>
public sealed record AiSummarySetting(bool Enabled, DateTimeOffset? UpdatedAt);

public sealed record AiSummaryRequest(bool? Enabled);

/// <summary>
/// The summary as the results page shows it. <paramref name="State"/> adds <c>off</c> (the tenant or package does not
/// show summaries) to <see cref="SearchSummary.State"/>.
/// </summary>
public sealed record SummaryView(string State, Guid? TurnId, string? Text, IReadOnlyList<SearchSummaryCitation> Citations);

/// <summary>
/// The AI summary above search results (spec 012 US6, FR-018): off by default, turned on by search administrators where
/// the package has <c>search.ai_summary</c>. The results page asks for it after the results, so they never wait for a
/// model. The summary itself comes from the assistant (014) through the AI Gateway (013), from passages retrieved as
/// the person, and always cites them. Guests get none, because the assistant is not theirs.
/// </summary>
public sealed class SearchSummariesService(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    ISearchSummaries summaries, ITenantMemberships memberships, IAuditWriter audit, IClock clock)
{
    public async Task<SummaryView> SummaryAsync(string? q, CancellationToken ct)
    {
        var query = SearchService.Validate(q, null, null);
        var user = context.RequireUser();
        if (!await entitlements.IsEnabledAsync(Capabilities.SearchAiSummary, ct) || !await EnabledAsync(ct)
            || await memberships.GetAsync(user, ct) is { MembershipType: MembershipTypes.Guest })
        {
            return new SummaryView("off", null, null, []);
        }
        var summary = await summaries.SummarizeAsync(query, ct);
        return new SummaryView(summary.State, summary.TurnId, summary.Text, summary.Citations);
    }

    public async Task<AiSummarySetting> GetAsync(CancellationToken ct)
    {
        await DemandManageAsync(ct);
        var rows = await session.QueryAsync("select ai_summary, updated_at from search.setting", null,
            r => new AiSummarySetting(r.GetBoolean(0), r.GetFieldValue<DateTimeOffset>(1)), ct);
        return rows.Count > 0 ? rows[0] : new AiSummarySetting(false, null);
    }

    public async Task<AiSummarySetting> SetAsync(AiSummaryRequest request, CancellationToken ct)
    {
        await DemandManageAsync(ct);
        if (request.Enabled is not { } enabled)
        {
            throw AppException.Validation("enabled", "validation.required");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into search.setting (tenant_id, ai_summary, updated_by, updated_at) values (platform.current_tenant_id(), @enabled, @by, @now)
            on conflict (tenant_id) do update set ai_summary = @enabled, updated_by = @by, updated_at = @now
            """, new { enabled, by = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchAiSummaryChanged, "search_setting", "ai_summary", Changes: new { enabled }), ct);
        return new AiSummarySetting(enabled, now);
    }

    private async Task<bool> EnabledAsync(CancellationToken ct) =>
        await session.ScalarAsync<bool?>("select ai_summary from search.setting", null, ct) == true;

    private async Task DemandManageAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SearchAiSummary, ct);
        await authz.DemandAsync(Permissions.SearchManage, AuthzTarget.Tenant, ct);
    }
}
