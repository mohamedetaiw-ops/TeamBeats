using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Search;

/// <summary>A federated source and whether the tenant searches it.</summary>
public sealed record FederatedSourceView(string Source, bool Enabled, DateTimeOffset? UpdatedAt);

public sealed record FederatedSourceRequest(bool? Enabled);

/// <summary>One federated source's group of results for the person. <paramref name="State"/> adds <c>timeout</c> to <see cref="FederatedAnswer.State"/>.</summary>
public sealed record FederatedGroup(string Source, string State, IReadOnlyList<FederatedHit> Results);

public sealed record FederatedPage(string Query, IReadOnlyList<FederatedGroup> Groups);

/// <summary>
/// Federated search (spec 012 US5, FR-016, FR-017). Search administrators turn each source on or off for the tenant;
/// every source starts off. The results page asks for federated results after its own, and each source answers within
/// 3 seconds or its group says it is not available now, so TeamBeats results never wait. A source that is off is never
/// queried. Guests get no federated results, because they search only inside their spaces (FR-022). Nothing found is
/// stored, and the query text is not logged.
/// </summary>
public sealed class FederatedSearch(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IEnumerable<IFederatedSource> sources, ITenantMemberships memberships, IAuditWriter audit, IClock clock)
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);
    public const int MaxResults = 10;

    private readonly Dictionary<string, IFederatedSource> byKey = sources.ToDictionary(s => s.Key, StringComparer.Ordinal);

    /// <summary>Overridden by tests to prove the time limit without waiting for it.</summary>
    internal TimeSpan TimeLimit { get; set; } = Limit;

    public async Task<FederatedPage> QueryAsync(string? q, CancellationToken ct)
    {
        var query = SearchService.Validate(q, null, null);
        await entitlements.DemandAsync(Capabilities.SearchFederated, ct);
        var user = context.RequireUser();
        if (await memberships.GetAsync(user, ct) is { MembershipType: MembershipTypes.Guest })
        {
            return new FederatedPage(query, []);
        }
        var groups = new List<FederatedGroup>();
        foreach (var key in await EnabledAsync(ct))
        {
            if (!byKey.TryGetValue(key, out var source))
            {
                continue;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeLimit);
            try
            {
                var answer = await source.QueryAsync(query, MaxResults, deadline.Token, ct);
                if (answer.State != "none")
                {
                    groups.Add(new FederatedGroup(key, answer.State, answer.State == "ok" ? answer.Hits.Take(MaxResults).ToList() : []));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                groups.Add(new FederatedGroup(key, "timeout", []));
            }
        }
        return new FederatedPage(query, groups);
    }

    public async Task<IReadOnlyList<FederatedSourceView>> ListAsync(CancellationToken ct)
    {
        await DemandManageAsync(ct);
        var stored = await session.QueryAsync("select source, enabled, updated_at from search.federated_source", null,
            r => (Source: r.GetString(0), Enabled: r.GetBoolean(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        return byKey.Keys.Order(StringComparer.Ordinal)
            .Select(k => stored.FirstOrDefault(s => s.Source == k) is { Source: not null } s ? new FederatedSourceView(k, s.Enabled, s.At) : new FederatedSourceView(k, false, null))
            .ToList();
    }

    public async Task<FederatedSourceView> SetAsync(string source, FederatedSourceRequest request, CancellationToken ct)
    {
        await DemandManageAsync(ct);
        if (!byKey.ContainsKey(source))
        {
            throw AppException.NotFound();
        }
        if (request.Enabled is not { } enabled)
        {
            throw AppException.Validation("enabled", "validation.required");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into search.federated_source (tenant_id, source, enabled, updated_by, updated_at)
            values (platform.current_tenant_id(), @source, @enabled, @by, @now)
            on conflict (tenant_id, source) do update set enabled = @enabled, updated_by = @by, updated_at = @now
            """, new { source, enabled, by = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchFederatedChanged, "search_federated_source", source, Changes: new { enabled }), ct);
        return new FederatedSourceView(source, enabled, now);
    }

    private async Task<IReadOnlyList<string>> EnabledAsync(CancellationToken ct) =>
        await session.QueryAsync("select source from search.federated_source where enabled order by source", null, r => r.GetString(0), ct);

    private async Task DemandManageAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SearchFederated, ct);
        await authz.DemandAsync(Permissions.SearchManage, AuthzTarget.Tenant, ct);
    }
}
