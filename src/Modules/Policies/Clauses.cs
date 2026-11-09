using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// The rules of a policy's text (FR-003, FR-005): numbered clauses at up to three levels, each with a title and a body in
/// every locale of the version, keys that keep a clause's anchor across versions, and a clause-level compare.
/// </summary>
public static partial class ClauseRules
{
    public const int MaxClauses = 300;
    public const int MaxLevel = 3;
    public const int MaxTitleLength = 300;
    public const int MaxCharacters = 400_000;
    public const int MaxChangeNote = 2000;

    /// <summary>Blocks a clause body may use: text, lists, quotes, tables and links. No headings (clauses are the structure), images or embeds.</summary>
    public static readonly IReadOnlySet<string> BodyBlocks = new HashSet<string>(StringComparer.Ordinal)
    {
        BlockTypes.Paragraph, BlockTypes.List, BlockTypes.Quote, BlockTypes.Table, BlockTypes.Link,
    };

    private static readonly IReadOnlySet<string> NoEmbeds = new HashSet<string>();

    [GeneratedRegex("^[a-z0-9]{4,16}$")]
    private static partial Regex KeyPattern();

    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>A new clause key: ten lower-case letters and digits.</summary>
    public static string NewKey()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }
        return new string(chars);
    }

    /// <summary>The anchor of a clause on the policy page, the same in every version that keeps the clause.</summary>
    public static string Anchor(string key) => "clause-" + key;

    /// <summary>Numbers from the order and levels: 1, 1.1, 1.2, 2, 2.1.1. A level may go down any amount but up by one.</summary>
    public static IReadOnlyList<string> Numbers(IReadOnlyList<int> levels)
    {
        var counters = new int[MaxLevel];
        var numbers = new List<string>(levels.Count);
        foreach (var level in levels)
        {
            counters[level - 1]++;
            for (var i = level; i < MaxLevel; i++)
            {
                counters[i] = 0;
            }
            numbers.Add(string.Join('.', counters.Take(level)));
        }
        return numbers;
    }

    internal static IReadOnlyList<ClauseView> Views(IReadOnlyList<StoredClause> clauses)
    {
        var numbers = Numbers(clauses.Select(c => c.Level).ToList());
        return clauses.Select((c, i) => new ClauseView(c.Key, Anchor(c.Key), numbers[i], c.Level, c.Titles, c.Bodies)).ToList();
    }

    /// <summary>
    /// Checks and normalizes a draft. Titles say which locales the version has; each must be enabled for the tenant.
    /// Missing text is allowed while drafting and refused on submit by <see cref="Complete"/>.
    /// </summary>
    internal static (List<string> Locales, Dictionary<string, string> Titles, List<StoredClause> Clauses) Normalize(DraftRequest request, IReadOnlyCollection<string> enabled,
        ValidationErrors errors)
    {
        var titles = (request.Titles ?? new Dictionary<string, string>()).Where(t => !string.IsNullOrWhiteSpace(t.Value))
            .ToDictionary(t => t.Key, t => t.Value.Trim(), StringComparer.Ordinal);
        if (titles.Count == 0 || titles.Values.Any(t => t.Length > MaxTitleLength))
        {
            errors.Add("titles", "validation.policies.titles");
        }
        if (titles.Keys.Any(l => !enabled.Contains(l)))
        {
            errors.Add("titles", "validation.policies.locale");
        }
        var locales = enabled.Where(titles.ContainsKey).ToList();
        if (request.ChangeNote is { } note && note.Trim().Length > MaxChangeNote)
        {
            errors.Add("changeNote", "validation.policies.change_note");
        }
        var input = request.Clauses ?? [];
        var clauses = new List<StoredClause>();
        if (input.Count > MaxClauses)
        {
            errors.Add("clauses", "validation.policies.too_many_clauses");
            return (locales, titles, clauses);
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var characters = 0;
        var previous = 0;
        for (var i = 0; i < input.Count; i++)
        {
            var field = $"clauses[{i}]";
            var c = input[i];
            if (c is null)
            {
                errors.Add(field, "validation.policies.clause");
                continue;
            }
            var key = c.Key ?? NewKey();
            if (!IsKey(key) || !keys.Add(key))
            {
                errors.Add(field + ".key", "validation.policies.clause_key");
            }
            var level = c.Level ?? 1;
            if (level < 1 || level > MaxLevel || level > previous + 1)
            {
                errors.Add(field + ".level", "validation.policies.clause_level");
            }
            previous = level;
            var clauseTitles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (locale, title) in c.Titles ?? new Dictionary<string, string>())
            {
                if (!titles.ContainsKey(locale))
                {
                    errors.Add(field + ".titles", "validation.policies.clause_locale");
                }
                else if (title?.Trim() is { Length: > 0 } trimmed)
                {
                    if (trimmed.Length > MaxTitleLength)
                    {
                        errors.Add(field + ".titles", "validation.policies.clause_title");
                    }
                    clauseTitles[locale] = trimmed;
                    characters += trimmed.Length;
                }
            }
            var bodies = new Dictionary<string, BodyDocument>(StringComparer.Ordinal);
            foreach (var (locale, body) in c.Bodies ?? new Dictionary<string, BodyDocument>())
            {
                if (!titles.ContainsKey(locale))
                {
                    errors.Add(field + ".bodies", "validation.policies.clause_locale");
                    continue;
                }
                if (body?.Blocks is not { Count: > 0 })
                {
                    continue;
                }
                if (body.Blocks.Any(b => b is null || !BodyBlocks.Contains(b.Type ?? string.Empty)))
                {
                    errors.Add(field + ".bodies", "validation.policies.clause_block");
                    continue;
                }
                BodyValidator.Validate(body, NoEmbeds, field + ".bodies", errors);
                characters += BodyValidator.PlainText(body).Length;
                bodies[locale] = body;
            }
            clauses.Add(new StoredClause(key, level, clauseTitles, bodies));
        }
        if (characters > MaxCharacters)
        {
            errors.Add("clauses", "validation.policies.too_large");
        }
        return (locales, titles, clauses);
    }

    /// <summary>
    /// A version may go for approval only when every clause has its title and body in every locale, so all language
    /// variants are approved together (FR-005), and the binding language, when set, is one of them.
    /// </summary>
    internal static void Complete(IReadOnlyList<string> locales, IReadOnlyList<StoredClause> clauses, string? bindingLocale, ValidationErrors errors)
    {
        if (clauses.Count == 0)
        {
            errors.Add("clauses", "validation.policies.no_clauses");
        }
        for (var i = 0; i < clauses.Count; i++)
        {
            if (locales.Any(l => !clauses[i].Titles.ContainsKey(l) || !clauses[i].Bodies.ContainsKey(l)))
            {
                errors.Add($"clauses[{i}]", "validation.policies.clause_incomplete");
            }
        }
        if (bindingLocale is not null && !locales.Contains(bindingLocale))
        {
            errors.Add("locales", "validation.policies.binding_locale_missing");
        }
    }

    /// <summary>The visible text of a clause in one locale, for compare and the hash of what a reader attested to.</summary>
    internal static string Text(StoredClause clause, string locale) =>
        (clause.Titles.GetValueOrDefault(locale) ?? string.Empty) + "\n" + (clause.Bodies.TryGetValue(locale, out var body) ? BodyValidator.PlainText(body) : string.Empty);

    /// <summary>
    /// Clause-level compare of a version against the version in force (US1 scenario 1): clauses of the new version in
    /// its order, each added, changed or unchanged, then the clauses it removed. A moved clause with the same text is
    /// unchanged; its new number shows on the after side.
    /// </summary>
    internal static List<ClauseChange> Compare(IReadOnlyList<StoredClause>? before, IReadOnlyList<StoredClause> after, IReadOnlyList<string> locales)
    {
        var oldViews = before is null ? [] : Views(before).ToDictionary(v => v.Key, StringComparer.Ordinal);
        var oldClauses = before?.ToDictionary(c => c.Key, StringComparer.Ordinal) ?? [];
        var newViews = Views(after);
        var changes = new List<ClauseChange>();
        for (var i = 0; i < after.Count; i++)
        {
            var clause = after[i];
            if (!oldClauses.TryGetValue(clause.Key, out var old))
            {
                changes.Add(new ClauseChange(clause.Key, ClauseChanges.Added, null, newViews[i]));
                continue;
            }
            var same = old.Level == clause.Level && locales.All(l => Text(old, l) == Text(clause, l));
            changes.Add(new ClauseChange(clause.Key, same ? ClauseChanges.Unchanged : ClauseChanges.Changed, oldViews[clause.Key], newViews[i]));
        }
        var kept = after.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        changes.AddRange(oldViews.Values.Where(v => !kept.Contains(v.Key)).Select(v => new ClauseChange(v.Key, ClauseChanges.Removed, v, null)));
        return changes;
    }

    /// <summary>SHA-256 of a text, lower-case hex, for the attestation record (FR-007).</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
