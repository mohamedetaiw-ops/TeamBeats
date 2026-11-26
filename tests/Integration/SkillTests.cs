using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.People;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 010 R2 T013 (US6, FR-018, FR-019, FR-031): the tenant keeps a skill list; people declare skills with a
/// proficiency, colleagues endorse them and the owner is told and may hide one; Find an expert ranks by proficiency and
/// visible endorsements. Skills off hides everything and deletes it after 90 days; Workplace has no skills; another
/// tenant finds nothing. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SkillTests(TenantWorld world)
{
    private const string People = "/api/v1/people";
    private const string Me = "/api/v1/me";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        if (problem.TryGetProperty("detailKey", out var key))
        {
            return key.GetString()!;
        }
        return problem.GetProperty("errors").EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
    }

    private static object Names(string en, string ar) => new Dictionary<string, string> { ["en"] = en, ["ar"] = ar };

    private static async Task<JsonElement> SetListAsync(HttpClient admin, object body)
    {
        var current = await admin.GetAsync($"{People}/skills");
        var put = new HttpRequestMessage(HttpMethod.Put, $"{People}/skills") { Content = JsonContent.Create(body) };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        return await OkAsync(admin.SendAsync(put));
    }

    private static Guid SkillId(JsonElement list, string en) =>
        list.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("names").GetProperty("en").GetString() == en).GetProperty("id").GetGuid();

    private async Task<(TestTenant T, Guid Legal, Guid Excel)> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sk-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "laila", "omar", "kofi", "sara" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        if (package == "workplace")
        {
            return (t, Guid.Empty, Guid.Empty);
        }
        var list = await SetListAsync(t["admin"].Client, new { skills = new[] { new { names = Names("Legal translation", "الترجمة القانونية") }, new { names = Names("Excel", "إكسل") } } });
        return (t, SkillId(list, "Legal translation"), SkillId(list, "Excel"));
    }

    [Fact]
    public async Task People_declare_skills_colleagues_endorse_them_and_experts_rank_by_proficiency()
    {
        var (t, legal, excel) = await SetupAsync();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PeopleSkillListChanged);

        // Only people administrators keep the list.
        var employeeList = await OkAsync(t["omar"].Client.GetAsync($"{People}/skills"));
        Assert.False(employeeList.GetProperty("canManage").GetBoolean());
        Assert.Equal(2, employeeList.GetProperty("skills").GetArrayLength());
        var stale = new HttpRequestMessage(HttpMethod.Put, $"{People}/skills") { Content = JsonContent.Create(new { enabled = false }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"s1\"");
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.SendAsync(stale)).StatusCode);

        await OkAsync(t["laila"].Client.PutAsJsonAsync($"{Me}/skills", new { skills = new[] { new { skillId = (Guid?)legal, name = (string?)null, proficiency = "expert" } } }));
        await OkAsync(t["omar"].Client.PutAsJsonAsync($"{Me}/skills", new { skills = new[] { new { skillId = (Guid?)legal, name = (string?)null, proficiency = "good" } } }));
        await OkAsync(t["kofi"].Client.PutAsJsonAsync($"{Me}/skills", new { skills = new[] { new { skillId = (Guid?)excel, name = (string?)null, proficiency = "basic" } } }));
        Assert.Equal("validation.people.skill_free_text",
            await ProblemAsync(t["kofi"].Client.PutAsJsonAsync($"{Me}/skills", new { skills = new[] { new { skillId = (Guid?)null, name = (string?)"Origami", proficiency = "good" } } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.people.proficiency",
            await ProblemAsync(t["kofi"].Client.PutAsJsonAsync($"{Me}/skills", new { skills = new[] { new { skillId = (Guid?)excel, name = (string?)null, proficiency = "guru" } } }), HttpStatusCode.BadRequest));

        // A colleague endorses once; the owner is told; nobody endorses themselves.
        var laila = t["laila"].Id;
        var endorsed = await OkAsync(t["sara"].Client.PutAsync($"{People}/{laila}/skills/{legal}/endorsement", null));
        var skill = Assert.Single(endorsed.GetProperty("items").EnumerateArray());
        Assert.Equal(1, skill.GetProperty("endorsements").GetInt32());
        Assert.True(skill.GetProperty("endorsedByMe").GetBoolean());
        await OkAsync(t["sara"].Client.PutAsync($"{People}/{laila}/skills/{legal}/endorsement", null));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @user and template_key = 'people.skill.endorsed'", ("user", laila)));
        Assert.Equal("conflict.people.endorse_self", await ProblemAsync(t["laila"].Client.PutAsync($"{People}/{laila}/skills/{legal}/endorsement", null), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.NotFound, (await t["sara"].Client.PutAsync($"{People}/{laila}/skills/{excel}/endorsement", null)).StatusCode);

        // Find an expert: proficiency first, then endorsements, and only people who declared a matching skill.
        var experts = await OkAsync(t["kofi"].Client.GetAsync($"{People}/experts?q=legal"));
        var order = experts.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("person").GetProperty("id").GetGuid()).ToList();
        Assert.Equal([laila, t["omar"].Id], order);
        Assert.Equal(1, experts.GetProperty("items")[0].GetProperty("endorsements").GetInt32());
        Assert.Single((await OkAsync(t["laila"].Client.GetAsync($"{People}/experts?skillId={excel}"))).GetProperty("items").EnumerateArray());

        // The owner hides an endorsement: it no longer counts.
        var mine = await OkAsync(t["laila"].Client.GetAsync($"{Me}/skills"));
        var endorser = Assert.Single(mine.GetProperty("items")[0].GetProperty("endorsers").EnumerateArray());
        Assert.Equal(t["sara"].Id, endorser.GetProperty("id").GetGuid());
        await OkAsync(t["laila"].Client.PostAsync($"{Me}/skills/{legal}/endorsements/{t["sara"].Id}:hide", null));
        Assert.Equal(0, (await OkAsync(t["omar"].Client.GetAsync($"{People}/{laila}/skills"))).GetProperty("items")[0].GetProperty("endorsements").GetInt32());

        // Another tenant does not find the person or their skills.
        var other = await world.CreateTenantAsync("sk-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{People}/{laila}/skills")).StatusCode);
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync($"{People}/experts?q=legal"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Free_text_skills_wait_as_suggestions_until_an_administrator_merges_them()
    {
        var (t, legal, _) = await SetupAsync();
        var admin = t["admin"].Client;
        var list = await SetListAsync(admin, new { allowFreeText = true });
        Assert.True(list.GetProperty("allowFreeText").GetBoolean());
        await OkAsync(t["laila"].Client.PutAsJsonAsync($"{Me}/skills", new { skills = new[] { new { skillId = (Guid?)null, name = (string?)"Contract translation", proficiency = "good" } } }));

        // A suggestion shows to its owner and administrators, not to colleagues or in Find an expert.
        var laila = t["laila"].Id;
        Assert.True(Assert.Single((await OkAsync(t["laila"].Client.GetAsync($"{People}/{laila}/skills"))).GetProperty("items").EnumerateArray()).GetProperty("suggested").GetBoolean());
        Assert.Empty((await OkAsync(t["omar"].Client.GetAsync($"{People}/{laila}/skills"))).GetProperty("items").EnumerateArray());
        Assert.Empty((await OkAsync(t["omar"].Client.GetAsync($"{People}/experts?q=contract"))).GetProperty("items").EnumerateArray());
        var adminList = await OkAsync(admin.GetAsync($"{People}/skills"));
        var suggestion = adminList.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("status").GetString() == "suggested").GetProperty("id").GetGuid();

        // Merged into Legal translation: Laila now has it, at her proficiency, and the suggestion is gone.
        var merged = await OkAsync(admin.PostAsJsonAsync($"{People}/skills/{suggestion}:merge", new { intoId = legal }));
        Assert.DoesNotContain(merged.GetProperty("skills").EnumerateArray(), s => s.GetProperty("id").GetGuid() == suggestion);
        var skill = Assert.Single((await OkAsync(t["omar"].Client.GetAsync($"{People}/{laila}/skills"))).GetProperty("items").EnumerateArray());
        Assert.Equal(legal, skill.GetProperty("skillId").GetGuid());
        Assert.Equal("good", skill.GetProperty("proficiency").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.PostAsJsonAsync($"{People}/skills/{legal}:merge", new { intoId = suggestion })).StatusCode);
    }

    [Fact]
    public async Task Skills_turned_off_are_hidden_and_deleted_after_90_days_and_Workplace_has_none()
    {
        var (t, legal, _) = await SetupAsync();
        var laila = t["laila"].Id;
        await OkAsync(t["laila"].Client.PutAsJsonAsync($"{Me}/skills", new { skills = new[] { new { skillId = (Guid?)legal, name = (string?)null, proficiency = "expert" } } }));

        await SetListAsync(t["admin"].Client, new { enabled = false });
        Assert.False((await OkAsync(t["omar"].Client.GetAsync($"{People}/{laila}/skills"))).GetProperty("enabled").GetBoolean());
        Assert.Empty((await OkAsync(t["omar"].Client.GetAsync($"{People}/{laila}/skills"))).GetProperty("items").EnumerateArray());
        Assert.False((await OkAsync(t["omar"].Client.GetAsync($"{People}/experts?q=legal"))).GetProperty("enabled").GetBoolean());
        Assert.Equal("conflict.people.skills_off",
            await ProblemAsync(t["laila"].Client.PutAsJsonAsync($"{Me}/skills", new { skills = Array.Empty<object>() }), HttpStatusCode.Conflict));

        // Turned on again within 90 days, everything is back.
        await SetListAsync(t["admin"].Client, new { enabled = true });
        Assert.Single((await OkAsync(t["omar"].Client.GetAsync($"{People}/{laila}/skills"))).GetProperty("items").EnumerateArray());

        // Off for more than 90 days: the maintenance job deletes it all.
        await SetListAsync(t["admin"].Client, new { enabled = false });
        var deleted = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var early = await sp.GetRequiredService<Skills>().PurgeAsync(DateTimeOffset.UtcNow.AddDays(89), CancellationToken.None);
            var late = await sp.GetRequiredService<Skills>().PurgeAsync(DateTimeOffset.UtcNow.AddDays(91), CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return (early, late);
        });
        Assert.Equal(0, deleted.early);
        Assert.Equal(2, deleted.late);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from people.person_skill"));

        var (w, _, _) = await SetupAsync("workplace");
        Assert.False((await OkAsync(w["admin"].Client.GetAsync($"{People}/skills"))).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await w["laila"].Client.PutAsJsonAsync($"{Me}/skills", new { skills = Array.Empty<object>() })).StatusCode);
    }
}
