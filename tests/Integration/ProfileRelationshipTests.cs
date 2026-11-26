using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 010 R2 T015 (US8, FR-028 to FR-030): the own profile shows how complete it is and the missing items until it is
/// complete or dismissed for 90 days; people administrators record assistants, shown on both profiles and audited; an
/// active spec 016 delegation shows the delegate and the last day only. Another tenant changes nothing. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ProfileRelationshipTests(TenantWorld world)
{
    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ValidationKeyAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errors").EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
    }

    private async Task<TestTenant> SetupAsync()
    {
        var t = await world.CreateTenantAsync("pr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "laila", "omar", "sara", "kofi", "amal" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        return t;
    }

    [Fact]
    public async Task The_own_profile_shows_what_is_missing_until_complete_or_dismissed()
    {
        var t = await SetupAsync();
        var laila = t["laila"].Client;
        var completeness = (await OkAsync(laila.GetAsync("/api/v1/me/profile"))).GetProperty("completeness");
        Assert.True(completeness.GetProperty("show").GetBoolean());
        var missing = completeness.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Contains("photo", missing);
        Assert.Contains("skills", missing);
        var before = completeness.GetProperty("percent").GetInt32();
        Assert.InRange(before, 0, 99);

        // Adding a skill fills that item.
        var admin = t["admin"].Client;
        var list = await admin.GetAsync("/api/v1/people/skills");
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/people/skills")
        {
            Content = JsonContent.Create(new { skills = new[] { new { names = new Dictionary<string, string> { ["en"] = "Budgeting" } } } }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", list.Headers.ETag!.Tag);
        var skill = (await OkAsync(admin.SendAsync(put))).GetProperty("skills")[0].GetProperty("id").GetGuid();
        await OkAsync(laila.PutAsJsonAsync("/api/v1/me/skills", new { skills = new[] { new { skillId = skill, proficiency = "good" } } }));
        completeness = (await OkAsync(laila.GetAsync("/api/v1/me/profile"))).GetProperty("completeness");
        Assert.DoesNotContain("skills", completeness.GetProperty("missing").EnumerateArray().Select(m => m.GetString()));
        Assert.True(completeness.GetProperty("percent").GetInt32() > before);

        // Dismissed: hidden for 90 days, the figures stay.
        completeness = (await OkAsync(laila.PostAsync("/api/v1/me/profile/completeness:dismiss", null))).GetProperty("completeness");
        Assert.False(completeness.GetProperty("show").GetBoolean());
        Assert.True(completeness.GetProperty("dismissedUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(89));
    }

    [Fact]
    public async Task Administrators_record_assistants_shown_on_both_profiles()
    {
        var t = await SetupAsync();
        var (laila, sara) = (t["laila"].Id, t["sara"].Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.PutAsJsonAsync($"/api/v1/people/{laila}/assistants", new { assistantIds = new[] { sara } })).StatusCode);
        Assert.Equal("validation.people.assistant_self", await ValidationKeyAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{laila}/assistants", new { assistantIds = new[] { laila } })));
        Assert.Equal("validation.people.assistants_count", await ValidationKeyAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{laila}/assistants",
            new { assistantIds = new[] { sara, t["omar"].Id, t["kofi"].Id, t["amal"].Id } })));
        Assert.Equal("validation.people.assistant_unknown", await ValidationKeyAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{laila}/assistants",
            new { assistantIds = new[] { Guid.NewGuid() } })));

        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{laila}/assistants", new { assistantIds = new[] { sara } }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PeopleAssistantSet);
        var profile = await OkAsync(t["kofi"].Client.GetAsync($"/api/v1/people/{laila}"));
        Assert.Equal(sara, Assert.Single(profile.GetProperty("assistants").EnumerateArray()).GetProperty("id").GetGuid());
        var saraProfile = await OkAsync(t["kofi"].Client.GetAsync($"/api/v1/people/{sara}"));
        Assert.Equal(laila, Assert.Single(saraProfile.GetProperty("assistantOf").EnumerateArray()).GetProperty("id").GetGuid());

        // Another tenant's administrator does not find the person.
        var other = await world.CreateTenantAsync("pr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PutAsJsonAsync($"/api/v1/people/{laila}/assistants", new { assistantIds = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task An_active_delegation_shows_the_delegate_and_last_day_only()
    {
        var t = await SetupAsync();
        var (laila, omar) = (t["laila"].Id, t["omar"].Id);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // A delegation that starts tomorrow is not shown yet.
        await OkAsync(t["laila"].Client.PutAsJsonAsync("/api/v1/me/delegation", new { delegateId = omar, startsOn = today.AddDays(2), endsOn = today.AddDays(9) }));
        Assert.Equal(JsonValueKind.Null, (await OkAsync(t["kofi"].Client.GetAsync($"/api/v1/people/{laila}"))).GetProperty("delegation").ValueKind);

        await OkAsync(t["laila"].Client.PutAsJsonAsync("/api/v1/me/delegation", new { delegateId = omar, startsOn = today.AddDays(-1), endsOn = today.AddDays(5) }));
        var delegation = (await OkAsync(t["kofi"].Client.GetAsync($"/api/v1/people/{laila}"))).GetProperty("delegation");
        Assert.Equal(omar, delegation.GetProperty("delegate").GetProperty("id").GetGuid());
        Assert.Equal(today.AddDays(5).ToString("yyyy-MM-dd"), delegation.GetProperty("until").GetString());
        Assert.Equal(["delegate", "until"], delegation.EnumerateObject().Select(p => p.Name).ToList());
    }
}
