using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

public sealed record DatasetCreateRequest(Guid? AgentId, Dictionary<string, string>? Names);

public sealed record DatasetCasesRequest(int? Version, List<CaseInput>? Cases);

public sealed record ImportRequest(string? Format, string? Content);

public sealed record ImportError(int Row, string Field, string Key);

/// <summary>An import checked and waiting (US2 scenario 4): how many rows are valid, and every refused row with its reason.</summary>
public sealed record ImportPreview(Guid ImportId, int BaseVersion, int Valid, IReadOnlyList<ImportError> Errors, IReadOnlyList<CaseInput> Sample);

public sealed record CaseView(int N, string Input, string Locale, Guid? PersonaId, CaseExpectations Expect, IReadOnlyList<string> Tags, string Severity, string Classification);

public sealed record DatasetVersionView(int Number, int CaseCount, string Classification, IReadOnlyList<string> Locales, string Source, Guid CreatedBy, DateTimeOffset CreatedAt);

public sealed record DatasetView(Guid Id, Guid AgentId, Dictionary<string, string> Names, int Version, IReadOnlyList<DatasetVersionView> Versions, IReadOnlyList<CaseView> Cases, bool CanEdit);

/// <summary>
/// Golden datasets (US2, FR-005). A dataset belongs to an agent and its unit. Versions never change: every edit or
/// confirmed import makes the next version, so a version used as evidence stays as it was. A dataset takes the highest
/// classification of its cases.
/// </summary>
public sealed class DatasetService(IDbSession session, ITenantContext context, EvalAccess access, IAuditWriter audit, IClock clock)
{
    public const int MaxImportBytes = 2 * 1024 * 1024;

    internal static async Task<IReadOnlyList<DatasetSummary>> SummariesAsync(IDbSession session, Guid agentId, CancellationToken ct) =>
        await session.QueryAsync(
            """
            select d.id, d.names::text, d.current_version, coalesce(v.case_count, 0), coalesce(v.classification, 'internal'), coalesce(v.locales, '{}'), d.updated_at
            from evaluation.dataset d
            left join evaluation.dataset_version v on v.dataset_id = d.id and v.number = d.current_version
            where d.agent_id = @agent order by d.created_at, d.id
            """, new { agent = agentId },
            r => new DatasetSummary(r.GetGuid(0), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1))!, r.GetInt32(2), r.GetInt32(3), r.GetString(4),
                r.GetFieldValue<string[]>(5), r.GetFieldValue<DateTimeOffset>(6)), ct);

    public async Task<IReadOnlyList<DatasetSummary>> ListAsync(Guid agentId, CancellationToken ct)
    {
        _ = await access.ReadableAgentAsync(agentId, ct);
        return await SummariesAsync(session, agentId, ct);
    }

    public async Task<DatasetView> CreateAsync(DatasetCreateRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (request.AgentId is null)
        {
            errors.Add("agentId", "validation.eval.dataset_agent");
        }
        var names = Names(request.Names, errors);
        errors.ThrowIfAny();
        var agent = await access.DesignableAgentAsync(request.AgentId!.Value, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into evaluation.dataset (tenant_id, id, agent_id, org_unit_id, names, current_version, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @agent, @unit, @names, 0, @user, @now, @now)
            """, new { id, agent = agent.Id, unit = agent.OrgUnitId, names = Jsonb.From(names), user = context.RequireUser(), now }, ct);
        return await GetAsync(id, null, ct);
    }

    private static Dictionary<string, string> Names(Dictionary<string, string>? names, ValidationErrors errors)
    {
        var clean = (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToDictionary(n => n.Key, n => n.Value.Trim());
        if (clean.Count == 0 || clean.Any(n => !CaseRules.Locales.Contains(n.Key) || n.Value.Length > 120))
        {
            errors.Add("names", "validation.eval.dataset_names");
        }
        return clean;
    }

    private async Task<(Guid Agent, Guid Unit, int Version, Dictionary<string, string> Names)> LoadAsync(Guid id, CancellationToken ct)
    {
        var rows = await session.QueryAsync("select agent_id, org_unit_id, current_version, names::text from evaluation.dataset where id = @id", new { id },
            r => (r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3))!), ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }

    public async Task<DatasetView> GetAsync(Guid id, int? version, CancellationToken ct)
    {
        var d = await LoadAsync(id, ct);
        var agent = await access.ReadableAgentAsync(d.Agent, ct);
        var number = version ?? d.Version;
        var versions = await session.QueryAsync(
            "select number, case_count, classification, locales, source, created_by, created_at from evaluation.dataset_version where dataset_id = @id order by number desc",
            new { id }, r => new DatasetVersionView(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetString(4), r.GetGuid(5),
                r.GetFieldValue<DateTimeOffset>(6)), ct);
        if (number != 0 && versions.All(v => v.Number != number))
        {
            throw AppException.NotFound();
        }
        return new DatasetView(id, d.Agent, d.Names, number, versions, await CasesAsync(id, number, ct), await access.CanDesignAsync(agent, ct));
    }

    internal async Task<IReadOnlyList<CaseView>> CasesAsync(Guid id, int version, CancellationToken ct) =>
        await session.QueryAsync(
            "select n, input, locale, persona_id, expectations::text, tags, severity, classification from evaluation.eval_case where dataset_id = @id and version = @version order by n",
            new { id, version },
            r => new CaseView(r.GetInt32(0), r.GetString(1), r.GetString(2), r.Get<Guid?>("persona_id"), JsonSerializer.Deserialize<CaseExpectations>(r.GetString(4), EvalJson.Options)!,
                r.GetFieldValue<string[]>(5), r.GetString(6), r.GetString(7)), ct);

    /// <summary>Replaces the cases with a new version (US2 scenario 3). The version sent must be the current one.</summary>
    public async Task<DatasetView> SaveCasesAsync(Guid id, DatasetCasesRequest request, CancellationToken ct)
    {
        var d = await LoadAsync(id, ct);
        _ = await access.DesignableAgentAsync(d.Agent, ct);
        if (request.Version != d.Version)
        {
            throw AppException.Conflict("conflict.eval.dataset_version");
        }
        var errors = new ValidationErrors();
        var cases = new List<CaseInput>();
        var input = request.Cases ?? [];
        if (input.Count > CaseRules.MaxCases)
        {
            errors.Add("cases", "validation.eval.cases_too_many");
        }
        for (var i = 0; i < input.Count && i < CaseRules.MaxCases; i++)
        {
            if (CaseRules.Check(input[i], $"cases[{i}]", errors) is { } c)
            {
                cases.Add(c);
            }
        }
        await CheckPersonasAsync(cases, (i, key) => errors.Add($"cases[{i}].personaId", key), ct);
        errors.ThrowIfAny();
        await WriteVersionAsync(id, d, cases, "edit", ct);
        return await GetAsync(id, null, ct);
    }

    private async Task CheckPersonasAsync(IReadOnlyList<CaseInput> cases, Action<int, string> fail, CancellationToken ct)
    {
        var ids = cases.Where(c => c.PersonaId is not null).Select(c => c.PersonaId!.Value).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }
        var known = (await session.QueryAsync("select id from evaluation.persona where id = any(@ids)", new { ids }, r => r.GetGuid(0), ct)).ToHashSet();
        for (var i = 0; i < cases.Count; i++)
        {
            if (cases[i].PersonaId is { } p && !known.Contains(p))
            {
                fail(i, "validation.eval.case_persona");
            }
        }
    }

    private async Task<int> WriteVersionAsync(Guid id, (Guid Agent, Guid Unit, int Version, Dictionary<string, string> Names) d, IReadOnlyList<CaseInput> cases, string source,
        CancellationToken ct)
    {
        var number = d.Version + 1;
        var now = clock.UtcNow;
        var user = context.RequireUser();
        var classification = Classifications.Highest(cases.Select(c => c.Classification!));
        var locales = cases.Select(c => c.Locale!).Distinct().Order().ToArray();
        await session.ExecAsync(
            """
            insert into evaluation.dataset_version (tenant_id, dataset_id, number, case_count, classification, locales, source, created_by, created_at)
            values (platform.current_tenant_id(), @id, @number, @count, @classification, @locales, @source, @user, @now)
            """, new { id, number, count = cases.Count, classification, locales, source, user, now }, ct);
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            await session.ExecAsync(
                """
                insert into evaluation.eval_case (tenant_id, dataset_id, version, n, input, locale, persona_id, expectations, tags, severity, classification)
                values (platform.current_tenant_id(), @id, @number, @n, @input, @locale, @persona, @expect, @tags, @severity, @classification)
                """, new
                {
                    id, number, n = i + 1, input = c.Input, locale = c.Locale, persona = c.PersonaId, expect = Jsonb.From(c.Expect), tags = c.Tags!.ToArray(),
                    severity = c.Severity, classification = c.Classification,
                }, ct);
        }
        await session.ExecAsync("update evaluation.dataset set current_version = @number, updated_at = @now where id = @id", new { id, number, now }, ct);
        await audit.WriteAsync(new AuditEntry(source == "import" ? AuditActions.EvalDatasetImported : AuditActions.EvalDatasetVersionCreated, "eval_dataset", id.ToString(),
            TargetOrgUnitId: d.Unit, Changes: new { agentId = d.Agent, version = number, cases = cases.Count, classification, locales }), ct);
        return number;
    }

    /// <summary>
    /// Checks a CSV or JSON import (US2 scenario 4) and keeps it waiting: every row is validated, refused rows are listed
    /// with reasons, and nothing reaches the dataset until the person confirms.
    /// </summary>
    public async Task<ImportPreview> ImportAsync(Guid id, ImportRequest request, CancellationToken ct)
    {
        var d = await LoadAsync(id, ct);
        _ = await access.DesignableAgentAsync(d.Agent, ct);
        if (request.Content is null || Encoding.UTF8.GetByteCount(request.Content) > MaxImportBytes)
        {
            throw AppException.Validation("content", "validation.eval.import_size");
        }
        var rows = request.Format switch
        {
            "csv" => CaseImport.FromCsv(request.Content),
            "json" => CaseImport.FromJson(request.Content),
            _ => throw AppException.Validation("format", "validation.eval.import_format"),
        };
        var errors = new List<ImportError>(rows.Errors);
        var valid = new List<(int Row, CaseInput Case)>();
        foreach (var (row, input) in rows.Rows)
        {
            var e = new ValidationErrors();
            if (CaseRules.Check(input, "case", e) is { } c)
            {
                valid.Add((row, c));
            }
            else
            {
                errors.AddRange(CaseImport.Flatten(row, e));
            }
        }
        var personaErrors = new List<int>();
        await CheckPersonasAsync(valid.Select(v => v.Case).ToList(), (i, key) => personaErrors.Add(i), ct);
        foreach (var i in personaErrors.Distinct().OrderDescending())
        {
            errors.Add(new ImportError(valid[i].Row, "personaId", "validation.eval.case_persona"));
            valid.RemoveAt(i);
        }
        if (rows.Rows.Count > CaseRules.MaxCases || d.Version > 0 && (await CasesAsync(id, d.Version, ct)).Count + valid.Count > CaseRules.MaxCases)
        {
            throw AppException.Validation("content", "validation.eval.cases_too_many");
        }

        var importId = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync("delete from evaluation.import where created_at < @old", new { old = now.AddDays(-1) }, ct);
        await session.ExecAsync(
            """
            insert into evaluation.import (tenant_id, id, dataset_id, base_version, cases, errors, created_by, created_at)
            values (platform.current_tenant_id(), @importId, @id, @version, @cases, @errors, @user, @now)
            """, new
            {
                importId, id, version = d.Version, cases = Jsonb.From(valid.Select(v => v.Case).ToList()), errors = Jsonb.From(errors.OrderBy(e => e.Row).ToList()),
                user = context.RequireUser(), now,
            }, ct);
        return new ImportPreview(importId, d.Version, valid.Count, errors.OrderBy(e => e.Row).ThenBy(e => e.Field, StringComparer.Ordinal).ToList(),
            valid.Take(20).Select(v => v.Case).ToList());
    }

    /// <summary>Adds the waiting import's valid rows to the current cases as the next version.</summary>
    public async Task<DatasetView> ConfirmImportAsync(Guid id, Guid importId, CancellationToken ct)
    {
        var d = await LoadAsync(id, ct);
        _ = await access.DesignableAgentAsync(d.Agent, ct);
        var rows = await session.QueryAsync(
            "select base_version, cases::text, created_by from evaluation.import where id = @importId and dataset_id = @id and confirmed_at is null",
            new { importId, id }, r => (Base: r.GetInt32(0), Cases: r.GetString(1), By: r.GetGuid(2)), ct);
        if (rows.Count == 0 || rows[0].By != context.RequireUser())
        {
            throw AppException.NotFound();
        }
        if (rows[0].Base != d.Version)
        {
            throw AppException.Conflict("conflict.eval.import_stale");
        }
        var imported = JsonSerializer.Deserialize<List<CaseInput>>(rows[0].Cases, EvalJson.Options)!;
        if (imported.Count == 0)
        {
            throw AppException.Validation("content", "validation.eval.import_empty");
        }
        var existing = (await CasesAsync(id, d.Version, ct))
            .Select(c => new CaseInput(c.Input, c.Locale, c.PersonaId, c.Expect, c.Tags.ToList(), c.Severity, c.Classification));
        await WriteVersionAsync(id, d, existing.Concat(imported).ToList(), "import", ct);
        await session.ExecAsync("update evaluation.import set confirmed_at = @now where id = @importId", new { importId, now = clock.UtcNow }, ct);
        return await GetAsync(id, null, ct);
    }
}

/// <summary>
/// Reads cases from CSV (header row, list cells separated by <c>|</c>, sources as <c>source:itemId</c>) or from a JSON
/// array of cases. Row numbers count data rows from 1.
/// </summary>
public static class CaseImport
{
    public static readonly IReadOnlyList<string> Columns =
        ["input", "locale", "persona", "answer", "key_facts", "sources", "forbidden_sources", "tools", "forbidden_tools", "refusal", "escalation", "tags", "severity", "classification"];

    public sealed record Parsed(List<(int Row, CaseInput? Case)> Rows, List<ImportError> Errors);

    public static IEnumerable<ImportError> Flatten(int row, ValidationErrors errors) =>
        errors.Entries.Select(e => new ImportError(row, e.Field.StartsWith("case.", StringComparison.Ordinal) ? e.Field[5..] : e.Field, e.Key));

    public static Parsed FromJson(string content)
    {
        try
        {
            var cases = JsonSerializer.Deserialize<List<CaseInput?>>(content, EvalJson.Options) ?? [];
            return new Parsed(cases.Select((c, i) => (i + 1, c)).ToList(), []);
        }
        catch (JsonException)
        {
            return new Parsed([], [new ImportError(0, "content", "validation.eval.import_json")]);
        }
    }

    public static Parsed FromCsv(string content)
    {
        var records = Records(content);
        if (records.Count == 0)
        {
            return new Parsed([], [new ImportError(0, "content", "validation.eval.import_empty")]);
        }
        var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var errors = new List<ImportError>();
        foreach (var h in header.Where(h => !Columns.Contains(h)))
        {
            errors.Add(new ImportError(0, h, "validation.eval.import_column"));
        }
        if (!header.Contains("input") || !header.Contains("locale"))
        {
            errors.Add(new ImportError(0, "header", "validation.eval.import_header"));
        }
        if (errors.Count > 0)
        {
            return new Parsed([], errors);
        }
        var rows = new List<(int, CaseInput?)>();
        for (var i = 1; i < records.Count; i++)
        {
            var cells = records[i];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            string Cell(string name) => header.IndexOf(name) is var at and >= 0 && at < cells.Count ? cells[at].Trim() : string.Empty;
            List<string> List(string name) => Cell(name).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            List<ExpectedSource> Sources(string name) => List(name).Select(s =>
            {
                var colon = s.IndexOf(':');
                return colon > 0 && Guid.TryParse(s[(colon + 1)..], out var item) ? new ExpectedSource(s[..colon], item) : new ExpectedSource(null, null);
            }).ToList();
            bool Flag(string name) => Cell(name).ToLowerInvariant() is "true" or "yes" or "1";
            var persona = Cell("persona");
            if (persona.Length > 0 && !Guid.TryParse(persona, out _))
            {
                errors.Add(new ImportError(i, "personaId", "validation.eval.case_persona"));
                continue;
            }
            var expect = new CaseExpectations(
                Cell("answer") is { Length: > 0 } a ? a : null, List("key_facts"), Sources("sources"), Sources("forbidden_sources"),
                List("tools").Select(t => new ExpectedTool(t, null)).ToList(), List("forbidden_tools"), false, Flag("refusal"), Flag("escalation"));
            rows.Add((i, new CaseInput(Cell("input"), Cell("locale"), persona.Length > 0 ? Guid.Parse(persona) : null, expect, List("tags"),
                Cell("severity") is { Length: > 0 } s ? s : null, Cell("classification") is { Length: > 0 } c ? c : null)));
        }
        return new Parsed(rows, errors);
    }

    /// <summary>RFC 4180 records: quoted cells may hold commas, line breaks and doubled quotes.</summary>
    public static List<List<string>> Records(string content)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var text = content.TrimStart('﻿');
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(ch);
                }
                continue;
            }
            switch (ch)
            {
                case '"' when cell.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    record.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    record.Add(cell.ToString());
                    cell.Clear();
                    records.Add(record);
                    record = [];
                    break;
                default:
                    cell.Append(ch);
                    break;
            }
        }
        if (cell.Length > 0 || record.Count > 0)
        {
            record.Add(cell.ToString());
            records.Add(record);
        }
        return records;
    }
}
