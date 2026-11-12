using TeamBeats.Localization;
using TeamBeats.Modules.Learning;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 028 FR-001 to FR-003, FR-010, section 6: course shape, quiz defaults and scoring, certificate IDs and the printed page.</summary>
public sealed class LearningRulesTests
{
    private static readonly string[] Enabled = ["en", "ar"];

    private static Dictionary<string, string> Both(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static QuizQuestionInput Single(string key, string right, string wrong) =>
        new(key, QuestionKinds.Single, Both("Q", "س"), [new QuizOptionInput("r", Both(right, right), true), new QuizOptionInput("w", Both(wrong, wrong), false)]);

    private static CourseDefinitionInput Course(params LessonInput[] lessons) => new(Both("Course", "دورة"), null, 15, lessons);

    [Fact]
    public void The_titles_decide_the_languages_and_every_lesson_needs_text_and_a_source_in_each()
    {
        var errors = new ValidationErrors();
        var page = Guid.NewGuid();
        var clean = CourseModel.Clean(Course(new LessonInput(null, LessonKinds.Page, Both("Read", "اقرأ"), new Dictionary<string, Guid> { ["en"] = page, ["ar"] = page }, null, 5)),
            Enabled, errors);
        Assert.False(errors.Any);
        Assert.Equal(["ar", "en"], clean.Locales);
        Assert.Matches("^[a-z0-9]{10}$", clean.Lessons[0].Key);

        var missing = new ValidationErrors();
        CourseModel.Clean(Course(new LessonInput("a", LessonKinds.Video, new Dictionary<string, string> { ["en"] = "Watch" }, new Dictionary<string, Guid> { ["en"] = page }, null, null)),
            Enabled, missing);
        Assert.Contains(("lessons[0].titles", "validation.learning.lesson_title"), missing.Entries);
        Assert.Contains(("lessons[0].sources", "validation.learning.lesson_source"), missing.Entries);

        var unknown = new ValidationErrors();
        CourseModel.Clean(new CourseDefinitionInput(new Dictionary<string, string> { ["fr"] = "Cours" }, null, null, []), Enabled, unknown);
        Assert.Contains(("titles", "validation.learning.course_title"), unknown.Entries);
    }

    [Fact]
    public void Quizzes_default_to_70_percent_and_three_attempts_and_check_their_answers()
    {
        var errors = new ValidationErrors();
        var clean = CourseModel.Clean(Course(new LessonInput("q", LessonKinds.Quiz, Both("Quiz", "اختبار"), null, new QuizInput(null, null, [Single("q1", "Yes", "No")]), null)),
            Enabled, errors);
        Assert.False(errors.Any);
        Assert.Equal(70, clean.Lessons[0].Quiz!.PassMark);
        Assert.Equal(3, clean.Lessons[0].Quiz!.Attempts);

        var bad = new ValidationErrors();
        CourseModel.Clean(Course(new LessonInput("q", LessonKinds.Quiz, Both("Quiz", "اختبار"), null, new QuizInput(0, 11,
        [
            new QuizQuestionInput("tf", QuestionKinds.TrueFalse, Both("Q", "س"),
                [new QuizOptionInput("t", Both("T", "ص"), true), new QuizOptionInput("f", Both("F", "خ"), true), new QuizOptionInput("x", Both("X", "ع"), false)]),
            new QuizQuestionInput("dup", QuestionKinds.Multiple, Both("Q", "س"), [new QuizOptionInput("a", Both("A", "أ"), false), new QuizOptionInput("a", Both("B", "ب"), false)]),
        ]), null)), Enabled, bad);
        var keys = bad.Entries.Select(e => e.Key).ToHashSet();
        Assert.Contains("validation.learning.pass_mark", keys);
        Assert.Contains("validation.learning.attempts", keys);
        Assert.Contains("validation.learning.option_count", keys);
        Assert.Contains("validation.learning.option_correct", keys);
        Assert.Contains("validation.learning.key", keys);
    }

    [Fact]
    public void A_question_counts_only_with_exactly_its_right_answers()
    {
        var quiz = new QuizDefinition(70, 3,
        [
            new QuizQuestion("one", QuestionKinds.Single, Both("Q", "س"), [new QuizOption("a", Both("A", "أ"), true), new QuizOption("b", Both("B", "ب"), false)]),
            new QuizQuestion("many", QuestionKinds.Multiple, Both("Q", "س"),
                [new QuizOption("a", Both("A", "أ"), true), new QuizOption("b", Both("B", "ب"), true), new QuizOption("c", Both("C", "ج"), false)]),
            new QuizQuestion("tf", QuestionKinds.TrueFalse, Both("Q", "س"), [new QuizOption("t", Both("T", "ص"), false), new QuizOption("f", Both("F", "خ"), true)]),
        ]);
        QuizAnswers Answers(string[] one, string[] many, string[] tf) => new(new Dictionary<string, IReadOnlyList<string>> { ["one"] = one, ["many"] = many, ["tf"] = tf });

        Assert.Equal((100, true), CourseModel.Score(quiz, Answers(["a"], ["b", "a"], ["f"])));
        Assert.Equal((66, false), CourseModel.Score(quiz, Answers(["a"], ["a"], ["f"])));
        Assert.Equal((33, false), CourseModel.Score(quiz, Answers(["a"], ["a", "b", "c"], ["t"])));
        Assert.Equal((0, false), CourseModel.Score(quiz, null));
    }

    [Fact]
    public void Certificate_ids_have_no_look_alikes_and_are_found_however_they_are_typed()
    {
        var code = Certificates.NewCode();
        Assert.Matches("^[A-HJKMNP-TV-Z2-9]{4}-[A-HJKMNP-TV-Z2-9]{4}-[A-HJKMNP-TV-Z2-9]{4}$", code);
        Assert.Equal(code, Certificates.NormalizeCode(code.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant()));
        Assert.Null(Certificates.NormalizeCode("ABCD-EFGH-IJK0"));
        Assert.Null(Certificates.NormalizeCode(null));
    }

    [Fact]
    public void The_printed_certificate_encodes_every_value_and_runs_right_to_left_in_arabic()
    {
        var view = new CertificateView(Guid.NewGuid(), "K7QM-2XRT-HP4N", Guid.NewGuid(), Both("Safety <b>", "السلامة"), 1,
            new CertificatePerson(Guid.NewGuid(), "Laila <script>"), DateTimeOffset.Parse("2026-11-12T08:00:00Z"), null);
        var localizer = new JsonCatalogueLocalizer();
        var en = Certificates.Html("en", "Acme & Co", view, localizer);
        Assert.Contains("dir=\"ltr\"", en, StringComparison.Ordinal);
        Assert.Contains("Laila &lt;script&gt;", en, StringComparison.Ordinal);
        Assert.Contains("Safety &lt;b&gt;", en, StringComparison.Ordinal);
        Assert.Contains("Acme &amp; Co", en, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", en, StringComparison.Ordinal);
        Assert.DoesNotContain("http", en, StringComparison.Ordinal);
        var ar = Certificates.Html("ar", "Acme", view, localizer);
        Assert.Contains("dir=\"rtl\"", ar, StringComparison.Ordinal);
        Assert.Contains("شهادة إتمام", ar, StringComparison.Ordinal);
    }
}
