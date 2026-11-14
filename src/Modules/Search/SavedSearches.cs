using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Search;

public sealed record SavedSearchView(Guid Id, string Query, bool Alerts, DateTimeOffset CreatedAt, DateTimeOffset? AlertedAt);

public sealed record SavedSearchRequest(string? Query, bool? Alerts);

public sealed record SavedSearchAlertsRequest(bool? Alerts);

/// <summary>Asks for one saved search to be checked for new matches, as its person (spec 012 US7).</summary>
public sealed record SavedSearchDue(Guid SavedSearchId) : IIntegrationEvent;

/// <summary>
/// Saved searches with daily alerts (spec 012 US7, FR-019). A person saves up to 25 searches and turns alerts on for any
/// of them. Once a day per saved search, items first indexed since the last check that match it and that the person may
/// read now are counted, and when there are any the person gets one grouped notification through spec 015 that opens
/// the search. Nothing is sent for items the person cannot read, and the item titles are never in the notification.
/// </summary>
public sealed class SavedSearches(IDbSession session, ITenantContext context, IEntitlementService entitlements, IClock clock)
{
    public const int MaxPerPerson = 25;
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public async Task<IReadOnlyList<SavedSearchView>> ListAsync(CancellationToken ct)
    {
        var user = await UserAsync(ct);
        return await session.QueryAsync(
            "select id, query, alerts, created_at, alerted_at from search.saved_search where user_id = @user order by created_at desc", new { user },
            r => new SavedSearchView(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), r.GetFieldValue<DateTimeOffset>(3), r.Get<DateTimeOffset?>("alerted_at")), ct);
    }

    public async Task<SavedSearchView> SaveAsync(SavedSearchRequest request, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var query = SearchService.Validate(request.Query, null, null);
        if (await session.ScalarAsync<int>("select count(*)::int from search.saved_search where user_id = @user", new { user }, ct) >= MaxPerPerson)
        {
            throw AppException.Conflict("conflict.search.saved_limit");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        var alerts = request.Alerts ?? true;
        var saved = await session.ExecAsync(
            """
            insert into search.saved_search (tenant_id, id, user_id, query, alerts, created_at, checked_at)
            values (platform.current_tenant_id(), @id, @user, @query, @alerts, @now, @now)
            on conflict (tenant_id, user_id, query) do nothing
            """, new { id, user, query, alerts, now }, ct);
        if (saved == 0)
        {
            throw AppException.Conflict("conflict.search.saved_exists");
        }
        return new SavedSearchView(id, query, alerts, now, null);
    }

    public async Task<SavedSearchView> SetAlertsAsync(Guid id, SavedSearchAlertsRequest request, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        if (request.Alerts is not { } alerts)
        {
            throw AppException.Validation("alerts", "validation.required");
        }
        // Turning alerts back on starts from now, so the first alert never reaches back over the time they were off.
        var rows = await session.QueryAsync(
            """
            update search.saved_search set alerts = @alerts, checked_at = case when @alerts and not alerts then @now else checked_at end
            where id = @id and user_id = @user
            returning id, query, alerts, created_at, alerted_at
            """, new { id, user, alerts, now = clock.UtcNow },
            r => new SavedSearchView(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), r.GetFieldValue<DateTimeOffset>(3), r.Get<DateTimeOffset?>("alerted_at")), ct);
        return rows.Count > 0 ? rows[0] : throw AppException.NotFound();
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        if (await session.ExecAsync("delete from search.saved_search where id = @id and user_id = @user", new { id, user }, ct) == 0)
        {
            throw AppException.NotFound();
        }
    }

    private async Task<Guid> UserAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SearchSavedAlerts, ct);
        return context.RequireUser();
    }
}

/// <summary>
/// Checks one saved search as its person: the same keyword match and permission filter as the search page, limited to
/// items first indexed since the last check. Runs in a scope of its own acting as the person, so each source decides
/// what they may read; a person who is no longer an active member is skipped and their alerts stop.
/// </summary>
public sealed class SavedSearchChecker(IServiceScopeFactory scopes, ITenantContext context, IClock clock) : IMessageHandler<SavedSearchDue>
{
    public const string Template = "search.saved_alert";
    public const int MaxCandidates = 200;

    public async Task HandleAsync(SavedSearchDue message, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var accessor = sp.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(ActorType.System, null);
        var session = sp.GetRequiredService<IDbSession>();
        var rows = await session.QueryAsync(
            "select user_id, query, checked_at from search.saved_search where id = @id and alerts for update skip locked", new { id = message.SavedSearchId },
            r => (User: r.GetGuid(0), Query: r.GetString(1), Checked: r.GetFieldValue<DateTimeOffset>(2)), ct);
        var now = clock.UtcNow;
        if (rows.Count == 0 || rows[0].Checked > now - SavedSearches.Interval)
        {
            return;
        }
        var (user, query, since) = rows[0];
        var member = await sp.GetRequiredService<ITenantMemberships>().GetAsync(user, ct);
        if (member is not { Status: MembershipStatus.Active } || !await sp.GetRequiredService<IEntitlementService>().IsEnabledAsync(Capabilities.SearchSavedAlerts, ct))
        {
            await session.ExecAsync("update search.saved_search set alerts = false where id = @id", new { id = message.SavedSearchId }, ct);
            await session.CommitAsync(ct);
            return;
        }
        accessor.SetActor(ActorType.User, user);
        var search = sp.GetRequiredService<SearchService>();
        var terms = SearchText.Terms(query);
        var count = 0;
        if (terms.Count > 0)
        {
            var groups = await search.GroupsAsync(terms, SearchText.QueryLanguage(query), ct);
            var matches = await search.KeywordAsync(SearchText.TsQuery(groups, all: true), since, null, MaxCandidates, ct);
            var fresh = await session.QueryAsync(
                "select source, item_id from search.item_state where changed_at > @since and (source, item_id) in (select * from unnest(@sources::text[], @items::uuid[]))",
                new { since, sources = matches.Select(m => m.Source).ToArray(), items = matches.Select(m => m.ItemId).ToArray() },
                r => (Source: r.GetString(0), Item: r.GetGuid(1)), ct);
            var newOnes = matches.Where(m => fresh.Contains((m.Source, m.ItemId))).ToList();
            count = (await search.PermittedAsync(newOnes, ct)).Count;
        }
        accessor.SetActor(ActorType.System, null);
        await session.ExecAsync("update search.saved_search set checked_at = @now, alerted_at = case when @count > 0 then @now else alerted_at end where id = @id",
            new { id = message.SavedSearchId, now, count }, ct);
        if (count > 0)
        {
            await sp.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest([user], NotificationCategories.SearchAlerts, Template,
                $"/search?q={Uri.EscapeDataString(query)}", new Dictionary<string, string> { ["query"] = query, ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                DedupeKey: $"saved_search:{message.SavedSearchId:N}:{now:yyyyMMdd}"), ct);
        }
        await session.CommitAsync(ct);
    }
}

/// <summary>Finds the saved searches due for their daily check in each tenant, every 15 minutes, and asks for each to be checked.</summary>
public sealed class SavedSearchScheduler(IDbSession session, IOutbox outbox, IClock clock)
{
    public const int Batch = 500;

    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var due = await session.QueryAsync(
            "select id from search.saved_search where alerts and checked_at <= @cutoff order by checked_at limit @batch",
            new { cutoff = clock.UtcNow - SavedSearches.Interval, batch = Batch }, r => r.GetGuid(0), ct);
        foreach (var id in due)
        {
            await outbox.EnqueueAsync(new SavedSearchDue(id), ct);
        }
        return due.Count;
    }
}

/// <summary>Search upkeep every 15 minutes per tenant: queues the saved searches due for their check, and prunes the query log past its retention.</summary>
public sealed class SavedSearchWorker(IServiceScopeFactory scopes, ILogger<SavedSearchWorker> logger) : BackgroundService
{
    /// <summary>The development host runs the worker in process when this is set, as staging has no separate workers.</summary>
    public const string InProcessKey = "Search:SavedSearches:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Saved search alerts could not list tenants");
            return;
        }
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                accessor.SetTenant(tenant.Id);
                accessor.SetActor(ActorType.System, null);
                await scope.ServiceProvider.GetRequiredService<SavedSearchScheduler>().RunDueAsync(ct);
                await scope.ServiceProvider.GetRequiredService<SearchQueryLog>().PruneAsync(ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Saved search alerts failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
