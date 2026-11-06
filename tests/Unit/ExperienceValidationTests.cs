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

    private sealed class Fixed(params ComponentManifest[] manifests) : IComponentRegistration
    {
        public IEnumerable<ComponentManifest> Manifests() => manifests;
    }

    [Fact]
    public void Catalogue_templates_match_their_layouts_and_use_only_known_categories()
    {
        var catalogue = new PlatformCatalogue();
        Assert.Equal(10, catalogue.Layouts.Count);
        Assert.Equal(12, catalogue.Templates.Count);
        foreach (var template in catalogue.Templates.Values)
        {
            Assert.All(template.Regions, r => Assert.All(r.Policy.AllowedCategories, c => Assert.Contains(c, ComponentRegistry.Categories)));
            Assert.All(template.Regions, r => Assert.InRange(r.Policy.MaxPlacements, 1, 40));
        }
        Assert.All(catalogue.Layouts.Values, l => Assert.All(l.Regions, r => Assert.InRange(r.Span, 1, 12)));
    }
}
