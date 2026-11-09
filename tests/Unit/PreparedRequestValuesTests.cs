using System.Text.Json;
using TeamBeats.Modules.Services;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 020 US8: an agent-prepared request keeps only fields of the form, as the field's type expects.</summary>
public sealed class PreparedRequestValuesTests
{
    private static readonly Dictionary<string, string> Label = new() { ["en"] = "Field" };

    private static readonly FormDefinition Form = new(
    [
        new FormField("name", FieldTypes.Text, Label),
        new FormField("boxes", FieldTypes.Number, Label),
        new FormField("start", FieldTypes.Date, Label),
        new FormField("sizes", FieldTypes.MultiChoice, Label),
        new FormField("budget", FieldTypes.Money, Label),
        new FormField("files", FieldTypes.Attachment, Label),
    ]);

    private static JsonElement Args(string fields) => JsonDocument.Parse($$"""{"serviceId":"x","fields":{{fields}}}""").RootElement.Clone();

    [Fact]
    public void Values_follow_the_field_types_and_unknown_or_unparsed_fields_are_dropped()
    {
        var values = RequestPrepareTool.Values(Form, Args(
            """
            [{"key":"name","value":" Sara "},{"key":"boxes","value":"2.5"},{"key":"start","value":"2026-12-01"},{"key":"sizes","value":"S, M"},
             {"key":"budget","value":"100"},{"key":"files","value":"a.pdf"},{"key":"other","value":"x"}]
            """));
        Assert.Equal(["name", "boxes", "start", "sizes"], values.Keys.ToArray());
        Assert.Equal("Sara", values["name"].GetString());
        Assert.Equal(2.5, values["boxes"].GetDouble());
        Assert.Equal(["S", "M"], values["sizes"].EnumerateArray().Select(v => v.GetString()!).ToArray());
    }

    [Fact]
    public void Bad_numbers_and_dates_are_left_for_the_person()
    {
        var values = RequestPrepareTool.Values(Form, Args("""[{"key":"boxes","value":"two"},{"key":"start","value":"1 December"},{"key":"name","value":" "}]"""));
        Assert.Empty(values);
        Assert.Empty(RequestPrepareTool.Values(Form, JsonDocument.Parse("""{"serviceId":"x"}""").RootElement));
    }
}
