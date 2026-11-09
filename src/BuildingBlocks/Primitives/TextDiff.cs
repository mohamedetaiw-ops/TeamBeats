using System.Text.RegularExpressions;

namespace TeamBeats.Primitives;

/// <summary>A run of text in a compare: kept, added or removed (spec 009 US4 scenario 1, spec 011 US2 scenario 4).</summary>
public sealed record DiffSegment(string Op, string Text)
{
    public const string Equal = "equal";
    public const string Added = "added";
    public const string Removed = "removed";
}

/// <summary>
/// Word-level differences between two texts by longest common subsequence. Words and the spaces between them are
/// separate tokens so the runs join back to the exact texts. Long texts fall back to lines, and very long ones to a
/// single replacement, so a compare never takes unbounded memory.
/// </summary>
public static partial class TextDiff
{
    public const long MaxCells = 4_000_000;

    [GeneratedRegex(@"\s+|[\p{L}\p{M}\p{N}_]+|[^\s\p{L}\p{M}\p{N}_]", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokens();

    public static IReadOnlyList<DiffSegment> Words(string? before, string? after)
    {
        before ??= string.Empty;
        after ??= string.Empty;
        if (before == after)
        {
            return before.Length == 0 ? [] : [new DiffSegment(DiffSegment.Equal, before)];
        }
        var a = WordTokens().Matches(before).Select(m => m.Value).ToArray();
        var b = WordTokens().Matches(after).Select(m => m.Value).ToArray();
        if ((long)a.Length * b.Length > MaxCells)
        {
            a = Lines(before);
            b = Lines(after);
        }
        if ((long)a.Length * b.Length > MaxCells)
        {
            return Merge([new(DiffSegment.Removed, before), new(DiffSegment.Added, after)]);
        }
        return Merge(Lcs(a, b));
    }

    private static string[] Lines(string text) => Regex.Split(text, "(?<=\n)").Where(l => l.Length > 0).ToArray();

    private static List<DiffSegment> Lcs(string[] a, string[] b)
    {
        // Trim the shared start and end first; most edits touch a small part of the text.
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
        {
            start++;
        }
        var endA = a.Length;
        var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }
        var result = new List<DiffSegment>();
        for (var i = 0; i < start; i++)
        {
            result.Add(new(DiffSegment.Equal, a[i]));
        }
        var n = endA - start;
        var m = endB - start;
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] = a[start + i] == b[start + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[start + x] == b[start + y])
            {
                result.Add(new(DiffSegment.Equal, a[start + x]));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                result.Add(new(DiffSegment.Removed, a[start + x++]));
            }
            else
            {
                result.Add(new(DiffSegment.Added, b[start + y++]));
            }
        }
        while (x < n)
        {
            result.Add(new(DiffSegment.Removed, a[start + x++]));
        }
        while (y < m)
        {
            result.Add(new(DiffSegment.Added, b[start + y++]));
        }
        for (var i = endA; i < a.Length; i++)
        {
            result.Add(new(DiffSegment.Equal, a[i]));
        }
        return result;
    }

    private static List<DiffSegment> Merge(List<DiffSegment> segments)
    {
        var merged = new List<DiffSegment>();
        foreach (var s in segments.Where(s => s.Text.Length > 0))
        {
            if (merged.Count > 0 && merged[^1].Op == s.Op)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + s.Text };
            }
            else
            {
                merged.Add(s);
            }
        }
        return merged;
    }
}
