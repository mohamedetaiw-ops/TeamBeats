using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Learning;

public static class LessonKinds
{
    public const string Page = "page";
    public const string Document = "document";
    public const string Video = "video";
    public const string Quiz = "quiz";

    /// <summary>An imported SCORM 1.2 or xAPI package (FR-016), played in a sandboxed frame.</summary>
    public const string Package = "package";

    public static readonly IReadOnlyList<string> All = [Page, Document, Video, Quiz, Package];

    /// <summary>Kinds that point at a page, a file or a package in each language of the course.</summary>
    public static bool HasSources(string kind) => kind is Page or Document or Video or Package;
}

public static class QuestionKinds
{
    public const string Single = "single";
    public const string Multiple = "multiple";
    public const string TrueFalse = "true_false";

    public static readonly IReadOnlyList<string> All = [Single, Multiple, TrueFalse];
}

public sealed record QuizOption(string Key, IReadOnlyDictionary<string, string> Labels, bool Correct);

public sealed record QuizQuestion(string Key, string Kind, IReadOnlyDictionary<string, string> Prompts, IReadOnlyList<QuizOption> Options);

public sealed record QuizDefinition(int PassMark, int Attempts, IReadOnlyList<QuizQuestion> Questions);

/// <summary>
/// One lesson. A page, document or video lesson names its source in each language of the course (FR-003), referenced by
/// ID and never copied (FR-001); a quiz lesson holds its questions.
/// </summary>
public sealed record LessonDefinition(string Key, string Kind, IReadOnlyDictionary<string, string> Titles, IReadOnlyDictionary<string, Guid>? Sources,
    QuizDefinition? Quiz, int? Minutes);

/// <summary>What a course teaches: its titles and summaries per locale (the locales of the titles are the course's languages) and its lessons in order.</summary>
public sealed record CourseDefinition(IReadOnlyDictionary<string, string> Titles, IReadOnlyDictionary<string, string> Summaries, int? Minutes,
    IReadOnlyList<LessonDefinition> Lessons)
{
    [JsonIgnore]
    public IReadOnlyList<string> Locales => Titles.Keys.Order(StringComparer.Ordinal).ToList();
}

public sealed record QuizOptionInput(string? Key, IReadOnlyDictionary<string, string>? Labels, bool Correct);

public sealed record QuizQuestionInput(string? Key, string? Kind, IReadOnlyDictionary<string, string>? Prompts, IReadOnlyList<QuizOptionInput>? Options);

public sealed record QuizInput(int? PassMark, int? Attempts, IReadOnlyList<QuizQuestionInput>? Questions);

public sealed record LessonInput(string? Key, string? Kind, IReadOnlyDictionary<string, string>? Titles, IReadOnlyDictionary<string, Guid>? Sources,
    QuizInput? Quiz, int? Minutes);

public sealed record CourseDefinitionInput(IReadOnlyDictionary<string, string>? Titles, IReadOnlyDictionary<string, string>? Summaries, int? Minutes,
    IReadOnlyList<LessonInput>? Lessons);

/// <summary>A learner's answers: for each question key, the option keys chosen.</summary>
public sealed record QuizAnswers(IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers);

/// <summary>
/// Pure course rules (spec 028 FR-001 to FR-003, section 6): the shape of a definition, the defaults (pass mark 70%,
/// three attempts) and quiz scoring. The service checks the sources exist and may be read.
/// </summary>
public static partial class CourseModel
{
    public const int MaxTitle = 200;
    public const int MaxSummary = 2000;
    public const int MaxLessons = 50;
    public const int MaxQuestions = 50;
    public const int MaxPrompt = 500;
    public const int MaxOptions = 10;
    public const int MaxMinutes = 600;
    public const int MaxAttempts = 10;
    public const int DefaultPassMark = 70;
    public const int DefaultAttempts = 3;

    [GeneratedRegex("^[a-z0-9_-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private static string NewKey() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>
    /// A clean definition from the builder's input, or errors. Locales must be ones the tenant has enabled; every lesson,
    /// question and option needs its text in every language of the course, and every source lesson a source per language.
    /// </summary>
    public static CourseDefinition Clean(CourseDefinitionInput? input, IReadOnlyCollection<string> enabledLocales, ValidationErrors errors)
    {
        var titles = Texts(input?.Titles, null, MaxTitle, "titles", "validation.learning.course_title", errors, enabledLocales);
        if (titles.Count == 0)
        {
            errors.Add("titles", "validation.learning.course_title");
        }
        var locales = titles.Keys.ToList();
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (locale, text) in input?.Summaries ?? new Dictionary<string, string>())
        {
            var clean = text?.Trim() ?? "";
            if (clean.Length == 0)
            {
                continue;
            }
            if (!locales.Contains(locale) || clean.Length > MaxSummary)
            {
                errors.Add("summaries", "validation.learning.course_summary");
                continue;
            }
            summaries[locale] = clean;
        }
        if (input?.Minutes is { } minutes && minutes is < 1 or > MaxMinutes)
        {
            errors.Add("minutes", "validation.learning.minutes");
        }
        var lessons = new List<LessonDefinition>();
        var given = input?.Lessons ?? [];
        if (given.Count > MaxLessons)
        {
            errors.Add("lessons", "validation.learning.lesson_count");
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(given.Count, MaxLessons); i++)
        {
            var field = $"lessons[{i}]";
            var lesson = given[i];
            var key = Key(lesson?.Key, field, keys, errors);
            var kind = lesson?.Kind ?? "";
            if (!LessonKinds.All.Contains(kind))
            {
                errors.Add($"{field}.kind", "validation.learning.lesson_kind");
                continue;
            }
            var lessonTitles = Texts(lesson!.Titles, locales, MaxTitle, $"{field}.titles", "validation.learning.lesson_title", errors);
            if (lesson.Minutes is { } m && m is < 1 or > MaxMinutes)
            {
                errors.Add($"{field}.minutes", "validation.learning.minutes");
            }
            IReadOnlyDictionary<string, Guid>? sources = null;
            QuizDefinition? quiz = null;
            if (LessonKinds.HasSources(kind))
            {
                var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
                foreach (var locale in locales)
                {
                    if (lesson.Sources?.TryGetValue(locale, out var id) == true && id != Guid.Empty)
                    {
                        map[locale] = id;
                    }
                    else
                    {
                        errors.Add($"{field}.sources", "validation.learning.lesson_source");
                    }
                }
                if (lesson.Sources?.Keys.Any(k => !locales.Contains(k)) == true)
                {
                    errors.Add($"{field}.sources", "validation.learning.lesson_source");
                }
                sources = map;
            }
            else
            {
                quiz = Quiz(lesson.Quiz, locales, $"{field}.quiz", errors);
            }
            lessons.Add(new LessonDefinition(key, kind, lessonTitles, sources, quiz, lesson.Minutes));
        }
        return new CourseDefinition(titles, summaries, input?.Minutes, lessons);
    }

    /// <summary>What publishing needs beyond a clean draft: at least one lesson.</summary>
    public static void CheckPublishable(CourseDefinition definition, ValidationErrors errors)
    {
        if (definition.Lessons.Count == 0)
        {
            errors.Add("lessons", "validation.learning.lesson_count");
        }
    }

    private static QuizDefinition Quiz(QuizInput? input, IReadOnlyList<string> locales, string field, ValidationErrors errors)
    {
        var passMark = input?.PassMark ?? DefaultPassMark;
        if (passMark is < 1 or > 100)
        {
            errors.Add($"{field}.passMark", "validation.learning.pass_mark");
        }
        var attempts = input?.Attempts ?? DefaultAttempts;
        if (attempts is < 1 or > MaxAttempts)
        {
            errors.Add($"{field}.attempts", "validation.learning.attempts");
        }
        var given = input?.Questions ?? [];
        if (given.Count is 0 or > MaxQuestions)
        {
            errors.Add($"{field}.questions", "validation.learning.question_count");
        }
        var questions = new List<QuizQuestion>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(given.Count, MaxQuestions); i++)
        {
            var qField = $"{field}.questions[{i}]";
            var question = given[i];
            var key = Key(question?.Key, qField, keys, errors);
            var kind = question?.Kind ?? "";
            if (!QuestionKinds.All.Contains(kind))
            {
                errors.Add($"{qField}.kind", "validation.learning.question_kind");
                continue;
            }
            var prompts = Texts(question!.Prompts, locales, MaxPrompt, $"{qField}.prompts", "validation.learning.question_prompt", errors);
            var optionsIn = question.Options ?? [];
            var countOk = kind == QuestionKinds.TrueFalse ? optionsIn.Count == 2 : optionsIn.Count is >= 2 and <= MaxOptions;
            if (!countOk)
            {
                errors.Add($"{qField}.options", "validation.learning.option_count");
            }
            var options = new List<QuizOption>();
            var optionKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < Math.Min(optionsIn.Count, MaxOptions); j++)
            {
                var oField = $"{qField}.options[{j}]";
                var option = optionsIn[j];
                var optionKey = Key(option?.Key, oField, optionKeys, errors);
                var labels = Texts(option?.Labels, locales, MaxTitle, $"{oField}.labels", "validation.learning.option_label", errors);
                options.Add(new QuizOption(optionKey, labels, option?.Correct == true));
            }
            var correct = options.Count(o => o.Correct);
            if (kind == QuestionKinds.Multiple ? correct == 0 : correct != 1)
            {
                errors.Add($"{qField}.options", "validation.learning.option_correct");
            }
            questions.Add(new QuizQuestion(key, kind, prompts, options));
        }
        return new QuizDefinition(passMark, attempts, questions);
    }

    private static string Key(string? given, string field, HashSet<string> seen, ValidationErrors errors)
    {
        var key = string.IsNullOrWhiteSpace(given) ? NewKey() : given.Trim();
        if (!KeyPattern().IsMatch(key) || !seen.Add(key))
        {
            errors.Add($"{field}.key", "validation.learning.key");
        }
        return key;
    }

    /// <summary>
    /// Text per locale, trimmed. With <paramref name="required"/> every listed locale needs text and no other locale is
    /// taken; without it (the course titles) the locales given decide the course's languages and must be enabled.
    /// </summary>
    private static Dictionary<string, string> Texts(IReadOnlyDictionary<string, string>? given, IReadOnlyList<string>? required, int max, string field, string key,
        ValidationErrors errors, IReadOnlyCollection<string>? enabled = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var bad = false;
        foreach (var (locale, text) in given ?? new Dictionary<string, string>())
        {
            var clean = text?.Trim() ?? "";
            if (required is null && clean.Length == 0)
            {
                continue;
            }
            var known = required?.Contains(locale) ?? enabled!.Contains(locale);
            if (!known || clean.Length is 0 || clean.Length > max)
            {
                bad = true;
                continue;
            }
            result[locale] = clean;
        }
        if (required is not null && required.Any(l => !result.ContainsKey(l)))
        {
            bad = true;
        }
        if (bad)
        {
            errors.Add(field, key);
        }
        return result;
    }

    /// <summary>Score in percent, rounded down, and whether it reaches the pass mark. A question counts when exactly its correct options are chosen.</summary>
    public static (int Score, bool Passed) Score(QuizDefinition quiz, QuizAnswers? answers)
    {
        var given = answers?.Answers ?? new Dictionary<string, IReadOnlyList<string>>();
        var right = 0;
        foreach (var question in quiz.Questions)
        {
            var correct = question.Options.Where(o => o.Correct).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
            if (given.TryGetValue(question.Key, out var chosen) && chosen is not null && chosen.ToHashSet(StringComparer.Ordinal).SetEquals(correct))
            {
                right++;
            }
        }
        var score = quiz.Questions.Count == 0 ? 0 : right * 100 / quiz.Questions.Count;
        return (score, score >= quiz.PassMark);
    }
}
