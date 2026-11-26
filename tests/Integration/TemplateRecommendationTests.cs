using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R3 FR-023 (T033): template recommendations from the page's purpose, viewport and density. They are advice
/// only: nothing is created or changed, a template switched off is never recommended, the facts are checked and only
/// people who may edit pages ask.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TemplateRecommendationTests(TenantWorld world)
{
    private const string Path = "/api/v1/experience/template-recommendations";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string[] Ids(JsonElement view) =>
        [.. view.GetProperty("recommendations").EnumerateArray().Select(r => r.GetProperty("templateId").GetString()!)];

    [Fact]
    public async Task Recommendations_follow_purpose_viewport_and_density_and_change_nothing()
    {
        var t = await world.CreateTenantAsync("trc-" + Guid.NewGuid().ToString("N")[..6], "enterprise_gov");
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        var admin = t["admin"].Client;
        var pagesBefore = (await OkAsync(admin.GetAsync("/api/v1/experience/pages"))).GetArrayLength();

        var news = await OkAsync(admin.GetAsync($"{Path}?purpose=news&viewport=desktop&density=heavy"));
        Assert.Equal(["T06", "T04", "T12"], Ids(news));
        Assert.All(news.GetProperty("recommendations").EnumerateArray(), r => Assert.Equal("compact", r.GetProperty("variant").GetString()));
        Assert.Contains("purpose", news.GetProperty("recommendations")[0].GetProperty("reasons").EnumerateArray().Select(x => x.GetString()));

        // On phones a one column layout of the same purpose moves up.
        Assert.Equal(["T09", "T12", "T02"], Ids(await OkAsync(admin.GetAsync($"{Path}?purpose=knowledge&viewport=desktop&density=light"))));
        var phone = await OkAsync(admin.GetAsync($"{Path}?purpose=knowledge&viewport=mobile&density=light"));
        Assert.Equal(["T09", "T02", "T12"], Ids(phone));
        Assert.Equal("spacious", phone.GetProperty("recommendations")[0].GetProperty("variant").GetString());

        // Defaults when nothing is said, and the facts are checked.
        Assert.Equal("general", (await OkAsync(admin.GetAsync(Path))).GetProperty("purpose").GetString());
        var bad = await admin.GetAsync($"{Path}?purpose=everything&viewport=watch&density=huge");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadAsStringAsync();
        Assert.Contains("validation.experience.recommendation_purpose", body);
        Assert.Contains("validation.experience.recommendation_viewport", body);
        Assert.Contains("validation.experience.recommendation_density", body);

        // A template switched off for the tenant is never recommended.
        Assert.True((await admin.PutAsJsonAsync("/api/v1/experience/feature-controls",
            new { subjectKind = "template", subjectId = "T06", orgUnitId = (Guid?)null, audienceId = (Guid?)null, enabled = false })).IsSuccessStatusCode);
        Assert.DoesNotContain("T06", Ids(await OkAsync(admin.GetAsync($"{Path}?purpose=news"))));

        // Advice only: no page appeared, and people who cannot edit pages are refused.
        Assert.Equal(pagesBefore, (await OkAsync(admin.GetAsync("/api/v1/experience/pages"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync($"{Path}?purpose=news")).StatusCode);
    }
}
