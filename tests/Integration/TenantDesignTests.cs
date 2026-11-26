using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 036 R3, DLS-120 to DLS-122 (T056, T058): a theme editor derives a design from one of the four; proposing it runs
/// the contrast checks; someone else who publishes themes approves it; applying it writes the base design and its values
/// into a brand's draft and changes nothing live until that draft is published. Without the package feature nothing can
/// be made, and another tenant never sees a design.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TenantDesignTests(TenantWorld world)
{
    private const string Designs = "/api/v1/experience/designs";
    private const string Brands = "/api/v1/experience/brands";

    private static object Token(string value, string? dark = null) => dark is null
        ? new Dictionary<string, object> { ["$value"] = value }
        : new Dictionary<string, object> { ["$value"] = value, ["$modes"] = new Dictionary<string, string> { ["dark"] = dark } };

    private static object Design(string key, object tokens, string @base = "atlas") =>
        new { key, @base, names = new { en = "Harbour", ar = "الميناء" }, description = "Campaign season", tokens };

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("tds-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "manager", MembershipTypes.Employee);
        await world.AssignAsync(t, "manager", "brand_manager", "tenant", null);
        await world.SeedUserAsync(t, "publisher", MembershipTypes.Employee);
        await world.AssignAsync(t, "publisher", "brand_publisher", "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task A_design_is_checked_reviewed_by_someone_else_and_applied_into_a_brand_draft()
    {
        var t = await FreshAsync();
        var manager = t["manager"].Client;
        var publisher = t["publisher"].Client;
        var tokens = new Dictionary<string, object> { ["color.accent.base"] = Token("#0b5c8a", "#7cc4ef"), ["radius.md"] = Token("10px") };

        var created = await OkAsync(manager.PostAsJsonAsync(Designs, Design("harbour", tokens)), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("draft", created.GetProperty("state").GetString());
        Assert.Equal("atlas", created.GetProperty("base").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeDesignSaved, id.ToString());

        // The design picks no design of its own, takes only schema tokens and typed values, and keys are unique.
        Assert.Equal("validation.theme.design_token", await KeyAsync(await manager.PostAsJsonAsync(Designs,
            Design("other", new Dictionary<string, object> { ["design.preset"] = Token("pulse") }))));
        Assert.Equal("validation.theme.color_invalid", await KeyAsync(await manager.PostAsJsonAsync(Designs,
            Design("evil", new Dictionary<string, object> { ["color.accent.base"] = Token("red;}body{display:none") }))));
        Assert.Equal("validation.theme.design_base", await KeyAsync(await manager.PostAsJsonAsync(Designs, Design("odd", tokens, "neon"))));
        Assert.Equal("validation.theme.design_key", await KeyAsync(await manager.PostAsJsonAsync(Designs, Design("Bad Key", tokens))));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(Designs, Design("harbour", tokens))).StatusCode);

        // White text on light surfaces is refused at proposal, not at save.
        var unreadable = await OkAsync(manager.PostAsJsonAsync(Designs,
            Design("glare", new Dictionary<string, object> { ["color.text.primary"] = Token("#fdfdfd") })), HttpStatusCode.Created);
        var refused = await manager.PostAsync($"{Designs}/{unreadable.GetProperty("id").GetGuid()}/propose", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("validation.theme.design_contrast", await KeyAsync(refused));

        // An approved design only can be applied.
        var brand = (await OkAsync(t["admin"].Client.GetAsync(Brands))).EnumerateArray().First().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"{Designs}/{id}/apply", new { brandId = brand })).StatusCode);

        var proposed = await OkAsync(manager.PostAsync($"{Designs}/{id}/propose", null));
        Assert.Equal("proposed", proposed.GetProperty("state").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeDesignProposed, id.ToString());
        // Someone who does not publish themes cannot review it, and nobody reviews their own proposal.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"{Designs}/{id}/review", new { approve = true })).StatusCode);
        var own = await OkAsync(publisher.PostAsJsonAsync(Designs, Design("own_pick", tokens)), HttpStatusCode.Created);
        await OkAsync(publisher.PostAsync($"{Designs}/{own.GetProperty("id").GetGuid()}/propose", null));
        var self = await publisher.PostAsJsonAsync($"{Designs}/{own.GetProperty("id").GetGuid()}/review", new { approve = true });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("authz.denied.design_own_proposal", await KeyAsync(self));
        var ownId = own.GetProperty("id").GetGuid();
        var sentBack = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Designs}/{ownId}/review", new { approve = false, note = "Too close to Atlas" }));
        Assert.Equal("rejected", sentBack.GetProperty("state").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeDesignRejected, ownId.ToString());

        var approved = await OkAsync(publisher.PostAsJsonAsync($"{Designs}/{id}/review", new { approve = true, note = "Fine for the season" }));
        Assert.Equal("approved", approved.GetProperty("state").GetString());
        Assert.Equal("Fine for the season", approved.GetProperty("reviewNote").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeDesignApproved, id.ToString());

        // Applying fills the brand draft with the base design and the values; nothing is live until it is published.
        var before = await t["employee"].Client.GetStringAsync("/api/v1/experience/theme.css?mode=light&density=comfortable");
        var draft = await OkAsync(manager.PostAsJsonAsync($"{Designs}/{id}/apply", new { brandId = brand }));
        Assert.True(draft.GetProperty("hasDraft").GetBoolean());
        var drafted = draft.GetProperty("tokens");
        Assert.Equal("atlas", drafted.GetProperty("design.preset").GetProperty("$value").GetString());
        Assert.Equal("#0b5c8a", drafted.GetProperty("color.accent.base").GetProperty("$value").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeDesignApplied, id.ToString());
        Assert.Equal(before, await t["employee"].Client.GetStringAsync("/api/v1/experience/theme.css?mode=light&density=comfortable"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await manager.PostAsJsonAsync($"{Designs}/{id}/apply", new { brandId = Guid.NewGuid() })).StatusCode);

        // Changing an approved design sends it back to draft; retiring needs the right to publish.
        var changed = await OkAsync(manager.PutAsJsonAsync($"{Designs}/{id}", Design("harbour", new Dictionary<string, object> { ["radius.md"] = Token("12px") })));
        Assert.Equal("draft", changed.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"{Designs}/{id}/apply", new { brandId = brand })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"{Designs}/{id}/retire", null)).StatusCode);
        Assert.Equal("retired", (await OkAsync(publisher.PostAsync($"{Designs}/{id}/retire", null))).GetProperty("state").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeDesignRetired, id.ToString());

        // An employee is not shown designs.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync(Designs)).StatusCode);
    }

    [Fact]
    public async Task Without_the_package_feature_nothing_is_made_and_another_tenant_never_sees_a_design()
    {
        var plain = await FreshAsync("workplace_plus");
        var list = await OkAsync(plain["manager"].Client.GetAsync(Designs));
        Assert.False(list.GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["manager"].Client.PostAsJsonAsync(Designs,
            Design("harbour", new Dictionary<string, object> { ["radius.md"] = Token("10px") }))).StatusCode);

        var t = await FreshAsync();
        var created = await OkAsync(t["manager"].Client.PostAsJsonAsync(Designs,
            Design("harbour", new Dictionary<string, object> { ["radius.md"] = Token("10px") })), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Empty((await OkAsync(plain["admin"].Client.GetAsync(Designs))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await plain["admin"].Client.GetAsync($"{Designs}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await plain["admin"].Client.PostAsync($"{Designs}/{id}/retire", null)).StatusCode);
    }
}
