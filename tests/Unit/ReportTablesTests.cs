using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TeamBeats.Modules.Analytics;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 T016 (FR-012): reports turn a dashboard answer into tables, CSV and XLSX without computing anything.</summary>
public sealed class ReportTablesTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Top_values_go_to_the_summary_and_each_list_becomes_a_table_with_nested_lists_pointing_at_their_row()
    {
        var tables = ReportTables.From(Parse("""
            {"metric":"active_people","total":12,"suppressed":false,"names":{"en":"A","ar":"أ"},
             "weeks":[{"week":"2026-11-09","value":12},{"week":"2026-11-16","value":null}],
             "scopes":[{"unit":"u1","templates":[{"key":"T01","pages":2}]},{"unit":"u2","templates":[{"key":"T02","pages":1},{"key":"T03","pages":4}]}],
             "tags":["x","y"]}
            """)).ToDictionary(t => t.Key);

        var summary = tables[ReportTables.Summary];
        Assert.Equal(["metric", "total", "suppressed", "names.en", "names.ar", "tags"], summary.Columns);
        var row = Assert.Single(summary.Rows);
        Assert.Equal(12, row[1]!.Value.GetInt32());
        Assert.Equal("x; y", row[5]!.Value.GetString());

        var weeks = tables["weeks"];
        Assert.Equal(["week", "value"], weeks.Columns);
        Assert.Null(weeks.Rows[1][1]); // A suppressed week stays empty.

        var nested = tables["scopes.templates"];
        Assert.Equal([ReportTables.RowColumn, "key", "pages"], nested.Columns);
        Assert.Equal([1, 2, 2], nested.Rows.Select(r => r[0]!.Value.GetInt32()));
    }

    [Fact]
    public void Csv_escapes_formulas_quotes_and_commas_but_keeps_negative_numbers()
    {
        Assert.Equal("'=HYPERLINK(1)", ReportTables.Cell("=HYPERLINK(1)"));
        Assert.Equal("'@cmd", ReportTables.Cell("@cmd"));
        Assert.Equal("-3.5", ReportTables.Cell("-3.5"));
        Assert.Equal("\"a, \"\"b\"\"\"", ReportTables.Cell("a, \"b\""));

        var csv = Encoding.UTF8.GetString(ReportTables.Csv(ReportTables.From(Parse("""{"name":"=1+1","rows":[{"a":1}]}"""))));
        Assert.Contains("# summary\r\nname\r\n'=1+1\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("# rows\r\na\r\n1\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Xlsx_has_one_sheet_per_table_with_numbers_as_numbers_and_text_never_a_formula()
    {
        var bytes = ReportTables.Xlsx(ReportTables.From(Parse("""{"name":"=1+1 <b>","weeks":[{"week":"2026-11-09","value":12}]}""")));
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }
        Assert.Contains("""<sheet name="summary" sheetId="1" r:id="rId1"/>""", Read("xl/workbook.xml"), StringComparison.Ordinal);
        Assert.Contains("""<sheet name="weeks" sheetId="2" r:id="rId2"/>""", Read("xl/workbook.xml"), StringComparison.Ordinal);
        var summary = Read("xl/worksheets/sheet1.xml");
        Assert.Contains("=1+1 &lt;b&gt;", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", summary, StringComparison.Ordinal);
        Assert.Contains("""<c r="B2"><v>12</v></c>""", Read("xl/worksheets/sheet2.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Sheet_names_and_columns_follow_the_spreadsheet_rules()
    {
        Assert.Equal("A", ReportTables.Column(0));
        Assert.Equal("Z", ReportTables.Column(25));
        Assert.Equal("AA", ReportTables.Column(26));
        Assert.Equal("ZZ", ReportTables.Column(701));
        var names = ReportTables.SheetNames(["a/b", "A_B", new string('x', 40), new string('x', 40)]);
        Assert.Equal("a_b", names[0]);
        Assert.Equal("A_B~2", names[1]);
        Assert.All(names, n => Assert.True(n.Length <= 31));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_scheduled_period_is_the_week_or_month_before_the_send_day()
    {
        var at = new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal((new DateOnly(2026, 2, 22), new DateOnly(2026, 2, 28)), ReportDefinitions.Period(ReportCadences.Weekly, at));
        Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), ReportDefinitions.Period(ReportCadences.Monthly, at));
        Assert.Equal(at.AddDays(7), ReportDefinitions.Next(ReportCadences.Weekly, at));
        Assert.Equal(at.AddMonths(1), ReportDefinitions.Next(ReportCadences.Monthly, at));
    }
}
