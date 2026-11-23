using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 R2 T019 (US4 scenario 3, FR-020): delegated experience administration. A delegate for Acme Gulf's brand and
/// pages works there and below, is refused in Acme Africa and on a value Holding locked, loses the rights when the area
/// is taken back, and nobody delegates what they do not hold, outside their tree, without the package, or to a guest.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ExperienceDelegationTests(TenantWorld world)
{
    private const string Delegations = "/api/v1/experience/delegations";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("xdl-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Plant", gulf, "department");
        await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var (name, unit) in new[] { ("del", "Gulf"), ("gulfadmin", "Gulf"), ("staff", "Gulf") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "gulfadmin", "organization_administrator", "org_subtree", gulf);
        return t;
    }

    private static Task<HttpResponseMessage> DelegateAsync(TestTenant t, string user, Guid unit, string to, params string[] areas) =>
        t[user].Client.PutAsJsonAsync($"{Delegations}/{unit}/{t[to].Id}", new { areas });

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static Task<HttpResponseMessage> OverrideAsync(TestTenant t, string user, Guid unit, string token, string value) =>
        t[user].Client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{unit}",
            new { brandId = (Guid?)null, overrides = new Dictionary<string, object> { [token] = new Dictionary<string, object> { ["$value"] = value } } });

    [Fact]
    public async Task A_delegate_works_in_their_organization_and_is_refused_next_door_and_on_locked_values()
    {
        var t = await FreshAsync();
        var (gulf, africa, plant) = (t.Unit("Gulf"), t.Unit("Africa"), t.Unit("Plant"));
        Assert.Equal(HttpStatusCode.NotFound, (await OverrideAsync(t, "del", gulf, "color.accent.base", "#0f766e")).StatusCode);

        var given = await DelegateAsync(t, "admin", gulf, "del", "brand", "pages");
        Assert.True(given.IsSuccessStatusCode, await given.Content.ReadAsStringAsync());
        var view = await given.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["brand", "pages"], view.GetProperty("areas").EnumerateArray().Select(a => a.GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ExperienceDelegationChanged, $"{gulf}:{t["del"].Id}");

        // Gulf and the plant below it: the delegate overrides the brand and makes pages.
        Assert.True((await OverrideAsync(t, "del", gulf, "color.accent.base", "#0f766e")).IsSuccessStatusCode);
        Assert.True((await OverrideAsync(t, "del", plant, "color.accent.base", "#115e59")).IsSuccessStatusCode);
        var page = await t["del"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = "dl-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = gulf });
        Assert.Equal(HttpStatusCode.Created, page.StatusCode);

        // Africa is a neighbour: refused, without revealing it.
        Assert.Equal(HttpStatusCode.NotFound, (await OverrideAsync(t, "del", africa, "color.accent.base", "#0f766e")).StatusCode);
        var far = await t["del"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = "dl-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = africa });
        Assert.False(far.IsSuccessStatusCode);
        // Nor may a delegate delegate further: that needs roles.assign.
        Assert.Equal(HttpStatusCode.NotFound, (await DelegateAsync(t, "del", gulf, "staff", "pages")).StatusCode);

        // A value Holding locked stays as Holding set it.
        Assert.True((await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/locks/{t.Unit("Holding")}", new { settings = new[] { "color.accent.base" } })).IsSuccessStatusCode);
        Assert.Equal("validation.theme.token_locked", await KeyAsync(await OverrideAsync(t, "del", gulf, "color.accent.base", "#134e4a")));

        // Taking the brand back leaves the pages; listing shows what is left.
        var reduced = await DelegateAsync(t, "admin", gulf, "del", "pages");
        Assert.Equal(["pages"], (await reduced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("areas").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await OverrideAsync(t, "del", gulf, "color.surface.base", "#f0fdfa")).StatusCode);
        var list = await t["admin"].Client.GetFromJsonAsync<JsonElement>(Delegations);
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal(t["del"].Id, entry.GetProperty("userId").GetGuid());

        // No areas ends the delegation.
        Assert.Equal(HttpStatusCode.NoContent, (await DelegateAsync(t, "admin", gulf, "del")).StatusCode);
        Assert.Empty((await t["admin"].Client.GetFromJsonAsync<JsonElement>(Delegations)).EnumerateArray());
        var after = await t["del"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = "dl-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = gulf });
        Assert.False(after.IsSuccessStatusCode);
    }

    [Fact]
    public async Task An_organization_administrator_delegates_only_inside_their_tree_and_a_held_role_survives_removal()
    {
        var t = await FreshAsync();
        var (gulf, africa) = (t.Unit("Gulf"), t.Unit("Africa"));
        Assert.True((await DelegateAsync(t, "gulfadmin", gulf, "del", "navigation")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DelegateAsync(t, "gulfadmin", africa, "del", "navigation")).StatusCode);
        // Delegating at Holding would cover Africa too, which the Gulf administrator does not hold.
        Assert.Equal(HttpStatusCode.NotFound, (await DelegateAsync(t, "gulfadmin", t.Unit("Holding"), "del", "navigation")).StatusCode);
        // They see only their own organizations' delegations.
        Assert.Single((await t["gulfadmin"].Client.GetFromJsonAsync<JsonElement>(Delegations)).EnumerateArray());

        // A role the person already held there is not taken away with the delegation.
        await world.AssignAsync(t, "staff", "page_publisher", "org_subtree", gulf);
        Assert.True((await DelegateAsync(t, "admin", gulf, "staff", "pages")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DelegateAsync(t, "admin", gulf, "staff")).StatusCode);
        var page = await t["staff"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = "dl-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = gulf });
        Assert.Equal(HttpStatusCode.Created, page.StatusCode);
    }

    [Fact]
    public async Task Delegations_are_checked_need_the_package_and_stay_in_the_tenant()
    {
        var t = await FreshAsync();
        var gulf = t.Unit("Gulf");
        Assert.Equal("validation.experience.delegation_area", await KeyAsync(await DelegateAsync(t, "admin", gulf, "del", "templates")));
        Assert.Equal("validation.experience.delegation_self", await KeyAsync(await DelegateAsync(t, "admin", gulf, "admin", "pages")));
        await world.SeedUserAsync(t, "visitor", MembershipTypes.Guest);
        Assert.Equal("validation.experience.delegation_user", await KeyAsync(await DelegateAsync(t, "admin", gulf, "visitor", "pages")));
        Assert.Equal("validation.experience.delegation_user",
            await KeyAsync(await t["admin"].Client.PutAsJsonAsync($"{Delegations}/{gulf}/{Guid.NewGuid()}", new { areas = new[] { "pages" } })));
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.PutAsJsonAsync($"{Delegations}/{Guid.NewGuid()}/{t["del"].Id}", new { areas = new[] { "pages" } })).StatusCode);
        // Another tenant cannot reach this tenant's organization, and an employee cannot list delegations.
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.PutAsJsonAsync($"{Delegations}/{gulf}/{t["del"].Id}", new { areas = new[] { "pages" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["staff"].Client.GetAsync(Delegations)).StatusCode);

        // Workplace has no delegated administration.
        var basic = await world.CreateTenantAsync("xdb-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var unit = await world.CreateUnitAsync(basic, "Solo", null, "organization");
        await world.SeedUserAsync(basic, "del", MembershipTypes.Employee);
        var refused = await basic["admin"].Client.PutAsJsonAsync($"{Delegations}/{unit}/{basic["del"].Id}", new { areas = new[] { "pages" } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("org.delegated_admin", await refused.Content.ReadAsStringAsync());
    }
}
