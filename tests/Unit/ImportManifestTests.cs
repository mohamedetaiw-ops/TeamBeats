using TeamBeats.Modules.Documents;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 011 FR-022: the bulk import manifest is read as RFC 4180 CSV and refused whole when its shape is wrong.</summary>
public sealed class ImportManifestTests
{
    private static string Key(Action act) =>
        Assert.Throws<AppException>(act).Errors!.Values.SelectMany(v => v).Single();

    [Fact]
    public void Quoted_fields_crlf_and_a_byte_order_mark_are_read()
    {
        var rows = ImportManifest.Parse("﻿path,name,field:site\r\n\"a, b.txt\",\"Say \"\"hi\"\"\",Dubai\r\nfolder\\c.txt,,\r\n\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("a, b.txt", rows[0].Path);
        Assert.Equal("Say \"hi\"", rows[0].Values["name"]);
        Assert.Equal("Dubai", rows[0].Values["field:site"]);
        Assert.Equal("folder/c.txt", rows[1].Path);
        Assert.Empty(rows[1].Values);
        Assert.Equal(2, rows[1].Number);
    }

    [Fact]
    public void A_line_break_inside_quotes_stays_in_the_field()
    {
        var row = Assert.Single(ImportManifest.Parse("path,name\na.txt,\"two\nlines\"\n"));
        Assert.Equal("two\nlines", row.Values["name"]);
    }

    [Theory]
    [InlineData("name\na.txt\n", "validation.documents.import_columns")]
    [InlineData("path,owner\na.txt,x\n", "validation.documents.import_columns")]
    [InlineData("path,path\na.txt,b.txt\n", "validation.documents.import_columns")]
    [InlineData("path,field:\na.txt,x\n", "validation.documents.import_columns")]
    [InlineData("path\n", "validation.documents.import_manifest")]
    [InlineData("", "validation.documents.import_manifest")]
    [InlineData("path,name\na.txt\n", "validation.documents.import_manifest")]
    [InlineData("path\n\"a.txt\n", "validation.documents.import_manifest")]
    [InlineData("path,name\n,x\n", "validation.documents.import_paths")]
    [InlineData("path\na.txt\nA/../a.txt\n", "validation.documents.import_paths")]
    [InlineData("path\na.txt\n/a.txt\n", "validation.documents.import_paths")]
    public void Manifests_with_the_wrong_shape_are_refused(string text, string key) =>
        Assert.Equal(key, Key(() => ImportManifest.Parse(text)));

    [Fact]
    public void More_than_the_row_limit_is_refused()
    {
        var text = "path\n" + string.Join('\n', Enumerable.Range(0, ImportManifest.MaxRows + 1).Select(i => $"f{i}.txt"));
        Assert.Equal("validation.documents.import_rows", Key(() => ImportManifest.Parse(text)));
    }

    [Fact]
    public void Folders_split_on_slashes_and_refuse_climbing()
    {
        Assert.Equal(["Policies", "HR"], ImportManifest.FolderPath(" Policies / HR/ "));
        Assert.Empty(ImportManifest.FolderPath(null)!);
        Assert.Null(ImportManifest.FolderPath("Policies/../HR"));
    }

    [Fact]
    public void Dates_are_iso_in_the_past_and_utc_when_no_offset_is_given()
    {
        var now = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2019, 3, 1, 0, 0, 0, TimeSpan.Zero), ImportManifest.Date("2019-03-01", now));
        Assert.Equal(new DateTimeOffset(2019, 3, 1, 8, 0, 0, TimeSpan.Zero), ImportManifest.Date("2019-03-01T10:00:00+02:00", now));
        Assert.Null(ImportManifest.Date(" ", now));
        Assert.Equal("validation.documents.import_date", Key(() => ImportManifest.Date("2027-01-01", now)));
        Assert.Equal("validation.documents.import_date", Key(() => ImportManifest.Date("1 March", now)));
    }

    [Fact]
    public void Report_fields_are_quoted_and_never_read_as_formulas()
    {
        Assert.Equal("\"a \"\"b\"\"\"", DocumentImports.Quote("a \"b\""));
        Assert.Equal("\"'=SUM(A1)\"", DocumentImports.Quote("=SUM(A1)"));
    }
}
