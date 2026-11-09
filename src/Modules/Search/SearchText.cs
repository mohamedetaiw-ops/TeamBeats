using System.Globalization;
using System.Text;

namespace TeamBeats.Modules.Search;

/// <summary>
/// Text handling for the index and for queries (spec 012 FR-005). Arabic letter variants are folded (alef forms, alef
/// maksura, ta marbuta, hamza carriers), diacritics, tatweel and Latin accents are removed and letters are lower-cased,
/// so "إجازة" and "اجازه" meet. English stemming is left to PostgreSQL's <c>english</c> configuration.
/// </summary>
public static class SearchText
{
    public const int MaxQueryLength = 200;
    public const int MaxTerms = 12;

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var raw in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark || raw == 'ـ')
            {
                continue;
            }
            var c = raw switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                _ => raw,
            };
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>The words of a normalized text: runs of letters and digits, everything else separates.</summary>
    public static IReadOnlyList<string> Words(string normalized)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
        {
            words.Add(sb.ToString());
        }
        return words;
    }

    /// <summary>The distinct words of a query, at most <see cref="MaxTerms"/>, in the order typed.</summary>
    public static IReadOnlyList<string> Terms(string? query) => [.. Words(Normalize(query)).Distinct(StringComparer.Ordinal).Take(MaxTerms)];

    /// <summary>The words of a query as one normalized phrase, the key best bets are matched on (US4 scenario 1).</summary>
    public static string QueryKey(string? query) => string.Join(' ', Terms(query));

    /// <summary>The language a query is treated as for locale specific best bets and synonyms: Arabic by its letters, else English.</summary>
    public static string QueryLanguage(string? query) => IsArabic(query) ? "ar" : "en";

    /// <summary>
    /// Each query term with the other words of every synonym set that holds it (US4 scenario 2), as the groups of
    /// <see cref="TsQuery"/>: a term matches when any word of its group does. The typed term stays first.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Expand(IReadOnlyList<string> terms, IEnumerable<IReadOnlyCollection<string>> synonymSets)
    {
        var sets = synonymSets.ToList();
        return [.. terms.Select(t => (IReadOnlyList<string>)[t, .. sets.Where(s => s.Contains(t)).SelectMany(s => s).Where(w => w != t).Distinct(StringComparer.Ordinal)])];
    }

    public static bool IsArabic(string? text) => text is not null && text.Any(c => c is >= '؀' and <= 'ۿ');

    /// <summary>
    /// A <c>to_tsquery</c> text: every group must match (AND), any word inside a group may (OR, for synonyms). Words are
    /// quoted lexemes, so query operators typed by a person are never interpreted. <paramref name="prefix"/> makes the
    /// last group match word beginnings, for type-ahead.
    /// </summary>
    public static string TsQuery(IReadOnlyList<IReadOnlyList<string>> groups, bool all, bool prefix = false)
    {
        var parts = new List<string>();
        for (var i = 0; i < groups.Count; i++)
        {
            var last = prefix && i == groups.Count - 1;
            var words = groups[i].Where(w => w.Length > 0).Select(w => Lexeme(w) + (last ? ":*" : string.Empty)).ToList();
            if (words.Count > 0)
            {
                parts.Add(words.Count == 1 ? words[0] : "(" + string.Join(" | ", words) + ")");
            }
        }
        return string.Join(all ? " & " : " | ", parts);
    }

    private static string Lexeme(string word) => "'" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Passages of about <paramref name="size"/> characters with <paramref name="overlap"/> characters shared between
    /// neighbours (spec 012 plan: 400 tokens with 15% overlap, about 1,600 and 240 characters), cut at spaces.
    /// </summary>
    public static IReadOnlyList<string> Passages(string? text, int size = 1600, int overlap = 240, int max = 200)
    {
        var clean = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= size)
        {
            return clean.Length == 0 ? [] : [clean];
        }
        var passages = new List<string>();
        var start = 0;
        while (start < clean.Length && passages.Count < max)
        {
            var end = Math.Min(start + size, clean.Length);
            if (end < clean.Length)
            {
                var space = clean.LastIndexOf(' ', end, end - start);
                if (space > start + size / 2)
                {
                    end = space;
                }
            }
            passages.Add(clean[start..end].Trim());
            if (end >= clean.Length)
            {
                break;
            }
            var next = end - overlap;
            var nextSpace = clean.IndexOf(' ', Math.Max(next, start + 1));
            start = nextSpace > start && nextSpace < end ? nextSpace + 1 : end;
        }
        return passages;
    }

    /// <summary>Reciprocal rank fusion of ranked lists (spec 012 plan, ranking): each list adds 1 / (k + rank), ranks from 1.</summary>
    public static IReadOnlyDictionary<T, double> Fuse<T>(IEnumerable<IReadOnlyList<T>> lists, int k = 60) where T : notnull
    {
        var scores = new Dictionary<T, double>();
        foreach (var list in lists)
        {
            for (var i = 0; i < list.Count; i++)
            {
                scores[list[i]] = scores.GetValueOrDefault(list[i]) + 1.0 / (k + i + 1);
            }
        }
        return scores;
    }

    /// <summary>About <paramref name="words"/> words of the passage around the first word that begins with a query term.</summary>
    public static string Snippet(string text, IReadOnlyList<string> terms, int words = 30)
    {
        var all = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (all.Length <= words)
        {
            return string.Join(' ', all);
        }
        var hit = Array.FindIndex(all, w => Words(Normalize(w)).Any(n => terms.Any(t => n.StartsWith(t, StringComparison.Ordinal))));
        var from = hit < 0 ? 0 : Math.Max(0, Math.Min(hit - words / 3, all.Length - words));
        var snippet = string.Join(' ', all.Skip(from).Take(words));
        return (from > 0 ? "… " : string.Empty) + snippet + (from + words < all.Length ? " …" : string.Empty);
    }
}
