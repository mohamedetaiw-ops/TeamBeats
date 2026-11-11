using System.Text;
using TeamBeats.Modules.Integrations.ClockData;
using TeamBeats.Modules.Integrations.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 017 FR-020: reading clock data CSV with a column mapping, and the rows that cannot be read.</summary>
public sealed class ClockCsvTests
{
    private static ClockCsvSettings Settings(bool header = true, string delimiter = ",", ClockColumns? columns = null, string? format = null, string zone = "Asia/Riyadh") =>
        new(delimiter, header, columns ?? new ClockColumns("Emp", "Id", "Time", null, null, "Dir", "Device"), format, null, null, zone, ["IN", "I"], ["OUT", "O"]);

    private static ClockParse Parse(string text, ClockCsvSettings settings) => ClockCsv.Parse(Encoding.UTF8.GetBytes(text), settings);

    [Fact]
    public void Rows_map_by_header_and_local_times_use_the_source_time_zone()
    {
        var csv = "﻿Emp,Id,Time,Dir,Device\r\nE100,p1,2026-11-09 08:01:00,in,Gate A\r\nE200,p2,2026-11-09T17:00:00Z,OUT,\"Gate, B\"\r\n";
        var parse = Parse(csv, Settings());
        Assert.Null(parse.FileError);
        Assert.Equal(2, parse.RowCount);
        Assert.Empty(parse.Problems);
        var first = parse.Rows[0];
        Assert.Equal(("E100", "p1", ClockDirections.In, "Gate A"), (first.EmployeeNumber, first.PunchId, first.Direction, first.Device));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 5, 1, 0, TimeSpan.Zero), first.At);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 17, 0, 0, TimeSpan.Zero), parse.Rows[1].At);
        Assert.Equal("Gate, B", parse.Rows[1].Device);
        Assert.Equal(3, parse.Rows[1].Line);
    }

    [Fact]
    public void Columns_by_number_with_separate_date_and_time_and_a_derived_punch_id()
    {
        var settings = new ClockCsvSettings(";", false, new ClockColumns("2", null, null, "3", "4", null, null), null, "dd/MM/yyyy", "HH:mm", "UTC", [], []);
        var parse = Parse("x;E7;09/11/2026;07:45\nx;E7;09/11/2026;07:45\n", settings);
        Assert.Equal(2, parse.Rows.Count);
        Assert.Equal(ClockDirections.Unknown, parse.Rows[0].Direction);
        Assert.StartsWith("h:", parse.Rows[0].PunchId, StringComparison.Ordinal);
        // The same punch twice gets the same ID, so it is stored once.
        Assert.Equal(parse.Rows[0].PunchId, parse.Rows[1].PunchId);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 7, 45, 0, TimeSpan.Zero), parse.Rows[0].At);
    }

    [Fact]
    public void Bad_rows_are_listed_with_their_line_and_reason()
    {
        var csv = "Emp,Id,Time,Dir,Device\n,p1,2026-11-09 08:00,IN,\nE1,p2,not a time,IN,\nE2,p3,2026-11-09 08:00,sideways,\n\nE3,p4,2026-11-09 08:00,O,\n";
        var parse = Parse(csv, Settings());
        Assert.Single(parse.Rows);
        Assert.Equal(
            [new ClockRowProblem(2, null, ClockRowReasons.MissingEmployee), new ClockRowProblem(3, "E1", ClockRowReasons.BadTime), new ClockRowProblem(4, "E2", ClockRowReasons.BadDirection)],
            parse.Problems);
        Assert.Equal(ClockDirections.Out, parse.Rows[0].Direction);
        Assert.Equal(6, parse.Rows[0].Line);
    }

    [Fact]
    public void A_quoted_field_may_span_lines_and_line_numbers_follow_the_file()
    {
        var parse = Parse("Emp,Id,Time,Dir,Device\nE1,p1,2026-11-09 08:00,IN,\"two\nlines\"\nE2,p2,2026-11-09 08:00,IN,\n", Settings());
        Assert.Equal("two\nlines", parse.Rows[0].Device);
        Assert.Equal(4, parse.Rows[1].Line);
    }

    [Fact]
    public void A_mapped_column_missing_from_the_header_refuses_the_file()
    {
        var parse = Parse("Employee,Id,Time\nE1,p1,2026-11-09 08:00\n", Settings());
        Assert.Equal("integrations.clock.file.missing_column", parse.FileError);
        Assert.Empty(parse.Rows);
    }

    [Fact]
    public void An_exact_format_and_a_time_skipped_by_daylight_saving()
    {
        var settings = Settings(columns: new ClockColumns("Emp", null, "Time", null, null, null, null), format: "yyyyMMddHHmm", zone: "Europe/Berlin");
        var parse = Parse("Emp,Time\nE1,202603290230\nE1,202603291030\n", settings);
        Assert.Equal(new ClockRowProblem(2, "E1", ClockRowReasons.BadTime), Assert.Single(parse.Problems));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 8, 30, 0, TimeSpan.Zero), Assert.Single(parse.Rows).At);
    }

    [Fact]
    public void Overlong_fields_are_refused_per_row()
    {
        var parse = Parse($"Emp,Id,Time,Dir,Device\nE1,p1,2026-11-09 08:00,IN,{new string('x', 500)}\n", Settings());
        Assert.Equal(ClockRowReasons.TooLong, Assert.Single(parse.Problems).Reason);
    }

    [Theory]
    [InlineData("*.csv", "punches_2026-11-09.CSV", true)]
    [InlineData("*.csv", "punches.csv.part", false)]
    [InlineData("clock_??.txt", "clock_01.txt", true)]
    [InlineData("clock_??.txt", "clock_1.txt", false)]
    public void File_patterns(string pattern, string name, bool match) => Assert.Equal(match, ClockImportPack.FileMatcher(pattern).IsMatch(name));
}
