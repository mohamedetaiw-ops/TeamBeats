using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 R2 US5 scenarios 3 and 4 (T029): AI translation makes a draft variant marked machine translated that keeps
/// the structure, marks and mentions of the source; nothing marked is published until a person approves or edits it;
/// confidential content is blocked by the default AI policy; the plan, the item's editors and the tenant bound it.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentTranslationTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<string?> ProblemKeyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString();
        }
        return problem.TryGetProperty("detailKey", out var key) ? key.GetString() : problem.GetProperty("code").GetString();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ctr-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "pia", "mia", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        return t;
    }

    private static object Item(Guid mention, string classification = "internal") => new
    {
        type = "news", classification,
        variants = new Dictionary<string, object>
        {
            ["en"] = new
            {
                title = "Canteen opens", summary = "New hours",
                body = new
                {
                    blocks = new object[]
                    {
                        new { type = "paragraph", children = new object[] { new { text = "Lunch is ", marks = new[] { "bold" } }, new { text = "Mia", mention }, new { text = " at noon #food" } } },
                        new { type = "list", items = new[] { new object[] { new { text = "Soup" } }, new object[] { new { text = "Salad" } } } },
                    },
                },
            },
        },
    };

    [Fact]
    public async Task A_machine_translation_keeps_the_structure_and_waits_for_a_person()
    {
        var t = await TenantAsync();
        var pia = t["pia"].Client;
        var id = Id(await OkAsync(pia.PostAsJsonAsync(Items, Item(t["mia"].Id)), HttpStatusCode.Created));

        var result = await OkAsync(pia.PostAsJsonAsync($"{Items}/{id}/translate", new { locale = "ar" }));
        Assert.Equal("translated", result.GetProperty("outcome").GetString());
        var ar = result.GetProperty("item").GetProperty("variants").GetProperty("ar");
        Assert.True(ar.GetProperty("machineTranslated").GetBoolean());
        Assert.Equal("مترجم: Canteen opens", ar.GetProperty("title").GetString());
        Assert.Equal("مترجم: New hours", ar.GetProperty("summary").GetString());
        var runs = ar.GetProperty("body").GetProperty("blocks")[0].GetProperty("children");
        Assert.Equal("مترجم: Lunch is ", runs[0].GetProperty("text").GetString());
        Assert.Equal("bold", runs[0].GetProperty("marks")[0].GetString());
        Assert.Equal("Mia", runs[1].GetProperty("text").GetString()); // A mention is a person, not text to translate.
        Assert.Equal(t["mia"].Id, runs[1].GetProperty("mention").GetGuid());
        Assert.Equal("مترجم: Salad", ar.GetProperty("body").GetProperty("blocks")[1].GetProperty("items")[1][0].GetProperty("text").GetString());
        Assert.False(result.GetProperty("item").GetProperty("variants").GetProperty("en").GetProperty("machineTranslated").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentTranslated, id.ToString());

        // Not published while marked; a person approves it, then it goes live.
        Assert.Equal("conflict.content.machine_translated", await ProblemKeyAsync(await pia.PostAsJsonAsync($"{Items}/{id}/publish", new { }), HttpStatusCode.Conflict));
        var approved = await OkAsync(pia.PostAsync($"{Items}/{id}/translations/ar/approve", null));
        Assert.False(approved.GetProperty("variants").GetProperty("ar").GetProperty("machineTranslated").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentTranslationApproved, id.ToString());
        await OkAsync(pia.PostAsJsonAsync($"{Items}/{id}/publish", new { }));

        // A fresh translation of the live item starts a draft; a person's edit of that variant clears the mark too.
        var again = await OkAsync(pia.PostAsJsonAsync($"{Items}/{id}/translate", new { locale = "ar" }));
        var variants = again.GetProperty("item").GetProperty("variants");
        Assert.True(variants.GetProperty("ar").GetProperty("machineTranslated").GetBoolean());
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(variants.GetRawText())!
            .ToDictionary(v => v.Key, v => (object)(v.Key == "ar"
                ? new { title = (string?)"المقصف يفتح", summary = v.Value.GetProperty("summary").GetString(), body = v.Value.GetProperty("body") }
                : new { title = v.Value.GetProperty("title").GetString(), summary = v.Value.GetProperty("summary").GetString(), body = v.Value.GetProperty("body") }));
        var saved = await OkAsync(pia.PutAsJsonAsync($"{Items}/{id}", new { variants = edited }));
        Assert.False(saved.GetProperty("variants").GetProperty("ar").GetProperty("machineTranslated").GetBoolean());
        Assert.Equal("conflict.content.not_machine_translated", await ProblemKeyAsync(await pia.PostAsync($"{Items}/{id}/translations/ar/approve", null), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Policy_plan_people_and_tenants_bound_translation()
    {
        var t = await TenantAsync();
        var pia = t["pia"].Client;

        // Confidential content does not go to a model under the default AI policy, and nothing changes.
        var secret = Id(await OkAsync(pia.PostAsJsonAsync(Items, Item(t["mia"].Id, "confidential")), HttpStatusCode.Created));
        var blocked = await OkAsync(pia.PostAsJsonAsync($"{Items}/{secret}/translate", new { locale = "ar" }));
        Assert.Equal("policy_blocked", blocked.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, blocked.GetProperty("item").ValueKind);
        Assert.False((await OkAsync(pia.GetAsync($"{Items}/{secret}"))).GetProperty("variants").TryGetProperty("ar", out _));

        var id = Id(await OkAsync(pia.PostAsJsonAsync(Items, Item(t["mia"].Id)), HttpStatusCode.Created));
        Assert.Equal("validation.content.translate_locale", await ProblemKeyAsync(await pia.PostAsJsonAsync($"{Items}/{id}/translate", new { locale = "en" }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.content.translate_locale", await ProblemKeyAsync(await pia.PostAsJsonAsync($"{Items}/{id}/translate", new { locale = "fr" }), HttpStatusCode.BadRequest));
        // A reader cannot translate, and another tenant does not find the item.
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.PostAsJsonAsync($"{Items}/{id}/translate", new { locale = "ar" })).StatusCode);
        var other = await world.CreateTenantAsync("ctr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{Items}/{id}/translate", new { locale = "ar" })).StatusCode);

        // The Workplace plan has no AI translation.
        var small = await TenantAsync("workplace");
        var item = Id(await OkAsync(small["pia"].Client.PostAsJsonAsync(Items, Item(small["mia"].Id)), HttpStatusCode.Created));
        Assert.Equal(HttpStatusCode.Forbidden, (await small["pia"].Client.PostAsJsonAsync($"{Items}/{item}/translate", new { locale = "ar" })).StatusCode);
    }
}
