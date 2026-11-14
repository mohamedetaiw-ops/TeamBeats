using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

public static class BudgetScopes
{
    public const string Tenant = "tenant";
    public const string OrgUnit = "org_unit";
    public const string Agent = "agent";
    public const string User = "user";

    /// <summary>One limit that applies to every person on their own (US5 scenario 3).</summary>
    public const string EachUser = "each_user";

    public static readonly IReadOnlyList<string> All = [Tenant, OrgUnit, Agent, User, EachUser];

    /// <summary>The order a downgrade model is picked in when several budgets downgrade: the narrowest scope wins.</summary>
    internal static int Narrowness(string scope) => scope switch
    {
        User or EachUser => 0,
        Agent => 1,
        OrgUnit => 2,
        _ => 3,
    };
}

public static class BudgetActions
{
    public const string Block = "block";
    public const string Downgrade = "downgrade";
    public const string Alert = "alert";

    public static readonly IReadOnlyList<string> All = [Block, Downgrade, Alert];
}

public static class BudgetPeriods
{
    public const string Month = "month";
    public const string Day = "day";
}

public sealed record BudgetRequest(string? Scope, Guid? ScopeId, string? Period, decimal? Amount, int? Requests, string? Action, string? DowngradeModel, Guid? OwnerId,
    int? Version = null);

/// <summary>
/// A budget with what was used in the current period: USD for an amount budget, requests for a request budget. A per
/// person budget shows no figure, since each person has their own.
/// </summary>
public sealed record BudgetView(Guid Id, string Scope, Guid? ScopeId, string Period, decimal? Amount, int? Requests, string Action, string? DowngradeModel,
    Guid? OwnerId, int Version, decimal? Used, DateTimeOffset PeriodStart, DateTimeOffset ResetsAt, DateTimeOffset UpdatedAt);

/// <summary>The budgets with <paramref name="Labels"/>: unit names and people's display names by ID, so the page never shows bare IDs.</summary>
public sealed record BudgetsView(bool Editable, bool ScopedAllowed, string? ReadOnlyReason, IReadOnlyList<BudgetView> Budgets,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Labels);

internal sealed record Budget(Guid Id, string Scope, Guid? ScopeId, string Period, decimal? Amount, int? Requests, string Action, string? DowngradeModel,
    Guid? OwnerId, int Version, DateTimeOffset UpdatedAt)
{
    public DateTimeOffset Start(DateTimeOffset now) => Period == BudgetPeriods.Month
        ? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
        : new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset End(DateTimeOffset now) => Period == BudgetPeriods.Month ? Start(now).AddMonths(1) : Start(now).AddDays(1);

    public bool Counted => Requests is not null;

    public decimal Limit => Requests ?? Amount ?? 0;

    /// <summary>The subject a counter or alert is kept for: the person for a per person budget, else the zero UUID.</summary>
    public Guid SubjectOf(Guid? user) => Scope == BudgetScopes.EachUser ? user ?? Guid.Empty : Guid.Empty;

    public static Budget Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.Get<Guid?>("scope_id"), r.GetString(3), r.Get<decimal?>("amount"),
        r.Get<int?>("requests"), r.GetString(6), r.Get<string?>("downgrade_model"), r.Get<Guid?>("owner_id"), r.GetInt32(9), r.GetFieldValue<DateTimeOffset>(10));

    public const string Columns = "id, scope, scope_id, period, amount, requests, action, downgrade_model, owner_id, version, updated_at";
}

/// <summary>Who a call is charged to for budgets: the acting unit (resolved on the server), the agent and the person.</summary>
internal sealed record BudgetSubject(Guid? Unit, Guid? Agent, Guid? User);

internal sealed record Reservation(Guid Budget, Guid Subject, DateTimeOffset PeriodStart);

/// <summary>What step 2 decided: a denial with its reset time, a model to downgrade to, and the requests it counted.</summary>
internal sealed record BudgetDecision(string? Denial, DateTimeOffset? ResetsAt, string? Downgrade, IReadOnlyList<Reservation> Reserved, IReadOnlyList<Budget> Matched,
    BudgetSubject Subject)
{
    public static BudgetDecision None(BudgetSubject subject) => new(null, null, null, [], [], subject);
}

/// <summary>
/// AI budgets (spec 013 US5, FR-012): set per tenant (<c>ai.budgets</c>) and per org unit, agent and person
/// (<c>ai.budgets.scoped</c>) by the AI Policy Administrator. Budgets keep applying if the package later loses a key,
/// since they only ever restrict.
/// </summary>
public sealed class AiBudgets(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    Modules.Identity.Contracts.IUserDirectory users,
    ModelPolicies policies,
    ModelCatalogue catalogue,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxBudgets = 200;
    public const decimal MaxAmount = 10_000_000m;
    public const int MaxRequests = 100_000_000;

    private async Task<bool> CanManageAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed;

    private async Task DemandViewAsync(CancellationToken ct)
    {
        if (!await CanManageAsync(ct) && !(await authz.CheckAsync(Permissions.AiPolicyView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
    }

    public async Task<BudgetsView> ListAsync(CancellationToken ct)
    {
        await DemandViewAsync(ct);
        var now = clock.UtcNow;
        var budgets = await session.QueryAsync($"select {Budget.Columns} from ai.budget order by created_at, id", null, Budget.Read, ct);
        var views = new List<BudgetView>();
        foreach (var b in budgets)
        {
            views.Add(await ViewAsync(b, now, ct));
        }
        var manage = await CanManageAsync(ct);
        var enabled = await entitlements.IsEnabledAsync(Capabilities.AiBudgets, ct);
        var labels = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var units = budgets.Where(b => b.Scope == BudgetScopes.OrgUnit && b.ScopeId is not null).Select(b => b.ScopeId!.Value).Distinct().ToList();
        if (units.Count > 0)
        {
            foreach (var (id, names) in await hierarchy.GetNamesAsync(units, ct))
            {
                labels[id.ToString()] = names;
            }
        }
        var people = budgets.Where(b => b.Scope == BudgetScopes.User && b.ScopeId is not null).Select(b => b.ScopeId!.Value)
            .Concat(budgets.Where(b => b.OwnerId is not null).Select(b => b.OwnerId!.Value)).Distinct();
        foreach (var id in people)
        {
            if (await users.GetAsync(id, ct) is { } user)
            {
                labels[id.ToString()] = new Dictionary<string, string> { ["default"] = user.DisplayName };
            }
        }
        return new BudgetsView(manage && enabled, await entitlements.IsEnabledAsync(Capabilities.AiBudgetsScoped, ct),
            !manage ? "ai.budgets.read_only_permission" : !enabled ? "ai.budgets.read_only_package" : null, views, labels);
    }

    private async Task<BudgetView> ViewAsync(Budget b, DateTimeOffset now, CancellationToken ct)
    {
        decimal? used = null;
        if (b.Scope != BudgetScopes.EachUser)
        {
            used = b.Counted
                ? await session.ScalarAsync<int?>("select requests from ai.budget_use where budget_id = @id and subject = @subject and period_start = @start",
                    new { id = b.Id, subject = Guid.Empty, start = b.Start(now) }, ct) ?? 0
                : await BudgetGuard.SpentAsync(session, hierarchy, b, null, now, ct);
        }
        return new BudgetView(b.Id, b.Scope, b.ScopeId, b.Period, b.Amount, b.Requests, b.Action, b.DowngradeModel, b.OwnerId, b.Version, used, b.Start(now),
            b.End(now), b.UpdatedAt);
    }

    private async Task DemandManageAsync(string scope, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.AiBudgets, ct);
        if (scope != BudgetScopes.Tenant)
        {
            await entitlements.DemandAsync(Capabilities.AiBudgetsScoped, ct);
        }
    }

    private async Task<BudgetRequest> ValidateAsync(BudgetRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var scope = request.Scope ?? string.Empty;
        if (!BudgetScopes.All.Contains(scope))
        {
            errors.Add("scope", "validation.ai.budget_scope");
        }
        var needsId = scope is BudgetScopes.OrgUnit or BudgetScopes.Agent or BudgetScopes.User;
        if (needsId != (request.ScopeId is not null))
        {
            errors.Add("scopeId", "validation.ai.budget_scope_id");
        }
        else if (scope == BudgetScopes.OrgUnit && !await hierarchy.UnitExistsAsync(request.ScopeId!.Value, ct))
        {
            errors.Add("scopeId", "validation.ai.budget_scope_id");
        }
        if (request.Period is not (BudgetPeriods.Month or BudgetPeriods.Day))
        {
            errors.Add("period", "validation.ai.budget_period");
        }
        if ((request.Amount is null) == (request.Requests is null) || request.Amount is <= 0 or > MaxAmount || request.Requests is <= 0 or > MaxRequests)
        {
            errors.Add("limit", "validation.ai.budget_limit");
        }
        if (request.Action is null || !BudgetActions.All.Contains(request.Action))
        {
            errors.Add("action", "validation.ai.budget_action");
        }
        else if (request.Action == BudgetActions.Downgrade)
        {
            // The cheaper model must be one the tenant allows, in the catalogue, and able to chat.
            var policy = (await policies.EffectiveAsync(ct)).Document;
            var snapshot = await catalogue.SnapshotAsync(ct);
            if (request.DowngradeModel is not { } model || !policy.Allowlist.Contains(model) || !snapshot.Models.TryGetValue(model, out var entry) || !entry.Active ||
                !entry.Can(ModelCapabilities.Chat))
            {
                errors.Add("downgradeModel", "validation.ai.budget_downgrade");
            }
        }
        else if (request.DowngradeModel is not null)
        {
            errors.Add("downgradeModel", "validation.ai.budget_downgrade");
        }
        errors.ThrowIfAny();
        return request with { DowngradeModel = request.Action == BudgetActions.Downgrade ? request.DowngradeModel : null };
    }

    private Task<bool> ExistsAsync(BudgetRequest r, Guid? except, CancellationToken ct) => session.ScalarAsync<bool>(
        """
        select exists (select 1 from ai.budget where scope = @scope and scope_id is not distinct from @scopeId and period = @period
          and (amount is null) = @counted and id is distinct from @except)
        """, new { scope = r.Scope, scopeId = r.ScopeId, period = r.Period, counted = r.Requests is not null, except }, ct);

    public async Task<BudgetView> CreateAsync(BudgetRequest request, CancellationToken ct)
    {
        await DemandManageAsync(request.Scope ?? BudgetScopes.Tenant, ct);
        var r = await ValidateAsync(request, ct);
        if (await session.ScalarAsync<long>("select count(*) from ai.budget", null, ct) >= MaxBudgets)
        {
            throw AppException.Conflict("conflict.ai.budget_limit");
        }
        if (await ExistsAsync(r, null, ct))
        {
            throw AppException.Conflict("conflict.ai.budget_exists");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into ai.budget (tenant_id, id, scope, scope_id, period, amount, requests, action, downgrade_model, owner_id, version, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @scope, @scopeId, @period, @amount, @requests, @action, @downgrade, @owner, 1, @user, @now, @now)
            """,
            new
            {
                id, scope = r.Scope, scopeId = r.ScopeId, period = r.Period, amount = r.Amount, requests = r.Requests, action = r.Action, downgrade = r.DowngradeModel,
                owner = r.OwnerId, user = context.RequireUser(), now,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiBudgetChanged, "ai_budget", id.ToString(), TargetOrgUnitId: r.Scope == BudgetScopes.OrgUnit ? r.ScopeId : null,
            Changes: new { change = "created", r.Scope, r.ScopeId, r.Period, r.Amount, r.Requests, r.Action, r.DowngradeModel, r.OwnerId }), ct);
        return await GetAsync(id, ct);
    }

    private async Task<Budget> LoadAsync(Guid id, CancellationToken ct, bool forUpdate = false)
    {
        var rows = await session.QueryAsync($"select {Budget.Columns} from ai.budget where id = @id{(forUpdate ? " for update" : string.Empty)}", new { id }, Budget.Read, ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }

    public async Task<BudgetView> GetAsync(Guid id, CancellationToken ct)
    {
        await DemandViewAsync(ct);
        return await ViewAsync(await LoadAsync(id, ct), clock.UtcNow, ct);
    }

    /// <summary>Changes the limit, action or owner. The scope and period of a budget never change; make a new one instead.</summary>
    public async Task<BudgetView> UpdateAsync(Guid id, BudgetRequest request, CancellationToken ct)
    {
        var current = await LoadAsync(id, ct, forUpdate: true);
        await DemandManageAsync(current.Scope, ct);
        if (request.Version != current.Version)
        {
            throw AppException.Conflict("conflict.ai.budget_changed");
        }
        var r = await ValidateAsync(request with { Scope = current.Scope, ScopeId = current.ScopeId, Period = current.Period }, ct);
        if (await ExistsAsync(r, id, ct))
        {
            throw AppException.Conflict("conflict.ai.budget_exists");
        }
        await session.ExecAsync(
            """
            update ai.budget set amount = @amount, requests = @requests, action = @action, downgrade_model = @downgrade, owner_id = @owner,
              version = version + 1, updated_at = @now where id = @id
            """, new { id, amount = r.Amount, requests = r.Requests, action = r.Action, downgrade = r.DowngradeModel, owner = r.OwnerId, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiBudgetChanged, "ai_budget", id.ToString(),
            TargetOrgUnitId: current.Scope == BudgetScopes.OrgUnit ? current.ScopeId : null,
            Changes: new
            {
                change = "updated", before = new { current.Amount, current.Requests, current.Action, current.DowngradeModel, current.OwnerId },
                after = new { r.Amount, r.Requests, r.Action, r.DowngradeModel, r.OwnerId },
            }), ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var current = await LoadAsync(id, ct, forUpdate: true);
        await DemandManageAsync(BudgetScopes.Tenant, ct);
        await session.ExecAsync("delete from ai.budget_use where budget_id = @id", new { id }, ct);
        await session.ExecAsync("delete from ai.budget_alert where budget_id = @id", new { id }, ct);
        await session.ExecAsync("delete from ai.budget where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiBudgetChanged, "ai_budget", id.ToString(),
            TargetOrgUnitId: current.Scope == BudgetScopes.OrgUnit ? current.ScopeId : null,
            Changes: new { change = "deleted", current.Scope, current.ScopeId, current.Period, current.Amount, current.Requests, current.Action }), ct);
    }
}

/// <summary>
/// Step 2 of the gateway pipeline (FR-002) and the threshold alerts of US5 scenario 2. Request budgets are counted as a
/// call is planned, in a transaction of their own, so concurrent calls cannot all take the last request and a caller's
/// rollback does not hand it back; a call that is then denied or fails gives it back. Amount budgets sum the cost of
/// served calls charged to the tenant, so concurrent calls can overshoot by the cost of the calls already running, far
/// inside the spec 005 tolerance of 1% or 10 units.
/// </summary>
public sealed class BudgetGuard(IServiceScopeFactory scopes, ITenantContext context, IDbSession session, IOrganizationHierarchy hierarchy, IClock clock,
    ILogger<BudgetGuard> logger)
{
    public const string Link = "/admin/ai/budgets";
    public const string Template = "ai.budget_reached";
    public static readonly int[] Thresholds = [80, 100];

    internal static async Task<decimal> SpentAsync(IDbSession db, IOrganizationHierarchy hierarchy, Budget b, Guid? user, DateTimeOffset now, CancellationToken ct)
    {
        var filter = b.Scope switch
        {
            BudgetScopes.OrgUnit => " and unit_id = any(@units)",
            BudgetScopes.Agent => " and agent_id = @scopeId",
            BudgetScopes.User => " and user_id = @scopeId",
            BudgetScopes.EachUser => " and user_id = @user",
            _ => string.Empty,
        };
        var units = b.Scope == BudgetScopes.OrgUnit && b.ScopeId is { } unit ? (await hierarchy.GetSubtreesAsync([unit], ct)).ToArray() : [];
        return await db.ScalarAsync<decimal>(
            $"select coalesce(sum(cost), 0) from ai.usage_record where at >= @start and decision = 'served' and charged_to = 'tenant'{filter}",
            new { start = b.Start(now), units, scopeId = b.ScopeId, user }, ct);
    }

    private async Task<IReadOnlyList<Budget>> MatchingAsync(BudgetSubject s, CancellationToken ct)
    {
        var units = s.Unit is { } unit ? (await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).ToArray() : [];
        return await session.QueryAsync(
            $"""
            select {Budget.Columns} from ai.budget
            where scope = 'tenant' or (scope = 'org_unit' and scope_id = any(@units)) or (scope = 'agent' and scope_id = @agent)
               or (scope = 'user' and scope_id = @user) or (scope = 'each_user' and @user::uuid is not null)
            """, new { units, agent = s.Agent, user = s.User }, Budget.Read, ct);
    }

    /// <summary>Runs <paramref name="work"/> in a tenant transaction of its own, committed at once.</summary>
    private async Task<T> OwnAsync<T>(Func<IDbSession, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        inner.SetTenant(context.RequireTenant());
        inner.SetActor(context.ActorType, context.ActorId);
        var db = scope.ServiceProvider.GetRequiredService<IDbSession>();
        var result = await work(db);
        await db.CommitAsync(CancellationToken.None);
        return result;
    }

    internal async Task<BudgetDecision> CheckAsync(BudgetSubject subject, CancellationToken ct)
    {
        var budgets = await MatchingAsync(subject, ct);
        if (budgets.Count == 0)
        {
            return BudgetDecision.None(subject);
        }
        var now = clock.UtcNow;
        var reserved = new List<Reservation>();
        var exhausted = new List<Budget>();
        var counted = budgets.Where(b => b.Counted).ToList();
        if (counted.Count > 0)
        {
            var counts = await OwnAsync(async db =>
            {
                var result = new List<(Budget Budget, Reservation Reservation, int Count)>();
                foreach (var b in counted)
                {
                    var r = new Reservation(b.Id, b.SubjectOf(subject.User), b.Start(now));
                    var count = await db.ScalarAsync<int>(
                        """
                        insert into ai.budget_use (tenant_id, budget_id, subject, period_start, requests) values (platform.current_tenant_id(), @budget, @subject, @start, 1)
                        on conflict (tenant_id, budget_id, subject, period_start) do update set requests = ai.budget_use.requests + 1
                        returning requests
                        """, new { budget = r.Budget, subject = r.Subject, start = r.PeriodStart }, CancellationToken.None);
                    result.Add((b, r, count));
                }
                return result;
            });
            foreach (var (b, r, count) in counts)
            {
                reserved.Add(r);
                if (count > b.Requests)
                {
                    exhausted.Add(b);
                }
            }
        }
        foreach (var b in budgets.Where(b => !b.Counted))
        {
            if (await SpentAsync(session, hierarchy, b, subject.User, now, ct) >= b.Amount)
            {
                exhausted.Add(b);
            }
        }
        var blocking = exhausted.Where(b => b.Action == BudgetActions.Block).ToList();
        if (blocking.Count > 0)
        {
            await ReleaseAsync(reserved);
            var denial = blocking.Any(b => !b.Counted) ? AiDenials.BudgetExhausted : AiDenials.QuotaExhausted;
            return new BudgetDecision(denial, blocking.Max(b => b.End(now)), null, [], budgets, subject);
        }
        var downgrade = exhausted.Where(b => b.Action == BudgetActions.Downgrade).OrderBy(b => BudgetScopes.Narrowness(b.Scope)).FirstOrDefault();
        return new BudgetDecision(null, null, downgrade?.DowngradeModel, reserved, budgets, subject);
    }

    /// <summary>Gives back the requests a call counted when it ends without an answer.</summary>
    internal async Task ReleaseAsync(IReadOnlyList<Reservation> reserved)
    {
        if (reserved.Count == 0)
        {
            return;
        }
        try
        {
            await OwnAsync(async db =>
            {
                foreach (var r in reserved)
                {
                    await db.ExecAsync(
                        "update ai.budget_use set requests = greatest(requests - 1, 0) where budget_id = @budget and subject = @subject and period_start = @start",
                        new { budget = r.Budget, subject = r.Subject, start = r.PeriodStart }, CancellationToken.None);
                }
                return true;
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI budget requests could not be given back");
        }
    }

    /// <summary>
    /// After a served call: sends each 80% and 100% alert once per budget, subject and period, to the AI Policy
    /// Administrators and the budget owner (US5 scenario 2). A per person budget alerts only its owner, so one person's
    /// limit does not notify every administrator. A failure is logged and never fails the call.
    /// </summary>
    internal async Task AlertAsync(BudgetDecision decision)
    {
        if (decision.Matched.Count == 0)
        {
            return;
        }
        try
        {
            var now = clock.UtcNow;
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var inner = sp.GetRequiredService<TenantContextAccessor>();
            inner.SetTenant(context.RequireTenant());
            inner.SetActor(ActorType.System, null);
            var db = sp.GetRequiredService<IDbSession>();
            IReadOnlyList<Guid>? admins = null;
            foreach (var b in decision.Matched)
            {
                var subject = b.SubjectOf(decision.Subject.User);
                var used = b.Counted
                    ? await db.ScalarAsync<int?>("select requests from ai.budget_use where budget_id = @id and subject = @subject and period_start = @start",
                        new { id = b.Id, subject, start = b.Start(now) }, CancellationToken.None) ?? 0
                    : await SpentAsync(db, sp.GetRequiredService<IOrganizationHierarchy>(), b, decision.Subject.User, now, CancellationToken.None);
                foreach (var threshold in Thresholds.Where(t => used * 100 >= b.Limit * t))
                {
                    var fresh = await db.ScalarAsync<long>(
                        """
                        with added as (insert into ai.budget_alert (tenant_id, budget_id, subject, period_start, threshold, at)
                          values (platform.current_tenant_id(), @id, @subject, @start, @threshold, @now) on conflict do nothing returning 1)
                        select count(*) from added
                        """, new { id = b.Id, subject, start = b.Start(now), threshold, now }, CancellationToken.None);
                    if (fresh == 0)
                    {
                        continue;
                    }
                    var recipients = new HashSet<Guid>();
                    if (b.Scope != BudgetScopes.EachUser)
                    {
                        admins ??= await sp.GetRequiredService<IAuthorizationProvisioning>().TenantHoldersAsync(Permissions.AiPolicyManage, CancellationToken.None);
                        recipients.UnionWith(admins);
                    }
                    if (b.OwnerId is { } owner)
                    {
                        recipients.Add(owner);
                    }
                    await sp.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry(AuditActions.AiBudgetReached, "ai_budget", b.Id.ToString(),
                        TargetOrgUnitId: b.Scope == BudgetScopes.OrgUnit ? b.ScopeId : null,
                        Changes: new { b.Scope, b.ScopeId, subject = subject == Guid.Empty ? (Guid?)null : subject, threshold, b.Action, limit = b.Limit, used }),
                        CancellationToken.None);
                    await sp.GetRequiredService<IOutbox>().EnqueueAsync(new AiBudgetThresholdReached(b.Id, b.Scope, b.ScopeId,
                        subject == Guid.Empty ? null : subject, threshold, b.Action, b.Start(now)), CancellationToken.None);
                    if (recipients.Count > 0)
                    {
                        await sp.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest(recipients, NotificationCategories.AiBudgets,
                            $"{Template}_{threshold}", Link, new Dictionary<string, string> { ["threshold"] = threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            DedupeKey: $"ai_budget:{b.Id:N}:{subject:N}:{b.Start(now):yyyyMMdd}:{threshold}"), CancellationToken.None);
                    }
                }
            }
            await db.CommitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI budget alerts could not be checked");
        }
    }
}
