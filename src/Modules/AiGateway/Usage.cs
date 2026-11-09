using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
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

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Day, UseCase, Model, Provider, Unit, Agent, User };
}

public sealed record UsageRow(string? Key, long Requests, long InputTokens, long OutputTokens, decimal Cost, long Users);

public sealed record DeniedRow(string Reason, long Requests);

public sealed record UsageView(
    DateOnly From,
    DateOnly To,
    string GroupBy,
    string Scope,
    UsageRow Totals,
    IReadOnlyList<UsageRow> Rows,
    IReadOnlyList<DeniedRow> Denied,
    int MinimumGroupSize,
    int HiddenGroups);

/// <summary>
/// The AI usage view (spec 013 US4, R1 part): requests, tokens and estimated cost by day, use case, model, provider,
/// org unit or agent, and denied calls by reason. Holders of <c>ai.policy_manage</c> or <c>ai.policy_view</c> at tenant
/// scope see the whole tenant and named users; an org-scoped <c>ai.policy_manage</c> holder sees calls made in their
/// subtree only, and groups with fewer than five people are hidden from them (scenario 3).
/// </summary>
public sealed class UsageReports(IDbSession session, IAuthorizationService authz, IOrganizationHierarchy hierarchy, IClock clock)
{
    public const int MinimumGroupSize = 5;
    public const int MaxDays = 400;

    public async Task<UsageView> ViewAsync(DateOnly? from, DateOnly? to, string? groupBy, CancellationToken ct)
    {
        var group = groupBy ?? UsageGroups.Day;
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        var errors = new ValidationErrors();
        if (!UsageGroups.All.Contains(group))
        {
            errors.Add("groupBy", "validation.ai.usage_group");
        }
        if (start > end || end.DayNumber - start.DayNumber > MaxDays)
        {
            errors.Add("from", "validation.ai.usage_period");
        }
        errors.ThrowIfAny();

        var tenantWide = (await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed ||
            (await authz.CheckAsync(Permissions.AiPolicyView, AuthzTarget.Tenant, ct)).Allowed;
        Guid[]? units = null;
        if (!tenantWide)
        {
            var coverage = await authz.CoverageAsync(Permissions.AiPolicyManage, ct);
            if (!coverage.Any)
            {
                throw AppException.Denied();
            }
            units = [.. await hierarchy.GetSubtreesAsync(coverage.Units, ct)];
            if (group == UsageGroups.User)
            {
                // Named users only for tenant-wide viewers (scenario 3).
                throw AppException.Denied();
            }
        }

        var key = group switch
        {
            UsageGroups.Day => "to_char(at at time zone 'UTC', 'YYYY-MM-DD')",
            UsageGroups.UseCase => "use_case",
            UsageGroups.Model => "model",
            UsageGroups.Provider => "provider",
            UsageGroups.Unit => "unit_id::text",
            UsageGroups.Agent => "agent_id::text",
            _ => "user_id::text",
        };
        var filter = "at >= @start and at < @end" + (units is null ? string.Empty : " and unit_id = any(@units)");
        var args = new { start = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), end = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), units };

        static UsageRow Row(Npgsql.NpgsqlDataReader r) => new(r.Get<string>("key"), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetDecimal(4), r.GetInt64(5));
        const string Sums = "count(*), coalesce(sum(input_tokens), 0)::bigint, coalesce(sum(output_tokens), 0)::bigint, coalesce(sum(cost), 0), count(distinct user_id)";
        var rows = await session.QueryAsync(
            $"select {key} as key, {Sums} from ai.usage_record where {filter} and decision = 'served' group by 1 order by 1", args, Row, ct);
        var totals = (await session.QueryAsync($"select null::text as key, {Sums} from ai.usage_record where {filter} and decision = 'served'", args, Row, ct))[0];
        var denied = await session.QueryAsync(
            $"select denial, count(*) from ai.usage_record where {filter} and decision = 'denied' group by 1 order by 2 desc", args,
            r => new DeniedRow(r.GetString(0), r.GetInt64(1)), ct);

        var hidden = 0;
        if (units is not null)
        {
            hidden = rows.Count(r => r.Users < MinimumGroupSize);
            rows = [.. rows.Where(r => r.Users >= MinimumGroupSize)];
        }
        return new UsageView(start, end, group, units is null ? "tenant" : "units", totals, rows, denied, MinimumGroupSize, hidden);
    }
}
