using System.Text.Json;
using TeamBeats.Modules.Services;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 016 T003: the form schema of FR-003 and the one validator used on save and on submit.</summary>
public sealed class FormValidatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    private static IReadOnlyDictionary<string, string> L(string en) => new Dictionary<string, string> { ["en"] = en, ["ar"] = en + " ar" };

    private static FieldOption O(string value) => new(value, L(value));

    private static FormDefinition Resignation() => new(
    [
        new FormField("name", FieldTypes.Text, L("Name"), Required: true, Prefill: PrefillSources.DisplayName),
        new FormField("notice", FieldTypes.PolicyNote, L("Notice"), Note: L("Resignation needs 60 days notice")),
        new FormField("last_day", FieldTypes.Date, L("Last working day"), Required: true,
            Rules: [new FieldRule(RuleKinds.MinDaysFromToday, 60, L("At least 60 days from today"))]),
        new FormField("reason", FieldTypes.Choice, L("Reason"), Required: true, Options: [O("career"), O("relocation"), O("other")]),
        new FormField("other_reason", FieldTypes.Text, L("Other reason"), Required: true, ShowWhen: new FieldCondition("reason", ["other"]),
            Rules: [new FieldRule(RuleKinds.MaxLength, 100)]),
        new FormField("cost", FieldTypes.Money, L("Cost"), Currencies: ["AED", "SAR"], Rules: [new FieldRule(RuleKinds.Max, 1000)]),
        new FormField("period", FieldTypes.DateRange, L("Period")),
        new FormField("people", FieldTypes.Person, L("People"), Rules: [new FieldRule(RuleKinds.MaxItems, 2)]),
        new FormField("unit", FieldTypes.OrgUnit, L("Unit")),
        new FormField("tags", FieldTypes.MultiChoice, L("Tags"), Options: [O("a"), O("b")]),
        new FormField("files", FieldTypes.Attachment, L("Files")),
        new FormField("boxes", FieldTypes.Number, L("Boxes"), Rules: [new FieldRule(RuleKinds.Min, 1), new FieldRule(RuleKinds.Max, 2)]),
    ]);

    private static string[] DefinitionErrors(FormDefinition form)
    {
        var errors = new ValidationErrors();
        FormValidator.ValidateDefinition(form, errors);
        return Keys(errors);
    }

    private static string[] Keys(ValidationErrors errors)
    {
        try
        {
            errors.ThrowIfAny();
            return [];
        }
        catch (AppException e)
        {
            return e.Errors!.SelectMany(kv => kv.Value.Select(v => kv.Key + ":" + v)).ToArray();
        }
    }

    private static (Dictionary<string, JsonElement> Data, FormReferences Refs, string[] Errors) Submit(object data)
    {
        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(data))!;
        var errors = new ValidationErrors();
        var (clean, refs) = FormValidator.ValidateData(Resignation(), input, Today, errors);
        return (clean, refs, Keys(errors));
    }

    [Fact]
    public void A_form_with_every_field_type_is_valid()
    {
        Assert.Empty(DefinitionErrors(Resignation()));
    }

    [Fact]
    public void Definitions_refuse_bad_keys_types_options_rules_prefill_and_forward_conditions()
    {
        Assert.Contains("form.fields[1].key:validation.services.field_key", DefinitionErrors(new([
            new FormField("a", FieldTypes.Text, L("A")), new FormField("a", FieldTypes.Text, L("B"))])));
        Assert.Contains("form.fields[0].key:validation.services.field_key", DefinitionErrors(new([new FormField("Bad Key", FieldTypes.Text, L("A"))])));
        Assert.Contains("form.fields[0].type:validation.services.field_type", DefinitionErrors(new([new FormField("a", "script", L("A"))])));
        Assert.Contains("form.fields[0].labels:validation.services.field_labels", DefinitionErrors(new([new FormField("a", FieldTypes.Text, new Dictionary<string, string>())])));
        Assert.Contains("form.fields[0].options:validation.services.field_options", DefinitionErrors(new([new FormField("a", FieldTypes.Choice, L("A"))])));
        Assert.Contains("form.fields[0].options:validation.services.field_options", DefinitionErrors(new([new FormField("a", FieldTypes.Choice, L("A"), Options: [O("x"), O("x")])])));
        Assert.Contains("form.fields[0].rules:validation.services.field_rules", DefinitionErrors(new([new FormField("a", FieldTypes.Text, L("A"), Rules: [new FieldRule(RuleKinds.Min, 1)])])));
        Assert.Contains("form.fields[0].prefill:validation.services.field_prefill", DefinitionErrors(new([new FormField("a", FieldTypes.Number, L("A"), Prefill: PrefillSources.Email)])));
        Assert.Contains("form.fields[0].prefill:validation.services.field_prefill", DefinitionErrors(new([new FormField("a", FieldTypes.Text, L("A"), Prefill: "profile.salary")])));
        // HR sources (spec 019 US5): the hire date fills a date field only, and only HR fields can be read-only or need a current value.
        Assert.Contains("form.fields[0].prefill:validation.services.field_prefill", DefinitionErrors(new([new FormField("a", FieldTypes.Text, L("A"), Prefill: PrefillSources.HrHireDate)])));
        Assert.Contains("form.fields[0].prefill:validation.services.field_prefill_hr_only", DefinitionErrors(new([new FormField("a", FieldTypes.Text, L("A"), Prefill: PrefillSources.Email, ReadOnly: true)])));
        Assert.Contains("form.fields[0].prefill:validation.services.field_prefill_hr_only", DefinitionErrors(new([new FormField("a", FieldTypes.Text, L("A"), MustBeCurrent: true)])));
        Assert.Empty(DefinitionErrors(new([new FormField("a", FieldTypes.Date, L("A"), Prefill: PrefillSources.HrHireDate, ReadOnly: true, MustBeCurrent: true),
            new FormField("b", FieldTypes.Text, L("B"), Prefill: PrefillSources.HrGrade, ReadOnly: true)])));
        Assert.Contains("form.fields[0].currencies:validation.services.field_currencies", DefinitionErrors(new([new FormField("a", FieldTypes.Money, L("A"), Currencies: ["dollars"])])));
        Assert.Contains("form.fields[0]:validation.services.policy_note_value", DefinitionErrors(new([new FormField("a", FieldTypes.PolicyNote, L("A"), Required: true, Note: L("n"))])));
        // A condition can only point back to an earlier choice field, so conditions never loop.
        Assert.Contains("form.fields[0].showWhen:validation.services.field_condition", DefinitionErrors(new([
            new FormField("a", FieldTypes.Text, L("A"), ShowWhen: new FieldCondition("b", ["x"])), new FormField("b", FieldTypes.Choice, L("B"), Options: [O("x")])])));
        Assert.Contains("form.fields[1].showWhen:validation.services.field_condition", DefinitionErrors(new([
            new FormField("b", FieldTypes.Choice, L("B"), Options: [O("x")]), new FormField("a", FieldTypes.Text, L("A"), ShowWhen: new FieldCondition("b", ["y"]))])));
        Assert.Contains("form.fields:validation.services.form_too_many_fields", DefinitionErrors(new(
            Enumerable.Range(0, 51).Select(i => new FormField("f" + i, FieldTypes.Text, L("F"))).ToList())));
    }

    [Fact]
    public void A_valid_submission_keeps_shown_fields_and_collects_references()
    {
        var person = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var file = Guid.NewGuid();
        var (data, refs, errors) = Submit(new
        {
            name = "Sara", last_day = "2027-01-07", reason = "career", other_reason = "ignored while hidden", cost = new { amount = 500, currency = "AED" },
            period = new { from = "2026-12-01", to = "2026-12-03" }, people = new[] { person.ToString() }, unit = unit.ToString(), tags = new[] { "a", "b" },
            files = new[] { file.ToString() }, boxes = 2, unknown = "dropped",
        });
        Assert.Empty(errors);
        Assert.False(data.ContainsKey("other_reason"));
        Assert.False(data.ContainsKey("unknown"));
        Assert.Equal("career", data["reason"].GetString());
        Assert.Equal([person], refs.People);
        Assert.Equal([unit], refs.Units);
        Assert.Equal([file], refs.Attachments);
    }

    [Fact]
    public void The_sixty_day_notice_rule_blocks_an_earlier_last_day()
    {
        Assert.Contains("data.last_day:validation.services.field_date_too_early", Submit(new { name = "Sara", last_day = "2027-01-06", reason = "career" }).Errors);
        Assert.Empty(Submit(new { name = "Sara", last_day = "2027-01-07", reason = "career" }).Errors);
    }

    [Fact]
    public void Required_fields_count_only_while_shown()
    {
        var hidden = Submit(new { name = "Sara", last_day = "2027-02-01", reason = "career" });
        Assert.Empty(hidden.Errors);
        var shown = Submit(new { name = "Sara", last_day = "2027-02-01", reason = "other" });
        Assert.Contains("data.other_reason:validation.services.field_required", shown.Errors);
        Assert.Contains("data.name:validation.services.field_required", Submit(new { name = "  ", last_day = "2027-02-01", reason = "career" }).Errors);
    }

    [Fact]
    public void Values_of_the_wrong_shape_or_out_of_range_are_refused()
    {
        var errors = Submit(new
        {
            name = 5, last_day = "08/11/2027", reason = "fired", cost = new { amount = 2000, currency = "AED" }, period = new { from = "2026-12-05", to = "2026-12-01" },
            people = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString() }, unit = "not-a-guid", tags = new[] { "a", "a" },
            boxes = 3,
        }).Errors;
        Assert.Contains("data.name:validation.services.field_invalid", errors);
        Assert.Contains("data.last_day:validation.services.field_invalid", errors);
        Assert.Contains("data.reason:validation.services.field_option", errors);
        Assert.Contains("data.cost:validation.services.field_max", errors);
        Assert.Contains("data.period:validation.services.field_range_order", errors);
        Assert.Contains("data.people:validation.services.field_too_many", errors);
        Assert.Contains("data.unit:validation.services.field_invalid", errors);
        Assert.Contains("data.tags:validation.services.field_option", errors);
        Assert.Contains("data.boxes:validation.services.field_max", errors);
        Assert.Contains("data.cost:validation.services.field_invalid", Submit(new { name = "S", last_day = "2027-02-01", reason = "career", cost = new { amount = 1, currency = "USD" } }).Errors);
    }

    [Fact]
    public void Search_folds_Arabic_letter_forms_and_ignores_diacritics()
    {
        Assert.Equal(Search.Normalize("بطاقات"), Search.Normalize("بِطَاقَات"));
        Assert.Equal(Search.Normalize("اجازة"), Search.Normalize("إجازة"));
        Assert.Equal("card", Search.Normalize("CARD"));
    }
}
