using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TeamBeats.Api.DevHost;
using TeamBeats.Localization;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Modules.Experience.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 032: the property schema subset, executable content, manifest validation and the platform catalogue.</summary>
public sealed class ExperienceValidationTests
{
    private static readonly ILocalizer Localizer = new JsonCatalogueLocalizer();

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("https://intranet.example/plant", true)]
    [InlineData("/pages/safety", true)]
    [InlineData("http://intranet.example", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JaVaScRiPt :alert(1)", false)]
    [InlineData("data:text/html;base64,AAAA", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/x\" onmouseover=\"alert(1)", false)]
    public void Only_https_and_site_relative_urls_are_safe(string url, bool safe) => Assert.Equal(safe, PropertyValidator.IsSafeUrl(url));

    [Theory]
    [InlineData("Plant safety starts with you", false)]
    [InlineData("Revenue < 5% and costs > 3%", false)]
    [InlineData("<script>alert(1)</script>", true)]
    [InlineData("<img src=x onerror=alert(1)>", true)]
    [InlineData("hello onclick = alert(1)", true)]
    [InlineData("</b>", true)]
    public void Markup_and_event_handlers_are_executable(string text, bool executable) => Assert.Equal(executable, PropertyValidator.IsExecutable(text));

    [Fact]
    public void Properties_are_checked_against_their_types()
    {
        var schema = new Dictionary<string, PropertySchema>
        {
            ["title"] = new("text", Required: true, MaxLength: 10),
            ["size"] = new("integer", Minimum: 1, Maximum: 5),
            ["mode"] = new("enum", Enum: ["a", "b"]),
            ["links"] = new("list", Items: new PropertySchema("url"), MaxItems: 2),
            ["accent"] = new("color_token"),
            ["picture"] = new("image"),
        };
        Assert.Empty(PropertyValidator.Validate(schema, J("""{"title":{"en":"Hi","ar":"مرحبا"},"size":3,"mode":"a","links":["/a"],"accent":"color.accent.base","picture":"asset:5f2b8a64-8c4e-4b7a-9a0e-0c3d2f9b1a11"}""")));

        var problems = PropertyValidator.Validate(schema,
            J("""{"size":3.5,"mode":"c","links":["/a","/b","/c"],"accent":"red","picture":"https://x.example/a.png","extra":1}"""));
        Assert.Contains(("title", "required"), problems);
        Assert.Contains(("size", "type"), problems);
        Assert.Contains(("mode", "enum"), problems);
        Assert.Contains(("links", "range"), problems);
        Assert.Contains(("accent", "color_token"), problems);
        Assert.Contains(("picture", "image"), problems);
        Assert.Contains(("extra", "unknown"), problems);
        Assert.Contains(("title", "length"), PropertyValidator.Validate(schema, J("""{"title":{"en":"Far too long a title"}}""")));
        Assert.Contains(("title", "executable"), PropertyValidator.Validate(schema, J("""{"title":{"en":"<b>x</b>"}}""")));
    }

    [Fact]
    public void Defaults_fill_properties_a_new_manifest_version_added()
    {
        var schema = new Dictionary<string, PropertySchema> { ["size"] = new("integer", Default: J("4")), ["title"] = new("string") };
        var props = PropertyValidator.WithDefaults(schema, J("""{"title":"x"}"""));
        Assert.Equal(4, props["size"].GetInt32());
        Assert.Equal("x", props["title"].GetString());
    }

    private static ComponentManifest Valid() => new ReferencePacks().Manifests().First(m => m.Id == "reference.alpha.text");

    [Fact]
    public void Reference_packs_register_except_the_invalid_manifest()
    {
        var registry = new ComponentRegistry([new ReferencePacks()], Localizer, NullLogger<ComponentRegistry>.Instance);
        Assert.Equal([("reference.beta.invalid@1.0.0", "context")], registry.Rejected);
        Assert.NotNull(registry.Find("reference.alpha.text@1"));
        Assert.Null(registry.Find("reference.alpha.text@2"));
    }

    [Fact]
    public void Manifests_outside_the_contract_are_refused()
    {
        Assert.Null(ComponentRegistry.Validate(Valid(), Localizer));
        Assert.Equal("compatibility", ComponentRegistry.Validate(Valid() with { Compatibility = "^2" }, Localizer));
        Assert.Equal("trust_tier", ComponentRegistry.Validate(Valid() with { Trust = "tenant" }, Localizer));
        Assert.Equal("context", ComponentRegistry.Validate(Valid() with { Context = ["userEmail"] }, Localizer));
        Assert.Equal("resource_key", ComponentRegistry.Validate(Valid() with { Display = Valid().Display with { NameKey = "missing.key" } }, Localizer));
        Assert.Equal("property_schema", ComponentRegistry.Validate(Valid() with
        {
            Properties = new Dictionary<string, PropertySchema> { ["body"] = new("html") },
        }, Localizer));
        Assert.Equal("requirements", ComponentRegistry.Validate(Valid() with { Requires = new ComponentRequirements(["made.up"], []) }, Localizer));
        Assert.Equal("data_contract", ComponentRegistry.Validate(Valid() with { Data = ["https://elsewhere.example/api"] }, Localizer));
        Assert.Equal("placement", ComponentRegistry.Validate(Valid() with { Placement = new PlacementRules(MinSpan: 8, MaxSpan: 4) }, Localizer));
    }

    [Fact]
    public void Upgrades_replace_the_same_major_and_never_downgrade()
    {
        var v11 = Valid() with { Version = "1.1.0" };
        var registry = new ComponentRegistry([new Fixed(Valid(), v11, Valid() with { Version = "1.0.5" })], Localizer, NullLogger<ComponentRegistry>.Instance);
        Assert.Equal("1.1.0", registry.Find("reference.alpha.text@1")!.Version);
        Assert.Equal("duplicate_version", registry.Rejected.Single().Reason);
    }

    [Fact]
    public void Migration_hooks_register_only_to_the_next_major_of_registered_components()
    {
        var registry = new ComponentRegistry([new TeamBeats.Testing.UpgradePack()], Localizer, NullLogger<ComponentRegistry>.Instance);
        Assert.Equal([("test.upgrade.gauge@2->test.upgrade.gauge@4", "migration_range")], registry.Rejected);
        Assert.Equal("test.upgrade.gauge@2", registry.MigrationFrom("test.upgrade.gauge@1")!.To);
        Assert.Null(registry.MigrationFrom("test.upgrade.gauge@2"));
    }

    private sealed class Fixed(params ComponentManifest[] manifests) : IComponentRegistration
    {
        public IEnumerable<ComponentManifest> Manifests() => manifests;
    }

    [Fact]
    public void Catalogue_templates_match_their_layouts_and_use_only_known_categories()
    {
        var catalogue = new PlatformCatalogue();
        Assert.Equal(10, catalogue.Layouts.Count);
        // The 12 templates of spec 032 plus the default home template of spec 008 (T013).
        Assert.Equal([.. Enumerable.Range(1, 12).Select(i => $"T{i:00}"), "H01"],
            catalogue.Templates.Keys.OrderBy(k => k.StartsWith('H') ? 1 : 0).ThenBy(k => k, StringComparer.Ordinal));
        var home = catalogue.Templates["H01"];
        Assert.Contains(home.Regions, r => r.Policy.Personal && r.Policy.Protection == RegionProtection.Optional);
        Assert.Contains(home.Regions, r => !r.Policy.Personal && r.Policy.Protection == RegionProtection.Protected);
        foreach (var template in catalogue.Templates.Values)
        {
            Assert.All(template.Regions, r => Assert.All(r.Policy.AllowedCategories, c => Assert.Contains(c, ComponentRegistry.Categories)));
            Assert.All(template.Regions, r => Assert.InRange(r.Policy.MaxPlacements, 1, 40));
        }
        Assert.All(catalogue.Layouts.Values, l => Assert.All(l.Regions, r => Assert.InRange(r.Span, 1, 12)));
    }

    [Fact]
    public void Tool_box_utilities_register_as_platform_components_needing_no_capability_and_addable_with_defaults()
    {
        var registry = new ComponentRegistry([new PlatformComponents()], Localizer, NullLogger<ComponentRegistry>.Instance);
        Assert.Empty(registry.Rejected);
        Assert.Equal(["toolbox.time_converter@1", "toolbox.weather@1", "toolbox.world_clock@1"], registry.All.Select(m => m.Reference));
        Assert.All(registry.All, m =>
        {
            Assert.Equal("utility", m.Display.Category);
            Assert.Empty(m.Requires.Capabilities);
            Assert.Empty(PropertyValidator.Validate(m.Properties, null));
        });
        Assert.Equal(["/api/v1/weather"], registry.Find("toolbox.weather@1")!.Data);
        Assert.Empty(registry.Find("toolbox.world_clock@1")!.Data);
        var clock = registry.Find("toolbox.world_clock@1")!;
        var tooMany = JsonSerializer.SerializeToElement(new { zones = Enumerable.Repeat("Asia/Dubai", PlatformComponents.MaxClockZones + 1) });
        Assert.NotEmpty(PropertyValidator.Validate(clock.Properties, tooMany));
        Assert.Empty(PropertyValidator.Validate(clock.Properties, JsonSerializer.SerializeToElement(new { zones = new[] { "Asia/Dubai", "Europe/London" } })));
    }

    [Theory]
    [InlineData(0, "clear")]
    [InlineData(2, "partly_cloudy")]
    [InlineData(3, "cloudy")]
    [InlineData(48, "fog")]
    [InlineData(55, "drizzle")]
    [InlineData(63, "rain")]
    [InlineData(81, "rain")]
    [InlineData(75, "snow")]
    [InlineData(86, "snow")]
    [InlineData(95, "thunderstorm")]
    [InlineData(4, "unknown")]
    public void Open_meteo_weather_codes_group_into_the_conditions_the_component_names(int code, string condition) =>
        Assert.Equal(condition, TeamBeats.Modules.Integrations.Weather.OpenMeteoWeather.Condition(code));

    [Fact]
    public void My_mail_and_My_calendar_register_as_personal_components_needing_Microsoft_365_and_their_feature()
    {
        var registry = new ComponentRegistry([new TeamBeats.Modules.Integrations.Microsoft365.OutlookComponents()], Localizer, NullLogger<ComponentRegistry>.Instance);
        Assert.Empty(registry.Rejected);
        var mail = registry.Find("outlook.my_mail@1")!;
        var calendar = registry.Find("outlook.my_calendar@1")!;
        Assert.Equal("personal", mail.Display.Category);
        Assert.Equal(["integration.microsoft365"], mail.Requires.Capabilities);
        Assert.Equal("microsoft365:mail", mail.Requires.ConnectionFeature);
        Assert.Equal("microsoft365:calendar", calendar.Requires.ConnectionFeature);
        Assert.Empty(PropertyValidator.Validate(mail.Properties, null));
        Assert.Empty(PropertyValidator.Validate(calendar.Properties, null));
        Assert.NotEmpty(PropertyValidator.Validate(mail.Properties, JsonSerializer.SerializeToElement(new { top = 11 })));
        Assert.NotEmpty(PropertyValidator.Validate(calendar.Properties, JsonSerializer.SerializeToElement(new { days = 15 })));
    }

    [Fact]
    public void Content_components_register_with_the_content_capability_and_validate_their_properties()
    {
        var registry = new ComponentRegistry([new TeamBeats.Modules.Content.ContentComponents()], Localizer, NullLogger<ComponentRegistry>.Instance);
        Assert.Empty(registry.Rejected);
        Assert.Equal(["content.announcement_banner@1", "content.call_to_action@1", "content.links@1", "content.news_feed@1", "content.rich_text@1"],
            registry.All.Select(m => m.Reference).Order(StringComparer.Ordinal));
        Assert.All(registry.All, m => Assert.Contains(TeamBeats.Entitlements.Contracts.Capabilities.ContentCore, m.Requires.Capabilities));
        Assert.Contains(TeamBeats.Entitlements.Contracts.Capabilities.ContentCriticalAck, registry.Find("content.announcement_banner@1")!.Requires.Capabilities);
        Assert.True(registry.Find("content.announcement_banner@1")!.Placement.Singleton);
        Assert.Equal(["/api/v1/content/feed"], registry.Find("content.news_feed@1")!.Data);

        var feed = registry.Find("content.news_feed@1")!;
        Assert.Empty(PropertyValidator.Validate(feed.Properties, null));
        Assert.NotEmpty(PropertyValidator.Validate(feed.Properties, JsonSerializer.SerializeToElement(new { top = 21 })));
        Assert.NotEmpty(PropertyValidator.Validate(feed.Properties, JsonSerializer.SerializeToElement(new { type = "event" })));

        var text = registry.Find("content.rich_text@1")!;
        Assert.NotEmpty(PropertyValidator.Validate(text.Properties, null));
        Assert.Empty(PropertyValidator.Validate(text.Properties, JsonSerializer.SerializeToElement(new { itemId = Guid.NewGuid().ToString() })));

        var links = registry.Find("content.links@1")!;
        Assert.Empty(PropertyValidator.Validate(links.Properties, JsonSerializer.SerializeToElement(new { links = new[] { "https://example.org/a" } })));
        Assert.NotEmpty(PropertyValidator.Validate(links.Properties, JsonSerializer.SerializeToElement(new { links = new[] { "javascript:alert(1)" } })));
        Assert.NotEmpty(PropertyValidator.Validate(links.Properties,
            JsonSerializer.SerializeToElement(new { links = Enumerable.Repeat("https://example.org", TeamBeats.Modules.Content.ContentComponents.MaxLinks + 1) })));

        var cta = registry.Find("content.call_to_action@1")!;
        Assert.NotEmpty(PropertyValidator.Validate(cta.Properties, null));
        Assert.Empty(PropertyValidator.Validate(cta.Properties,
            JsonSerializer.SerializeToElement(new { heading = new { en = "Join" }, label = new { en = "Sign up" }, link = "https://example.org/join" })));
    }

    [Fact]
    public void A_manifest_naming_a_connection_feature_in_another_form_is_rejected()
    {
        var bad = new TeamBeats.Modules.Integrations.Microsoft365.OutlookComponents().Manifests().First() with
        {
            Requires = new ComponentRequirements(["integration.microsoft365"], [], "Microsoft 365 mail"),
        };
        var registry = new ComponentRegistry([new Fixed(bad)], Localizer, NullLogger<ComponentRegistry>.Instance);
        Assert.Contains(registry.Rejected, r => r.Reason == "requirements");
    }

}
