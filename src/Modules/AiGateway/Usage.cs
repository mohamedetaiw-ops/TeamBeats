using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>Metadata of one gateway call (US8 scenario 1). Never the prompt or the answer.</summary>
internal sealed record CallRecord(
    string UseCase,
    Guid? UnitId,
    Guid? AgentId,
    int? AgentVersion,
    string? Model,
    string? ModelRequested,
    string? Provider,
    AiUsage Usage,
    int LatencyMs,
    decimal Cost,
    string? Denial,
    DataClassification? Classification,
    bool Fallback,
    bool Partial,
    bool PlatformCharged,
    IReadOnlyList<AiMessage>? Messages = null,
    string? Answer = null);

/// <summary>
/// Writes call records (FR-011) in a transaction of their own as each call ends, so a caller that rolls back, or a
/// process that stops right after, still leaves the record (NFR: no call record lost). A failed write is logged and
/// never fails the call it describes.
/// </summary>
public sealed class UsageRecorder(IServiceScopeFactory scopes, ITenantContext context, DebugLogProtector protector, IClock clock, ILogger<UsageRecorder> logger)
{
    internal async Task RecordAsync(CallRecord call, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
            inner.SetTenant(context.RequireTenant());
            inner.SetActor(context.ActorType, context.ActorId);
            var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
            await session.ExecAsync(
                """
                insert into ai.usage_record (tenant_id, id, at, unit_id, user_id, use_case, agent_id, agent_version, model, model_requested, provider,
                  input_tokens, output_tokens, latency_ms, cost, decision, denial, classification, fallback, partial, charged_to, correlation_id)
                values (platform.current_tenant_id(), @id, @at, @unit, @user, @useCase, @agent, @agentVersion, @model, @requested, @provider,
                  @input, @output, @latency, @cost, @decision, @denial, @classification, @fallback, @partial, @charged, @correlation)
                """,
                new
                {
                    id = Ids.New(), at = clock.UtcNow, unit = call.UnitId, user = context.ActorType == ActorType.User ? context.ActorId : null, useCase = call.UseCase,
                    agent = call.AgentId, agentVersion = call.AgentVersion, model = call.Model, requested = call.ModelRequested, provider = call.Provider,
                    input = call.Usage.InputTokens, output = call.Usage.OutputTokens, latency = call.LatencyMs, cost = call.Cost,
                    decision = call.Denial is null ? "served" : "denied", denial = call.Denial,
                    classification = call.Classification is { } c ? Classifications.Key(c) : null, fallback = call.Fallback, partial = call.Partial,
                    charged = call.PlatformCharged ? "platform" : "tenant", correlation = Correlation.Id,
                },
                CancellationToken.None);
            if (call.Denial is null && call.Messages is not null && call.Classification is { } level && call.Model is not null)
            {
                await DebugWindows.CaptureAsync(session, protector, context.RequireTenant(), clock.UtcNow, call.UseCase, level, call.Model, call.Messages,
                    call.Answer ?? string.Empty, CancellationToken.None);
            }
            await session.CommitAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "AI call record could not be written for use case {UseCase}", call.UseCase);
        }
    }

    /// <summary>Requests served this calendar month (UTC) and charged to the tenant, for the package quota.</summary>
    internal static async Task<long> MonthCountAsync(IDbSession session, DateTimeOffset now, CancellationToken ct)
    {
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return await session.ScalarAsync<long>(
            "select count(*) from ai.usage_record where at >= @start and decision = 'served' and charged_to = 'tenant'", new { start }, ct);
    }

    public static decimal Cost(CatalogueEntry? model, AiUsage usage) => model is null
        ? 0
        : Math.Round((usage.InputTokens * model.InputPrice + usage.OutputTokens * model.OutputPrice) / 1_000_000m, 6);
}

public static class UsageGroups
{
    public const string Day = "day";
    public const string UseCase = "use_case";
    public const string Model = "model";
    public const string Provider = "provider";
    public const string Unit = "unit";
    public const string Agent = "agent";
    public const string User = "user";
    public const string Classification = "classification";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Day, UseCase, Model, Provider, Unit, Agent, User, Classification };
}

public sealed record UsageRow(string? Key, long Requests, long InputTokens, long OutputTokens, decimal Cost, long Users);

public sealed record DeniedRow(string Reason, long Requests);

/// <summary>A monthly USD budget with what it has used so far and where it ends at the current daily rate.</summary>
public sealed record BudgetForecast(Guid Id, string Scope, Guid? ScopeId, decimal Amount, decimal Used, decimal Projected, bool OverBudget);

/// <summary>
/// Month to date spend (UTC calendar month) in the viewer's scope, the daily rate so far and the projected month end at
/// that rate, with cost by provider and by classification (SC-002), and the tenant's monthly USD budgets for viewers
/// who may see them.
/// </summary>
public sealed record UsageForecast(
    DateOnly MonthStart,
    DateOnly MonthEnd,
    int DaysElapsed,
    int DaysInMonth,
    decimal MonthToDate,
    decimal DailyRate,
    decimal Projected,
    IReadOnlyList<UsageRow> ByProvider,
    IReadOnlyList<UsageRow> ByClassification,
    IReadOnlyList<BudgetForecast> Budgets,
    string Scope,
    string Currency,
    decimal Rate);

public sealed record UsageView(
    DateOnly From,
    DateOnly To,
    string GroupBy,
    string Scope,
    UsageRow Totals,
    IReadOnlyList<UsageRow> Rows,
    IReadOnlyList<DeniedRow> Denied,
    int MinimumGroupSize,
    int HiddenGroups,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Labels,
    string Currency = "USD",
    decimal Rate = 1m);

/// <summary>
/// The AI usage view (spec 013 US4, R1 part): requests, tokens and estimated cost by day, use case, model, provider,
/// org unit or agent, and denied calls by reason. Holders of <c>ai.policy_manage</c> or <c>ai.policy_view</c> at tenant
/// scope see the whole tenant and named users; an org-scoped <c>ai.policy_manage</c> holder sees calls made in their
/// subtree only, and groups with fewer than five people are hidden from them (scenario 3). Unit rows carry the unit's
/// names and user rows the person's display name, under <c>Labels</c>, so the page never shows bare ids.
/// R2 adds cost by classification, a CSV export, the month end forecast against budgets and a display currency, and
/// reads the daily aggregates kept after call records pass 25 months.
/// </summary>
public sealed class UsageReports(
    IDbSession session,
    IAuthorizationService authz,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    IClock clock,
    ModelPolicies policies,
    AiBudgets budgets,
    IAuditWriter audit)
{
    public const int MinimumGroupSize = 5;
    public const int MaxDays = 400;

    /// <summary>
    /// Live call records and the daily aggregates of older ones, in one shape. An aggregate row has no person; its
    /// <c>archived_users</c> says how many people it covered.
    /// </summary>
    private const string Source = """
        (select at, unit_id, user_id, use_case, agent_id, model, provider, classification, decision, denial, 1::bigint as requests,
                input_tokens::bigint as input_tokens, output_tokens::bigint as output_tokens, cost, 0 as archived_users
           from ai.usage_record
         union all
         select (day::timestamp at time zone 'UTC'), unit_id, null::uuid, use_case, agent_id, model, provider, classification, decision, denial, requests,
                input_tokens, output_tokens, cost, users
           from ai.usage_daily) u
        """;

    private const string Sums = "coalesce(sum(requests), 0)::bigint, coalesce(sum(input_tokens), 0)::bigint, coalesce(sum(output_tokens), 0)::bigint, " +
        "coalesce(sum(cost), 0), greatest(count(distinct user_id), coalesce(max(archived_users), 0))::bigint";

    private static UsageRow Row(Npgsql.NpgsqlDataReader r) => new(r.Get<string>("key"), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetDecimal(4), r.GetInt64(5));

    private static DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// Whose calls the viewer may see: null for the whole tenant (<c>ai.policy_manage</c> or <c>ai.policy_view</c> at
    /// tenant scope), else the units of their <c>ai.policy_manage</c> subtrees. Nobody else sees usage.
    /// </summary>
    private async Task<Guid[]?> ScopeAsync(CancellationToken ct)
    {
        if ((await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed ||
            (await authz.CheckAsync(Permissions.AiPolicyView, AuthzTarget.Tenant, ct)).Allowed)
        {
            return null;
        }
        var coverage = await authz.CoverageAsync(Permissions.AiPolicyManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        return [.. await hierarchy.GetSubtreesAsync(coverage.Units, ct)];
    }

    private async Task<(string Currency, decimal Rate)> DisplayAsync(CancellationToken ct)
    {
        var document = (await policies.EffectiveAsync(ct)).Document;
        return document.DisplayCurrency is { } currency && document.DisplayRate is { } rate ? (currency, rate) : ("USD", 1m);
    }

    private (DateOnly Start, DateOnly End) Period(DateOnly? from, DateOnly? to, ValidationErrors errors)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        if (start > end || end.DayNumber - start.DayNumber > MaxDays)
        {
            errors.Add("from", "validation.ai.usage_period");
        }
        return (start, end);
    }

    public async Task<UsageView> ViewAsync(DateOnly? from, DateOnly? to, string? groupBy, CancellationToken ct)
    {
        var group = groupBy ?? UsageGroups.Day;
        var errors = new ValidationErrors();
        if (!UsageGroups.All.Contains(group))
        {
            errors.Add("groupBy", "validation.ai.usage_group");
        }
        var (start, end) = Period(from, to, errors);
        errors.ThrowIfAny();

        var units = await ScopeAsync(ct);
        if (units is not null && group == UsageGroups.User)
        {
            // Named users only for tenant-wide viewers (scenario 3).
            throw AppException.Denied();
        }

        var key = group switch
        {
            UsageGroups.Day => "to_char(at at time zone 'UTC', 'YYYY-MM-DD')",
            UsageGroups.UseCase => "use_case",
            UsageGroups.Model => "model",
            UsageGroups.Provider => "provider",
            UsageGroups.Unit => "unit_id::text",
            UsageGroups.Agent => "agent_id::text",
            UsageGroups.Classification => "classification",
            _ => "user_id::text",
        };
        var filter = "at >= @start and at < @end" + (units is null ? string.Empty : " and unit_id = any(@units)");
        var args = new { start = Start(start), end = Start(end.AddDays(1)), units };

        var rows = await session.QueryAsync(
            $"select {key} as key, {Sums} from {Source} where {filter} and decision = 'served' group by 1 order by 1", args, Row, ct);
        var totals = (await session.QueryAsync($"select null::text as key, {Sums} from {Source} where {filter} and decision = 'served'", args, Row, ct))[0];
        var denied = await session.QueryAsync(
            $"select denial, sum(requests)::bigint from {Source} where {filter} and decision = 'denied' group by 1 order by 2 desc", args,
            r => new DeniedRow(r.GetString(0), r.GetInt64(1)), ct);

        var hidden = 0;
        if (units is not null)
        {
            hidden = rows.Count(r => r.Users < MinimumGroupSize);
            rows = [.. rows.Where(r => r.Users >= MinimumGroupSize)];
        }
        var labels = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var ids = rows.Select(r => Guid.TryParse(r.Key, out var id) ? id : (Guid?)null).OfType<Guid>().ToList();
        if (group == UsageGroups.Unit && ids.Count > 0)
        {
            foreach (var (id, names) in await hierarchy.GetNamesAsync(ids, ct))
            {
                labels[id.ToString()] = names;
            }
        }
        else if (group == UsageGroups.User)
        {
            foreach (var id in ids)
            {
                if (await users.GetAsync(id, ct) is { } user)
                {
                    labels[id.ToString()] = new Dictionary<string, string> { ["default"] = user.DisplayName };
                }
            }
        }
        var display = await DisplayAsync(ct);
        return new UsageView(start, end, group, units is null ? "tenant" : "units", totals, rows, denied, MinimumGroupSize, hidden, labels, display.Currency, display.Rate);
    }

    /// <summary>The month end forecast (US4 R2). Unit viewers get their subtree only, with small groups hidden, and no budgets.</summary>
    public async Task<UsageForecast> ForecastAsync(CancellationToken ct)
    {
        var units = await ScopeAsync(ct);
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var elapsed = today.Day;
        var filter = "at >= @start and at < @end and decision = 'served'" + (units is null ? string.Empty : " and unit_id = any(@units)");
        var args = new { start = Start(monthStart), end = Start(today.AddDays(1)), units };
        var total = (await session.QueryAsync($"select null::text as key, {Sums} from {Source} where {filter}", args, Row, ct))[0];
        async Task<IReadOnlyList<UsageRow>> ByAsync(string column)
        {
            var rows = await session.QueryAsync($"select {column} as key, {Sums} from {Source} where {filter} group by 1 order by 5 desc", args, Row, ct);
            return units is null ? rows : [.. rows.Where(r => r.Users >= MinimumGroupSize)];
        }
        var byProvider = await ByAsync("provider");
        var byClassification = await ByAsync("classification");

        var rate = Math.Round(total.Cost / elapsed, 6);
        decimal Project(decimal used) => Math.Round(used / elapsed * daysInMonth, 2);
        var forecasts = new List<BudgetForecast>();
        if (units is null)
        {
            foreach (var b in (await budgets.ListAsync(ct)).Budgets.Where(b => b.Period == BudgetPeriods.Month && b.Amount is not null && b.Used is not null))
            {
                var projected = Project(b.Used!.Value);
                forecasts.Add(new BudgetForecast(b.Id, b.Scope, b.ScopeId, b.Amount!.Value, b.Used.Value, projected, projected > b.Amount.Value));
            }
        }
        var display = await DisplayAsync(ct);
        return new UsageForecast(monthStart, monthStart.AddDays(daysInMonth - 1), elapsed, daysInMonth, total.Cost, rate, Project(total.Cost), byProvider,
            byClassification, forecasts, units is null ? "tenant" : "units", display.Currency, display.Rate);
    }

    /// <summary>
    /// The CSV export (US4 scenario 4): one line per day, use case, model, provider, classification, unit and agent, with
    /// requests, tokens and cost in USD (and in the display currency when one is set). Tenant-wide viewers also get the
    /// person; unit viewers get their subtree only, without people, and lines covering fewer than five people are left
    /// out. Every export is audited with its period and scope.
    /// </summary>
    public async Task<(string FileName, string Csv)> ExportAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var (start, end) = Period(from, to, errors);
        errors.ThrowIfAny();
        var units = await ScopeAsync(ct);
        var named = units is null;
        var filter = "at >= @start and at < @end and decision = 'served'" + (named ? string.Empty : " and unit_id = any(@units)");
        var args = new { start = Start(start), end = Start(end.AddDays(1)), units };
        var rows = await session.QueryAsync(
            $"""
            select to_char(at at time zone 'UTC', 'YYYY-MM-DD'), use_case, model, provider, classification, unit_id::text, agent_id::text{(named ? ", user_id::text" : ", null::text")},
                   {Sums}
              from {Source} where {filter}
             group by 1, 2, 3, 4, 5, 6, 7{(named ? ", 8" : string.Empty)}
             order by 1, 2, 3, 4, 5, 6, 7{(named ? ", 8" : string.Empty)}
            """, args,
            r => (Day: r.GetString(0), UseCase: r.GetString(1), Model: r.Get<string?>("model"), Provider: r.Get<string?>("provider"),
                Classification: r.Get<string?>("classification"), Unit: r.Get<string?>("unit_id"), Agent: r.Get<string?>("agent_id"), User: r.IsDBNull(7) ? null : r.GetString(7),
                Requests: r.GetInt64(8), Input: r.GetInt64(9), Output: r.GetInt64(10), Cost: r.GetDecimal(11), Users: r.GetInt64(12)), ct);
        if (!named)
        {
            rows = [.. rows.Where(r => r.Users >= MinimumGroupSize)];
        }
        var display = await DisplayAsync(ct);
        var converted = display.Currency != "USD";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var csv = new System.Text.StringBuilder();
        csv.Append("date,use_case,model,provider,classification,unit_id,agent_id");
        csv.Append(named ? ",user_id" : string.Empty);
        csv.Append(",requests,input_tokens,output_tokens,cost_usd");
        csv.Append(converted ? $",cost_{display.Currency.ToLowerInvariant()}" : string.Empty);
        csv.Append("\r\n");
        foreach (var r in rows)
        {
            csv.Append(r.Day).Append(',').Append(Csv(r.UseCase)).Append(',').Append(Csv(r.Model)).Append(',').Append(Csv(r.Provider)).Append(',')
                .Append(Csv(r.Classification)).Append(',').Append(r.Unit).Append(',').Append(r.Agent);
            if (named)
            {
                csv.Append(',').Append(r.User);
            }
            csv.Append(',').Append(r.Requests.ToString(inv)).Append(',').Append(r.Input.ToString(inv)).Append(',').Append(r.Output.ToString(inv))
                .Append(',').Append(r.Cost.ToString("0.000000", inv));
            if (converted)
            {
                csv.Append(',').Append(Math.Round(r.Cost * display.Rate, 6).ToString("0.000000", inv));
            }
            csv.Append("\r\n");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AiUsageExported, "ai_usage", null,
            Changes: new { from = start, to = end, scope = named ? "tenant" : "units", rows = rows.Count, people = named }), ct);
        return ($"ai-usage-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}.csv", csv.ToString());
    }

    /// <summary>CSV escaping with formula-injection protection for spreadsheet tools.</summary>
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        if (value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}

/// <summary>Agent cost for spec 020 FR-018: served calls of the agent in the period, by version.</summary>
public sealed class AiAgentUsage(IDbSession session) : IAiAgentUsage
{
    public async Task<IReadOnlyList<AgentVersionCost>> AgentCostsAsync(Guid agentId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select agent_version, count(*), coalesce(sum(input_tokens + output_tokens), 0)::bigint, coalesce(sum(cost), 0)
            from ai.usage_record where agent_id = @agentId and agent_version is not null and decision = 'served' and at >= @from and at < @to
            group by 1 order by 1
            """, new { agentId, from, to }, r => new AgentVersionCost(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.GetDecimal(3)), ct);
}
