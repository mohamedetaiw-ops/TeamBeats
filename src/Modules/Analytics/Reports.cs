using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>The dashboards a report or an export can be made from; each runs the same code its page calls.</summary>
public static class ReportDashboards
{
    public const string Overview = "overview";
    public const string Campaign = "campaign";
    public const string Service = "service";
    public const string Compliance = "compliance";
    public const string ScopeUnit = "scope_unit";
    public const string ScopeLocation = "scope_location";
    public const string Experience = "experience";
    public const string Sentiment = "sentiment";
    public const string AiUsage = "ai_usage";

    public static readonly IReadOnlyList<string> All = [Overview, Campaign, Service, Compliance, ScopeUnit, ScopeLocation, Experience, Sentiment, AiUsage];

    /// <summary>Dashboards that read one thing, named by <see cref="ReportDefinition.Target"/>.</summary>
    public static readonly IReadOnlySet<string> Targeted = new HashSet<string>(StringComparer.Ordinal) { Campaign, Service, ScopeUnit, ScopeLocation };

    /// <summary>Dashboards with a period: a schedule sends the last week or month, an export the period it names.</summary>
    public static readonly IReadOnlySet<string> Dated = new HashSet<string>(StringComparer.Ordinal) { Overview, Service, ScopeUnit, ScopeLocation, Experience, Sentiment, AiUsage };
}

public static class ReportCadences
{
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
}

public static class ReportFormats
{
    public const string Csv = "csv";
    public const string Xlsx = "xlsx";
}

public static class ReportStatuses
{
    public const string Queued = "queued";
    public const string Ready = "ready";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>
/// One dashboard with its filter set. <see cref="Metric"/>, <see cref="Filters"/> and <see cref="GroupBy"/> belong to the
/// overview metric query; <see cref="Target"/> names the campaign, service, unit or location of a targeted dashboard.
/// </summary>
public sealed record ReportDefinition(string? Dashboard, string? Target = null, string? Metric = null, MetricFilters? Filters = null, string? GroupBy = null);

public sealed record ExportRequest(ReportDefinition? Definition, string? Format, string? Name);

public sealed record ScheduleRequest(string? Name, ReportDefinition? Definition, string? Cadence, Guid? RecipientUnitId);

public sealed record ReportScheduleView(Guid Id, string Name, ReportDefinition Definition, string Cadence, Guid? RecipientUnitId,
    IReadOnlyDictionary<string, string>? RecipientUnitNames, DateTimeOffset NextRunAt, DateTimeOffset? LastRunAt, DateTimeOffset CreatedAt);

public sealed record ReportSummary(Guid Id, string Name, string Dashboard, Guid? ScheduleId, bool Mine, string? Format, string Status,
    DateOnly? PeriodFrom, DateOnly? PeriodTo, DateTimeOffset CreatedAt, DateTimeOffset? DoneAt);

/// <summary>One table of a report: the columns are JSON paths of the dashboard's answer, the cells its values.</summary>
public sealed record ReportTable(string Key, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<JsonElement?>> Rows);

public sealed record ReportView(ReportSummary Report, ReportDefinition Definition, bool CanDownload, JsonElement? Result, IReadOnlyList<ReportTable> Tables);

/// <summary>What the report actions on a dashboard may offer, and the units a schedule may go to.</summary>
/// <summary>What the package allows on a dashboard: exports, schedules (and their units), and AI summaries (both under analytics.advanced).</summary>
public sealed record ReportOptions(bool CanExport, bool CanSchedule, IReadOnlyList<WorkspaceUnit> Units, bool CanSummarize = false);

public sealed record ReportList(bool CanExport, bool CanSchedule, IReadOnlyList<ReportSummary> Reports, IReadOnlyList<ReportScheduleView> Schedules);

/// <summary>The file of a report, built when it is downloaded from the stored, already aggregated result.</summary>
public sealed record ReportFile(string FileName, string ContentType, byte[] Content);

/// <summary>Runs one report definition as the person in the tenant context, through the dashboard's own service.</summary>
public sealed class ReportDefinitions(IServiceProvider services)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Checks the shape of a definition; permissions and filters are checked when it runs.</summary>
    public static ReportDefinition Validate(ReportDefinition? definition)
    {
        var errors = new ValidationErrors();
        var d = definition ?? new ReportDefinition(null);
        if (d.Dashboard is null || !ReportDashboards.All.Contains(d.Dashboard))
        {
            errors.Add("definition.dashboard", "validation.analytics.dashboard");
            errors.ThrowIfAny();
        }
        var targeted = ReportDashboards.Targeted.Contains(d.Dashboard!);
        if (targeted && string.IsNullOrWhiteSpace(d.Target))
        {
            errors.Add("definition.target", "validation.required");
        }
        else if (targeted && d.Dashboard != ReportDashboards.ScopeLocation && !Guid.TryParse(d.Target, out _))
        {
            errors.Add("definition.target", "validation.analytics.target");
        }
        else if (d.Target is { Length: > 200 })
        {
            errors.Add("definition.target", "validation.analytics.target");
        }
        if (!targeted && d.Target is not null)
        {
            errors.Add("definition.target", "validation.analytics.target");
        }
        if (d.Dashboard == ReportDashboards.Overview && string.IsNullOrEmpty(d.Metric))
        {
            errors.Add("definition.metric", "validation.analytics.metric");
        }
        if (d.Dashboard != ReportDashboards.Overview && (d.Metric is not null || d.GroupBy is not null || HasNonDateFilter(d.Filters)))
        {
            errors.Add("definition.filters", "validation.analytics.filter");
        }
        errors.ThrowIfAny();
        return d;
    }

    private static bool HasNonDateFilter(MetricFilters? f) =>
        f is not null && (f.UnitId is not null || f.Subtree is not null || f.Location is not null || f.Channel is not null || f.Locale is not null
            || f.MembershipType is not null || f.SiteId is not null || f.ContentType is not null);

    /// <summary>The dashboard's answer, as its page receives it. Dates override the definition's when given.</summary>
    public async Task<JsonElement> RunAsync(ReportDefinition d, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        from ??= d.Filters?.From;
        to ??= d.Filters?.To;
        object result = d.Dashboard switch
        {
            ReportDashboards.Overview => await services.GetRequiredService<Workspace>().QueryAsync(
                new MetricQuery(d.Metric, (d.Filters ?? new MetricFilters()) with { From = from, To = to }, d.GroupBy), ct),
            ReportDashboards.Campaign => await services.GetRequiredService<CampaignAnalytics>().GetAsync(Guid.Parse(d.Target!), ct),
            ReportDashboards.Service => await services.GetRequiredService<ServiceAnalytics>().GetAsync(Guid.Parse(d.Target!), from, to, ct),
            ReportDashboards.Compliance => await services.GetRequiredService<ComplianceAnalytics>().GetAsync(ct),
            ReportDashboards.ScopeUnit => await services.GetRequiredService<ScopeDashboards>().UnitAsync(Guid.Parse(d.Target!), from, to, ct),
            ReportDashboards.ScopeLocation => await services.GetRequiredService<ScopeDashboards>().LocationAsync(d.Target!, from, to, ct),
            ReportDashboards.Experience => await services.GetRequiredService<ExperienceAnalytics>().GetAsync(from, to, ct),
            ReportDashboards.Sentiment => await services.GetRequiredService<SentimentAnalytics>().GetAsync(from, to, ct),
            ReportDashboards.AiUsage => await services.GetRequiredService<AiUsageAnalytics>().GetAsync(from, to, ct),
            _ => throw AppException.Validation("definition.dashboard", "validation.analytics.dashboard"),
        };
        return JsonSerializer.SerializeToElement(result, result.GetType(), Json);
    }

    /// <summary>The period a scheduled send covers: the seven days, or the calendar month length, before the send day.</summary>
    internal static (DateOnly From, DateOnly To) Period(string cadence, DateTimeOffset at)
    {
        var to = DateOnly.FromDateTime(at.UtcDateTime).AddDays(-1);
        var from = cadence == ReportCadences.Monthly ? to.AddDays(1).AddMonths(-1) : to.AddDays(-6);
        return (from, to);
    }

    internal static DateTimeOffset Next(string cadence, DateTimeOffset at) => cadence == ReportCadences.Monthly ? at.AddMonths(1) : at.AddDays(7);
}

/// <summary>
/// Turns a dashboard's answer into tables, the same for every dashboard: top level values go to <c>summary</c>, and each
/// list of records becomes its own table, nested lists too, with <c>row</c> pointing at the record that holds them.
/// Names in several languages stay as one column per language. Nothing is computed here: suppressed figures stay null.
/// </summary>
internal static class ReportTables
{
    public const string Summary = "summary";
    public const string RowColumn = "row";

    private sealed class Builder(string key, bool nested)
    {
        public string Key { get; } = key;
        public List<string> Columns { get; } = nested ? [RowColumn] : [];
        public List<Dictionary<string, JsonElement?>> Rows { get; } = [];
    }

    public static IReadOnlyList<ReportTable> From(JsonElement root)
    {
        var tables = new List<Builder>();
        var summary = new Builder(Summary, false);
        tables.Add(summary);
        var row = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        Flatten(root, "", row, summary, tables, "", null);
        if (row.Count > 0)
        {
            summary.Rows.Add(row);
        }
        return tables.Where(t => t.Rows.Count > 0)
            .Select(t => new ReportTable(t.Key, t.Columns, t.Rows.Select(r => (IReadOnlyList<JsonElement?>)t.Columns.Select(c => r.GetValueOrDefault(c)).ToList()).ToList()))
            .ToList();
    }

    private static void Flatten(JsonElement value, string path, Dictionary<string, JsonElement?> row, Builder table, List<Builder> tables, string tablePath, int? parent)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in value.EnumerateObject())
                {
                    Flatten(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, row, table, tables, tablePath, parent);
                }
                break;
            case JsonValueKind.Array when value.EnumerateArray().Any(e => e.ValueKind is JsonValueKind.Object or JsonValueKind.Array):
                var key = tablePath.Length == 0 ? path : tablePath + "." + path;
                var child = tables.FirstOrDefault(t => t.Key == key);
                if (child is null)
                {
                    child = new Builder(key, table.Key != Summary || parent is not null || tablePath.Length > 0);
                    tables.Add(child);
                }
                var holder = (parent ?? 0) + 1;
                foreach (var item in value.EnumerateArray())
                {
                    var r = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
                    if (child.Columns.Count > 0 && child.Columns[0] == RowColumn)
                    {
                        r[RowColumn] = JsonSerializer.SerializeToElement(holder);
                    }
                    var index = child.Rows.Count;
                    child.Rows.Add(r);
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        Flatten(item, "", r, child, tables, key, index);
                    }
                    else
                    {
                        Set(child, r, "value", JsonSerializer.SerializeToElement(item.ValueKind == JsonValueKind.Array
                            ? string.Join("; ", item.EnumerateArray().Select(Text)) : Text(item)));
                    }
                }
                break;
            case JsonValueKind.Array:
                Set(table, row, path, JsonSerializer.SerializeToElement(string.Join("; ", value.EnumerateArray().Select(Text))));
                break;
            default:
                Set(table, row, path, value.ValueKind == JsonValueKind.Null ? null : value.Clone());
                break;
        }
    }

    private static void Set(Builder table, Dictionary<string, JsonElement?> row, string column, JsonElement? value)
    {
        if (!table.Columns.Contains(column))
        {
            table.Columns.Add(column);
        }
        row[column] = value;
    }

    public static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    /// <summary>CSV with each table under a <c># key</c> line, and formula characters escaped for spreadsheet tools.</summary>
    public static byte[] Csv(IReadOnlyList<ReportTable> tables)
    {
        var sb = new StringBuilder();
        foreach (var t in tables)
        {
            if (sb.Length > 0)
            {
                sb.Append("\r\n");
            }
            sb.Append("# ").Append(Cell(t.Key)).Append("\r\n");
            sb.Append(string.Join(',', t.Columns.Select(Cell))).Append("\r\n");
            foreach (var r in t.Rows)
            {
                sb.Append(string.Join(',', r.Select(c => c is { } v ? Cell(Text(v)) : ""))).Append("\r\n");
            }
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    internal static string Cell(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>An Office Open XML workbook with one sheet per table and inline strings, so no cell is ever a formula.</summary>
    public static byte[] Xlsx(IReadOnlyList<ReportTable> tables)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Entry(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            var names = SheetNames(tables.Select(t => t.Key).ToList());
            var sheets = string.Concat(names.Select((n, i) => $"""<sheet name="{Xml(n)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>"""));
            var overrides = string.Concat(names.Select((_, i) =>
                $"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""));
            Entry("[Content_Types].xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>{overrides}</Types>""");
            Entry("_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Entry("xl/workbook.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>{sheets}</sheets></workbook>""");
            Entry("xl/_rels/workbook.xml.rels",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{string.Concat(names.Select((_, i) => $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>"""))}</Relationships>""");
            for (var i = 0; i < tables.Count; i++)
            {
                var t = tables[i];
                var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
                var r = 1;
                sb.Append(Row(r++, t.Columns.Select(c => (JsonElement?)JsonSerializer.SerializeToElement(c)).ToList()));
                foreach (var row in t.Rows)
                {
                    sb.Append(Row(r++, row));
                }
                sb.Append("</sheetData></worksheet>");
                Entry($"xl/worksheets/sheet{i + 1}.xml", sb.ToString());
            }
        }
        return stream.ToArray();
    }

    private static string Row(int number, IReadOnlyList<JsonElement?> cells)
    {
        var sb = new StringBuilder($"""<row r="{number}">""");
        for (var c = 0; c < cells.Count; c++)
        {
            var reference = Column(c) + number.ToString(CultureInfo.InvariantCulture);
            switch (cells[c])
            {
                case null:
                    break;
                case { ValueKind: JsonValueKind.Number } n:
                    sb.Append($"""<c r="{reference}"><v>{n.GetRawText()}</v></c>""");
                    break;
                case { } v:
                    sb.Append($"""<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{Xml(Text(v))}</t></is></c>""");
                    break;
            }
        }
        return sb.Append("</row>").ToString();
    }

    internal static string Column(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }
        return name;
    }

    /// <summary>Sheet names: at most 31 characters, none of <c>[]:*?/\</c>, unique ignoring case.</summary>
    internal static IReadOnlyList<string> SheetNames(IReadOnlyList<string> keys)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var key in keys)
        {
            var clean = new string(key.Select(ch => "[]:*?/\\".Contains(ch) ? '_' : ch).ToArray());
            var name = clean.Length > 31 ? clean[^31..] : clean.Length == 0 ? "sheet" : clean;
            for (var n = 2; !used.Add(name); n++)
            {
                var suffix = "~" + n.ToString(CultureInfo.InvariantCulture);
                name = (clean.Length + suffix.Length > 31 ? clean[^(31 - suffix.Length)..] : clean) + suffix;
            }
            names.Add(name);
        }
        return names;
    }

    private static string Xml(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case < ' ' when ch is not '\t' and not '\n' and not '\r': break; // Not allowed in XML 1.0.
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Scheduled reports and exports (spec 021 US7, FR-011, FR-012, T016). A schedule sends one dashboard with its filters
/// weekly or monthly to its owner or to the members of one unit; an export makes one file of a dashboard. Both are
/// computed in the background as each recipient, with that person's permissions at send time, through the dashboard's own
/// code, so the minimum group size and the scope rules are the same as on the page. A recipient who may not open the
/// dashboard gets nothing and the owner is told. Files need <c>analytics.export</c> and are audited; schedules need
/// <c>analytics.advanced</c>.
/// </summary>
public sealed class AnalyticsReports(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context,
    IOrganizationHierarchy organization, IAuditWriter audit, ReportDefinitions definitions, IClock clock)
{
    public const int MaxSchedulesPerOwner = 50;
    public const int MaxReportsListed = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Link(Guid id) => "/analytics/reports/" + id;

    private async Task<Guid> DemandViewerAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsCore, ct);
        var user = context.RequireUser();
        if (!(await authz.CoverageAsync(Permissions.AnalyticsView, ct)).Any)
        {
            throw AppException.Denied();
        }
        return user;
    }

    private static string Name(string? name, ReportDefinition d)
    {
        var n = (name ?? "").Trim();
        if (n.Length > 120)
        {
            throw AppException.Validation("name", "validation.analytics.name_too_long");
        }
        return n.Length == 0 ? d.Dashboard! : n;
    }

    public async Task<ReportOptions> OptionsAsync(CancellationToken ct)
    {
        await DemandViewerAsync(ct);
        var canSchedule = await entitlements.IsEnabledAsync(Capabilities.AnalyticsAdvanced, ct);
        IReadOnlyList<WorkspaceUnit> units = [];
        if (canSchedule)
        {
            // A schedule may go to a unit the viewer covers: for the whole tenant, every unit with members and the units above them.
            var coverage = await authz.CoverageAsync(Permissions.AnalyticsView, ct);
            var ids = coverage.WholeTenant
                ? (await organization.GetPathsAsync((await organization.ListActiveMembershipsAsync(ct)).Select(m => m.OrgUnitId).Distinct(), ct)).Values
                    .SelectMany(p => p).Distinct().ToList()
                : coverage.Units.ToList();
            var names = ids.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(ids, ct);
            units = names.Select(n => new WorkspaceUnit(n.Key, n.Value))
                .OrderBy(u => u.Names.GetValueOrDefault("en") ?? u.Names.Values.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new ReportOptions(await entitlements.IsEnabledAsync(Capabilities.AnalyticsExport, ct), canSchedule, units, canSchedule);
    }

    public async Task<ReportList> ListAsync(CancellationToken ct)
    {
        var user = await DemandViewerAsync(ct);
        var reports = await session.QueryAsync(
            $"""
            select {SummaryColumns} from analytics.report where recipient_user_id = @user order by created_at desc limit {MaxReportsListed}
            """, new { user }, r => Summary(r, user), ct);
        var schedules = await SchedulesAsync(user, ct);
        return new ReportList(await entitlements.IsEnabledAsync(Capabilities.AnalyticsExport, ct), await entitlements.IsEnabledAsync(Capabilities.AnalyticsAdvanced, ct),
            reports, schedules);
    }

    private const string SummaryColumns = "id, name, definition->>'dashboard', schedule_id, owner_user_id, format, status, period_from, period_to, created_at, done_at";

    private static ReportSummary Summary(System.Data.Common.DbDataReader r, Guid user) => new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetGuid(3), r.GetGuid(4) == user, r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateOnly>(8), r.GetFieldValue<DateTimeOffset>(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10));

    private async Task<IReadOnlyList<ReportScheduleView>> SchedulesAsync(Guid owner, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select id, name, definition::text, cadence, recipient_unit_id, next_run_at, last_run_at, created_at
            from analytics.report_schedule where owner_user_id = @owner order by created_at
            """, new { owner }, r => (Id: r.GetGuid(0), Name: r.GetString(1), Definition: r.GetString(2), Cadence: r.GetString(3), Unit: r.IsDBNull(4) ? (Guid?)null : r.GetGuid(4),
                Next: r.GetFieldValue<DateTimeOffset>(5), Last: r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6), Created: r.GetFieldValue<DateTimeOffset>(7)), ct);
        var units = rows.Select(r => r.Unit).OfType<Guid>().Distinct().ToList();
        var names = units.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(units, ct);
        return rows.Select(r => new ReportScheduleView(r.Id, r.Name, JsonSerializer.Deserialize<ReportDefinition>(r.Definition, Json)!, r.Cadence, r.Unit,
            r.Unit is { } u ? names.GetValueOrDefault(u) : null, r.Next, r.Last, r.Created)).ToList();
    }

    public async Task<ReportView> GetAsync(Guid id, CancellationToken ct)
    {
        var user = await DemandViewerAsync(ct);
        var rows = await session.QueryAsync(
            $"select {SummaryColumns}, definition::text, result::text from analytics.report where id = @id and recipient_user_id = @user",
            new { id, user }, r => (Summary: Summary(r, user), Definition: r.GetString(11), Result: r.IsDBNull(12) ? null : r.GetString(12)), ct);
        if (rows.Count == 0)
        {
            throw AppException.NotFound();
        }
        var row = rows[0];
        JsonElement? result = row.Result is null ? null : JsonDocument.Parse(row.Result).RootElement.Clone();
        var tables = result is { } r ? ReportTables.From(r) : [];
        var canDownload = row.Summary.Status == ReportStatuses.Ready && await entitlements.IsEnabledAsync(Capabilities.AnalyticsExport, ct);
        return new ReportView(row.Summary, JsonSerializer.Deserialize<ReportDefinition>(row.Definition, Json)!, canDownload, result, tables);
    }

    /// <summary>The file of a ready report, for its recipient only, with <c>analytics.export</c>. Every download is audited.</summary>
    public async Task<ReportFile> FileAsync(Guid id, string? format, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsExport, ct);
        var view = await GetAsync(id, ct);
        var chosen = format ?? view.Report.Format ?? ReportFormats.Csv;
        if (chosen is not (ReportFormats.Csv or ReportFormats.Xlsx))
        {
            throw AppException.Validation("format", "validation.analytics.format");
        }
        if (view.Report.Status != ReportStatuses.Ready)
        {
            throw AppException.Conflict("conflict.analytics.report_not_ready");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AnalyticsExportDownloaded, "analytics_report", id.ToString(),
            Changes: new { dashboard = view.Definition.Dashboard, format = chosen }), ct);
        var stamp = view.Report.CreatedAt.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var file = $"analytics-{view.Definition.Dashboard}-{stamp}.{chosen}";
        return chosen == ReportFormats.Xlsx
            ? new ReportFile(file, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ReportTables.Xlsx(view.Tables))
            : new ReportFile(file, "text/csv; charset=utf-8", ReportTables.Csv(view.Tables));
    }

    /// <summary>Queues an export of one dashboard for the person asking. The dashboard is run once now so a refusal is immediate.</summary>
    public async Task<ReportSummary> ExportAsync(ExportRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsExport, ct);
        var user = await DemandViewerAsync(ct);
        var d = ReportDefinitions.Validate(request.Definition);
        var format = request.Format ?? ReportFormats.Csv;
        if (format is not (ReportFormats.Csv or ReportFormats.Xlsx))
        {
            throw AppException.Validation("format", "validation.analytics.format");
        }
        await definitions.RunAsync(d, null, null, ct);
        var name = Name(request.Name, d);
        var id = Guid.NewGuid();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into analytics.report (tenant_id, id, recipient_user_id, schedule_id, owner_user_id, name, definition, period_from, period_to, format, status, created_at)
            values (platform.current_tenant_id(), @id, @user, null, @user, @name, @definition::jsonb, @from, @to, @format, 'queued', @now)
            """, new { id, user, name, definition = JsonSerializer.Serialize(d, Json), from = d.Filters?.From, to = d.Filters?.To, format, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AnalyticsExportRequested, "analytics_report", id.ToString(),
            Changes: new { dashboard = d.Dashboard, target = d.Target, metric = d.Metric, filters = d.Filters, groupBy = d.GroupBy, format }), ct);
        return new ReportSummary(id, name, d.Dashboard!, null, true, format, ReportStatuses.Queued, d.Filters?.From, d.Filters?.To, now, null);
    }

    public async Task<ReportScheduleView> ScheduleAsync(ScheduleRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsAdvanced, ct);
        var user = await DemandViewerAsync(ct);
        var d = ReportDefinitions.Validate(request.Definition);
        if (d.Filters is { From: not null } or { To: not null })
        {
            // A schedule always sends the last week or month.
            throw AppException.Validation("definition.filters.from", "validation.analytics.schedule_dates");
        }
        var cadence = request.Cadence ?? "";
        if (cadence is not (ReportCadences.Weekly or ReportCadences.Monthly))
        {
            throw AppException.Validation("cadence", "validation.analytics.cadence");
        }
        if (request.RecipientUnitId is { } unit)
        {
            // A group is a unit the owner's analytics scope covers, so nobody can send reports to people outside it.
            var coverage = await authz.CoverageAsync(Permissions.AnalyticsView, ct);
            if (!(await organization.GetNamesAsync([unit], ct)).ContainsKey(unit))
            {
                throw AppException.Validation("recipientUnitId", "validation.analytics.filter");
            }
            if (!coverage.Covers(unit))
            {
                throw AppException.Denied();
            }
        }
        var count = await session.ScalarAsync<long>("select count(*) from analytics.report_schedule where owner_user_id = @user", new { user }, ct);
        if (count >= MaxSchedulesPerOwner)
        {
            throw AppException.Conflict("conflict.analytics.too_many_schedules");
        }
        // The owner must be able to open the dashboard now.
        var now = clock.UtcNow;
        var (from, to) = ReportDefinitions.Period(cadence, now);
        await definitions.RunAsync(d, ReportDashboards.Dated.Contains(d.Dashboard!) ? from : null, ReportDashboards.Dated.Contains(d.Dashboard!) ? to : null, ct);
        var name = Name(request.Name, d);
        var id = Guid.NewGuid();
        await session.ExecAsync(
            """
            insert into analytics.report_schedule (tenant_id, id, owner_user_id, name, definition, cadence, recipient_unit_id, next_run_at, created_at)
            values (platform.current_tenant_id(), @id, @user, @name, @definition::jsonb, @cadence, @unit, @now, @now)
            """, new { id, user, name, definition = JsonSerializer.Serialize(d, Json), cadence, unit = request.RecipientUnitId, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AnalyticsScheduleCreated, "analytics_schedule", id.ToString(),
            Changes: new { dashboard = d.Dashboard, target = d.Target, metric = d.Metric, filters = d.Filters, groupBy = d.GroupBy, cadence, recipients = request.RecipientUnitId?.ToString() ?? "owner" }), ct);
        return (await SchedulesAsync(user, ct)).First(s => s.Id == id);
    }

    public async Task DeleteScheduleAsync(Guid id, CancellationToken ct)
    {
        var user = await DemandViewerAsync(ct);
        var deleted = await session.ExecAsync("delete from analytics.report_schedule where id = @id and owner_user_id = @user", new { id, user }, ct);
        if (deleted == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AnalyticsScheduleChanged, "analytics_schedule", id.ToString(), Changes: new { deleted = true }), ct);
    }
}

/// <summary>
/// The background side of reports (FR-011, FR-012): turns due schedules into one queued report per recipient, then
/// computes each queued report in its own scope as its recipient. A recipient refused by the dashboard (no permission,
/// a scope they do not cover, or a package without the dashboard) is skipped and the owner told. Ready reports tell the
/// recipient. Reports are kept <see cref="KeepDays"/> days.
/// </summary>
public sealed class ReportRunner(IServiceScopeFactory scopes, IDbSession session, ITenantContext context, INotificationSender notifications)
{
    public const int KeepDays = 90;
    public const string ReadyTemplate = "analytics.report_ready";
    public const string SkippedTemplate = "analytics.report_skipped";
    public const string FailedTemplate = "analytics.report_failed";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record RunResult(int Expanded, int Ready, int Skipped, int Failed);

    public async Task<RunResult> RunAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        int expanded;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
            accessor.SetTenant(tenant);
            accessor.SetActor(ActorType.System, null);
            var db = scope.ServiceProvider.GetRequiredService<IDbSession>();
            expanded = await ExpandAsync(db, scope.ServiceProvider.GetRequiredService<IOrganizationHierarchy>(),
                scope.ServiceProvider.GetRequiredService<ITenantMemberships>(), now, limit, ct);
            await db.CommitAsync(ct);
        }
        var queued = await session.QueryAsync(
            "select id from analytics.report where status = 'queued' order by created_at limit @limit", new { limit }, r => r.GetGuid(0), ct);
        int ready = 0, skipped = 0, failed = 0;
        var skippedBySchedule = new Dictionary<(Guid Owner, Guid? Schedule, string Name), int>();
        foreach (var id in queued)
        {
            var (status, report) = await ComputeAsync(tenant, id, now, ct);
            switch (status)
            {
                case ReportStatuses.Ready: ready++; break;
                case ReportStatuses.Failed: failed++; break;
                case ReportStatuses.Skipped when report is { } r:
                    skipped++;
                    var key = (r.Owner, r.Schedule, r.Name);
                    skippedBySchedule[key] = skippedBySchedule.GetValueOrDefault(key) + 1;
                    break;
            }
        }
        foreach (var ((owner, schedule, name), count) in skippedBySchedule)
        {
            await notifications.SendAsync(new NotificationRequest([owner], NotificationCategories.System, SkippedTemplate, "/analytics/reports",
                new Dictionary<string, string> { ["name"] = name, ["count"] = count.ToString(CultureInfo.InvariantCulture) },
                DedupeKey: $"analytics.report_skipped:{schedule?.ToString("N") ?? owner.ToString("N")}:{now:yyyyMMddHHmm}"), ct);
        }
        await session.ExecAsync("delete from analytics.report where created_at < @cutoff", new { cutoff = now.AddDays(-KeepDays) }, ct);
        await session.CommitAsync(ct);
        return new RunResult(expanded, ready, skipped, failed);
    }

    /// <summary>One report row per recipient of each due schedule: the owner, or the active members of the unit.</summary>
    private static async Task<int> ExpandAsync(IDbSession db, IOrganizationHierarchy organization, ITenantMemberships memberships, DateTimeOffset now, int limit,
        CancellationToken ct)
    {
        var due = await db.QueryAsync(
            """
            select id, owner_user_id, name, definition::text, cadence, recipient_unit_id, next_run_at from analytics.report_schedule
            where next_run_at <= @now order by next_run_at limit @limit for update skip locked
            """, new { now, limit }, r => (Id: r.GetGuid(0), Owner: r.GetGuid(1), Name: r.GetString(2), Definition: r.GetString(3), Cadence: r.GetString(4),
                Unit: r.IsDBNull(5) ? (Guid?)null : r.GetGuid(5), Next: r.GetFieldValue<DateTimeOffset>(6)), ct);
        var count = 0;
        foreach (var s in due)
        {
            var definition = JsonSerializer.Deserialize<ReportDefinition>(s.Definition, Json)!;
            var dated = ReportDashboards.Dated.Contains(definition.Dashboard ?? "");
            var (from, to) = ReportDefinitions.Period(s.Cadence, now);
            IReadOnlyCollection<Guid> recipients = [s.Owner];
            if (s.Unit is { } unit)
            {
                var members = await organization.GetMemberUserIdsAsync([unit], ct);
                var active = await memberships.GetManyAsync(members.ToList(), ct);
                recipients = active.Where(m => m.Value.Status == MembershipStatus.Active).Select(m => m.Key).ToList();
            }
            foreach (var recipient in recipients)
            {
                await db.ExecAsync(
                    """
                    insert into analytics.report (tenant_id, id, recipient_user_id, schedule_id, owner_user_id, name, definition, period_from, period_to, format, status, created_at)
                    values (platform.current_tenant_id(), @id, @recipient, @schedule, @owner, @name, @definition::jsonb, @from, @to, null, 'queued', @now)
                    """, new { id = Guid.NewGuid(), recipient, schedule = s.Id, owner = s.Owner, name = s.Name, definition = s.Definition,
                        from = dated ? from : (DateOnly?)null, to = dated ? to : (DateOnly?)null, now }, ct);
                count++;
            }
            // Catch up once after downtime, then keep the cadence from now.
            var next = ReportDefinitions.Next(s.Cadence, s.Next);
            await db.ExecAsync("update analytics.report_schedule set next_run_at = @next, last_run_at = @now where id = @id",
                new { id = s.Id, next = next <= now ? ReportDefinitions.Next(s.Cadence, now) : next, now }, ct);
        }
        return count;
    }

    private sealed record Claimed(Guid Recipient, Guid Owner, Guid? Schedule, string Name, ReportDefinition Definition, DateOnly? From, DateOnly? To, string? Format);

    /// <summary>Runs one report as its recipient, in a scope of its own, and records the answer or why there is none.</summary>
    private async Task<(string Status, Claimed? Report)> ComputeAsync(Guid tenant, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var accessor = sp.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(ActorType.System, null);
        var db = sp.GetRequiredService<IDbSession>();
        var rows = await db.QueryAsync(
            """
            select recipient_user_id, owner_user_id, schedule_id, name, definition::text, period_from, period_to, format from analytics.report
            where id = @id and status = 'queued' for update skip locked
            """, new { id }, r => new Claimed(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3),
                JsonSerializer.Deserialize<ReportDefinition>(r.GetString(4), Json)!, r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), r.IsDBNull(7) ? null : r.GetString(7)), ct);
        if (rows.Count == 0)
        {
            return ("", null);
        }
        var report = rows[0];
        var member = await sp.GetRequiredService<ITenantMemberships>().GetAsync(report.Recipient, ct);
        string status;
        string? result = null;
        if (member is not { Status: MembershipStatus.Active })
        {
            status = ReportStatuses.Skipped;
        }
        else
        {
            // Permissions are those of the recipient now (FR-011).
            accessor.SetActor(ActorType.User, report.Recipient);
            try
            {
                var answer = await sp.GetRequiredService<ReportDefinitions>().RunAsync(report.Definition, report.From, report.To, ct);
                result = answer.GetRawText();
                status = ReportStatuses.Ready;
            }
            catch (AppException ex) when (ex.Status is 403 or 404)
            {
                status = ReportStatuses.Skipped;
            }
            catch (AppException)
            {
                status = ReportStatuses.Failed;
            }
            accessor.SetActor(ActorType.System, null);
        }
        await db.ExecAsync("update analytics.report set status = @status, result = @result::jsonb, done_at = @now where id = @id",
            new { id, status, result, now }, ct);
        var notifications = sp.GetRequiredService<INotificationSender>();
        var audit = sp.GetRequiredService<IAuditWriter>();
        if (status == ReportStatuses.Ready)
        {
            await notifications.SendAsync(new NotificationRequest([report.Recipient], NotificationCategories.System, ReadyTemplate, AnalyticsReports.Link(id),
                new Dictionary<string, string> { ["name"] = report.Name }, DedupeKey: "analytics.report_ready:" + id.ToString("N")), ct);
        }
        else if (status == ReportStatuses.Failed && report.Schedule is null)
        {
            await notifications.SendAsync(new NotificationRequest([report.Recipient], NotificationCategories.System, FailedTemplate, "/analytics/reports",
                new Dictionary<string, string> { ["name"] = report.Name }, DedupeKey: "analytics.report_failed:" + id.ToString("N")), ct);
        }
        if (report.Schedule is null)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AnalyticsExportCompleted, "analytics_report", id.ToString(),
                Changes: new { dashboard = report.Definition.Dashboard, format = report.Format, status }), ct);
        }
        await db.CommitAsync(ct);
        return (status, report);
    }
}

/// <summary>
/// Sends due schedules and makes queued exports for every active tenant, every <see cref="Tick"/>, so an export is
/// ready soon after it is asked for. Runs in the workers host, and in process for development when configured.
/// </summary>
public sealed class ReportWorker(IServiceScopeFactory scopes, IClock clock, ILogger<ReportWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Analytics:Reports:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

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
            logger.LogWarning(ex, "Analytics reports could not list tenants");
            return;
        }
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                await scope.ServiceProvider.GetRequiredService<ReportRunner>().RunAsync(clock.UtcNow, 200, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Analytics reports failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
