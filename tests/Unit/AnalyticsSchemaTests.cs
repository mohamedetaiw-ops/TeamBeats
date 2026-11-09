using System.Text.Json;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 FR-003 and FR-017: the declared schema check and pseudonymous keys.</summary>
public sealed class AnalyticsSchemaTests
{
    private static IReadOnlyDictionary<string, object>? Check(string name, object fields, out int dropped)
    {
        var json = JsonSerializer.SerializeToElement(fields);
        return AnalyticsSchemaCheck.Fields(AnalyticsEvents.Schemas[name], json.EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)), out dropped);
    }

    [Fact]
    public void Declared_fields_keep_their_type_and_others_are_dropped_and_counted()
    {
        var item = Guid.NewGuid();
        var kept = Check(AnalyticsEvents.ContentViewed, new { source = "policies", itemId = item, note = "free text" }, out var dropped);
        Assert.NotNull(kept);
        Assert.Equal(item.ToString("D"), kept!["itemId"]);
        Assert.Equal(1, dropped);
        Assert.Equal(4L, Check(AnalyticsEvents.SearchQueried, new { results = 4 }, out _)!["results"]);
    }

    [Theory]
    [InlineData("{\"source\":\"content\"}")]
    [InlineData("{\"source\":\"wiki\",\"itemId\":\"8f5b8d40-1c3e-4a55-9a51-1b8f6b2c3d4e\"}")]
    [InlineData("{\"source\":\"content\",\"itemId\":\"not-a-guid\"}")]
    [InlineData("{\"source\":\"content\",\"itemId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"source\":\"has space\",\"itemId\":\"8f5b8d40-1c3e-4a55-9a51-1b8f6b2c3d4e\"}")]
    public void A_missing_required_field_or_a_value_outside_its_type_drops_the_event(string fields)
    {
        var json = JsonDocument.Parse(fields).RootElement;
        Assert.Null(AnalyticsSchemaCheck.Fields(AnalyticsEvents.Schemas[AnalyticsEvents.ContentViewed],
            json.EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)), out _));
    }

    [Fact]
    public void Counts_and_component_references_are_checked()
    {
        Assert.Null(Check(AnalyticsEvents.SearchQueried, new { results = -1 }, out _));
        Assert.Null(Check(AnalyticsEvents.SearchQueried, new { results = 2.5 }, out _));
        Assert.NotNull(Check(AnalyticsEvents.UiError, new { kind = "render", release = "2026.11.9", component = "core.news_list@2" }, out _));
        Assert.Null(Check(AnalyticsEvents.UiError, new { kind = "render", release = "2026.11.9", component = "<script>" }, out _));
        Assert.True(AnalyticsEvents.Schemas[AnalyticsEvents.UiError].Essential);
        Assert.False(AnalyticsEvents.Schemas[AnalyticsEvents.UiError].UserKeyed);
    }

    [Fact]
    public void Keys_are_stable_per_salt_and_differ_between_salts()
    {
        var user = Guid.NewGuid();
        var a = new byte[32];
        var b = new byte[32];
        b[0] = 1;
        Assert.Equal(Pseudonyms.Key(a, user), Pseudonyms.Key(a, user));
        Assert.NotEqual(Pseudonyms.Key(a, user), Pseudonyms.Key(b, user));
        Assert.Matches("^[0-9a-f]{32}$", Pseudonyms.Key(a, user));
        Assert.Equal(new Dictionary<string, string> { ["results"] = "3", ["share"] = "0.5" },
            AnalyticsSchemaCheck.AsStrings(new Dictionary<string, object> { ["results"] = 3L, ["share"] = 0.5 }));
    }
}
