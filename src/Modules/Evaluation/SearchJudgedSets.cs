using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

/// <summary>A result a search should find for a judged query, with how relevant it is (1 relevant, 2 very relevant, 3 the best answer).</summary>
public sealed record Judgment(string? Source, Guid? ItemId, int? Grade);

public sealed record JudgedQuery(int N, string Query, string Locale, IReadOnlyList<Judgment> Judgments);

public sealed record JudgedQueryInput(string? Query, string? Locale, List<Judgment>? Judgments);

public sealed record JudgedSetCreateRequest(Dictionary<string, string>? Names, Guid? OrgUnitId);

public sealed record JudgedSetQueriesRequest(int? Version, List<JudgedQueryInput>? Queries);

public sealed record JudgedSetSummary(Guid Id, Dictionary<string, string> Names, Guid OrgUnitId, int Version, int Queries, IReadOnlyList<string> Locales, DateTimeOffset UpdatedAt);

public sealed record JudgedSetView(Guid Id, Dictionary<string, string> Names, Guid OrgUnitId, int Version, IReadOnlyList<JudgedQuery> Queries, DateTimeOffset UpdatedAt);

/// <summary>
/// Judged query sets for search ranking (spec 012 US8, FR-020; spec 021 US10): datasets of kind <c>search</c> that
/// belong to the tenant, managed by search administrators (<c>search.manage</c>). Each query names the results that
/// should rank high, with a grade, and runs as the default persona of the set's org unit, so its permissions are real.
/// Like every dataset, a set never changes: each save makes the next version, and a gate run keeps the version it used.
/// The retrieval release gate scores every set (precision at 5 and NDCG at 10) against the index in force and the
/// proposed one.
/// </summary>
public sealed class SearchJudgedSets(IDbSession session, ITenantContext context, IAuthorizationService authz, IOrganizationHierarchy hierarchy,
    IAuditWriter audit, IClock clock)
{
    public const int MaxQueries = 500;
    public const int MaxJudgments = 20;
    public const int MaxQueryLength = 200;

    public async Task<IReadOnlyList<JudgedSetSummary>> ListAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        return await session.QueryAsync(
            """
            select d.id, d.names::text, d.org_unit_id, d.current_version, coalesce(v.case_count, 0), coalesce(v.locales, '{}'), d.updated_at
            from evaluation.dataset d
            left join evaluation.dataset_version v on v.dataset_id = d.id and v.number = d.current_version
            where d.kind = 'search' order by d.created_at, d.id
            """, null,
            r => new JudgedSetSummary(r.GetGuid(0), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1))!, r.GetGuid(2), r.GetInt32(3), r.GetInt32(4),
                r.GetFieldValue<string[]>(5), r.GetFieldValue<DateTimeOffset>(6)), ct);
    }

    public async Task<JudgedSetView> CreateAsync(JudgedSetCreateRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var errors = new ValidationErrors();
        var names = (request.Names ?? []).Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToDictionary(n => n.Key, n => n.Value.Trim());
        if (names.Count == 0 || names.Any(n => !CaseRules.Locales.Contains(n.Key) || n.Value.Length > 120))
        {
            errors.Add("names", "validation.eval.dataset_names");
        }
        if (request.OrgUnitId is null || !await hierarchy.UnitExistsAsync(request.OrgUnitId.Value, ct))
        {
            errors.Add("orgUnitId", "validation.eval.persona_unit");
        }
        errors.ThrowIfAny();
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into evaluation.dataset (tenant_id, id, agent_id, org_unit_id, names, current_version, created_by, created_at, updated_at, kind)
            values (platform.current_tenant_id(), @id, null, @unit, @names, 0, @user, @now, @now, 'search')
            """, new { id, unit = request.OrgUnitId!.Value, names = Jsonb.From(names), user = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalDatasetVersionCreated, "eval_dataset", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { kind = "search", version = 0 }), ct);
        return await GetAsync(id, ct);
    }

    public async Task<JudgedSetView> GetAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(ct);
        var (names, unit, version, updated) = await LoadAsync(id, ct);
        return new JudgedSetView(id, names, unit, version, await QueriesAsync(session, id, version, ct), updated);
    }

    /// <summary>Saves the queries as the next version. The version sent must be the current one.</summary>
    public async Task<JudgedSetView> SaveQueriesAsync(Guid id, JudgedSetQueriesRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var (_, unit, version, _) = await LoadAsync(id, ct);
        if (request.Version != version)
        {
            throw AppException.Conflict("conflict.eval.dataset_version");
        }
        var errors = new ValidationErrors();
        var input = request.Queries ?? [];
        if (input.Count > MaxQueries)
        {
            errors.Add("queries", "validation.eval.cases_too_many");
        }
        var queries = new List<(string Query, string Locale, List<ExpectedSource> Sources)>();
        for (var i = 0; i < input.Count && i < MaxQueries; i++)
        {
            var q = input[i];
            var at = $"queries[{i}]";
            var text = (q?.Query ?? string.Empty).Trim();
            if (text.Length is 0 or > MaxQueryLength)
            {
                errors.Add($"{at}.query", "validation.eval.judged_query");
            }
            if (q?.Locale is null || !CaseRules.Locales.Contains(q.Locale))
            {
                errors.Add($"{at}.locale", "validation.eval.case_locale");
            }
            var judgments = q?.Judgments ?? [];
            if (judgments.Count is 0 or > MaxJudgments
                || judgments.Any(j => j is null || string.IsNullOrWhiteSpace(j.Source) || j.Source.Length > 40 || j.ItemId is null || j.Grade is not (>= 1 and <= 3))
                || judgments.Where(j => j?.ItemId is not null).GroupBy(j => (j.Source, j.ItemId)).Any(g => g.Count() > 1))
            {
                errors.Add($"{at}.judgments", "validation.eval.judgments");
                continue;
            }
            if (text.Length is > 0 and <= MaxQueryLength && q!.Locale is { } locale && CaseRules.Locales.Contains(locale))
            {
                queries.Add((text, locale, judgments.Select(j => new ExpectedSource(j.Source!.Trim(), j.ItemId, j.Grade)).ToList()));
            }
        }
        errors.ThrowIfAny();

        var number = version + 1;
        var now = clock.UtcNow;
        var user = context.RequireUser();
        var locales = queries.Select(q => q.Locale).Distinct().Order().ToArray();
        await session.ExecAsync(
            """
            insert into evaluation.dataset_version (tenant_id, dataset_id, number, case_count, classification, locales, source, created_by, created_at)
            values (platform.current_tenant_id(), @id, @number, @count, 'internal', @locales, 'edit', @user, @now)
            """, new { id, number, count = queries.Count, locales, user, now }, ct);
        for (var i = 0; i < queries.Count; i++)
        {
            var q = queries[i];
            await session.ExecAsync(
                """
                insert into evaluation.eval_case (tenant_id, dataset_id, version, n, input, locale, persona_id, expectations, tags, severity, classification)
                values (platform.current_tenant_id(), @id, @number, @n, @input, @locale, null, @expect, '{}', 'blocking', 'internal')
                """, new { id, number, n = i + 1, input = q.Query, locale = q.Locale, expect = Jsonb.From(new CaseExpectations(Sources: q.Sources)) }, ct);
        }
        await session.ExecAsync("update evaluation.dataset set current_version = @number, updated_at = @now where id = @id", new { id, number, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalDatasetVersionCreated, "eval_dataset", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { kind = "search", version = number, cases = queries.Count, locales }), ct);
        return await GetAsync(id, ct);
    }

    internal static async Task<IReadOnlyList<JudgedQuery>> QueriesAsync(IDbSession session, Guid id, int version, CancellationToken ct) =>
        await session.QueryAsync(
            "select n, input, locale, expectations::text from evaluation.eval_case where dataset_id = @id and version = @version order by n", new { id, version },
            r => new JudgedQuery(r.GetInt32(0), r.GetString(1), r.GetString(2),
                (JsonSerializer.Deserialize<CaseExpectations>(r.GetString(3), EvalJson.Options)!.Sources ?? []).Select(s => new Judgment(s.Source, s.ItemId, s.Grade ?? 1)).ToList()), ct);

    private async Task<(Dictionary<string, string> Names, Guid Unit, int Version, DateTimeOffset Updated)> LoadAsync(Guid id, CancellationToken ct)
    {
        var rows = await session.QueryAsync("select names::text, org_unit_id, current_version, updated_at from evaluation.dataset where id = @id and kind = 'search'", new { id },
            r => (JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(0))!, r.GetGuid(1), r.GetInt32(2), r.GetFieldValue<DateTimeOffset>(3)), ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }

    private Task DemandAsync(CancellationToken ct) => authz.DemandAsync(Permissions.SearchManage, AuthzTarget.Tenant, ct);
}
