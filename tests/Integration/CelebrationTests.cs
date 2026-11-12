using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.People;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T013 (US6, FR-015, FR-016): birthdays only for people who opt in, stored as day and month with no year
/// and cleared on opt-out; work anniversaries from the profile, off until the tenant turns them on and with a personal
/// opt-out; joiners from the people newcomers rules; the viewer's units by default; greetings only on the day, once
/// per colleague; settings with If-Match; tenant isolation. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class CelebrationTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString()!)).ToArray()
            : [];
    }

    /// <summary>Today in the test tenants' time zone (Asia/Dubai), as the service counts it: after 20:00 UTC it is already tomorrow there.</summary>
    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Dubai"));

    private async Task<TestTenant> SetupAsync()
    {
        var t = await world.CreateTenantAsync("ce-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.CreateUnitAsync(t, "Finance", null, "department");
        await world.CreateUnitAsync(t, "Sales", null, "department");
        foreach (var name in new[] { "laila", "ahmed", "kofi", "mona" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "laila", "Finance");
        await world.AddMembershipAsync(t, "ahmed", "Finance");
        await world.AddMembershipAsync(t, "kofi", "Sales");
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            foreach (var name in new[] { "laila", "ahmed", "kofi", "mona" })
            {
                await sp.GetRequiredService<ProfileStore>().RefreshAsync(t[name].Id, CancellationToken.None);
            }
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        // Everyone joined long ago, so nobody counts as a new joiner unless a test says so.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update people.profile set hire_date = date '2015-03-10', first_seen = date '2015-03-10'");
        return t;
    }

    private static Task<HttpResponseMessage> BirthdayAsync(HttpClient client, int? month, int? day, bool? anniversaryOptOut = null) =>
        client.PutAsJsonAsync($"{Api}/celebrations/preferences", new { birthdayMonth = month, birthdayDay = day, anniversaryOptOut });

    private static async Task<JsonElement> ListAsync(HttpClient client, string? scope = null) =>
        await OkAsync(client.GetAsync($"{Api}/celebrations{(scope is null ? "" : $"?scope={scope}")}"));

    private static IEnumerable<(Guid Id, string Kind)> Entries(JsonElement list, string part) =>
        list.GetProperty(part).EnumerateArray().Select(e => (e.GetProperty("person").GetProperty("id").GetGuid(), e.GetProperty("kind").GetString()!));

    private static async Task SetSettingsAsync(TestTenant t, object settings)
    {
        var current = await t["admin"].Client.GetAsync($"{Api}/settings/celebrations");
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/celebrations") { Content = JsonContent.Create(settings) };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        await OkAsync(t["admin"].Client.SendAsync(request));
    }

    [Fact]
    public async Task Birthdays_show_only_for_people_who_opted_in_and_never_keep_a_year()
    {
        var t = await SetupAsync();
        Assert.Contains("validation.engagement.celebration_birthday", await ErrorKeysAsync(await BirthdayAsync(t["laila"].Client, 2, 30)));
        Assert.Contains("validation.engagement.celebration_birthday", await ErrorKeysAsync(await BirthdayAsync(t["laila"].Client, 4, null)));
        await OkAsync(BirthdayAsync(t["laila"].Client, Today.Month, Today.Day));
        var soon = Today.AddDays(3);
        await OkAsync(BirthdayAsync(t["kofi"].Client, soon.Month, soon.Day));

        // Ahmed is in Finance with Laila: he sees her today and can greet her; Kofi is in Sales, so only on the whole list.
        var mine = await ListAsync(t["ahmed"].Client);
        Assert.Equal("unit", mine.GetProperty("scope").GetString());
        Assert.Contains((t["laila"].Id, "birthday"), Entries(mine, "today"));
        Assert.DoesNotContain(Entries(mine, "upcoming"), e => e.Id == t["kofi"].Id);
        Assert.True(mine.GetProperty("today")[0].GetProperty("canGreet").GetBoolean());
        var all = await ListAsync(t["ahmed"].Client, "all");
        Assert.Contains((t["kofi"].Id, "birthday"), Entries(all, "upcoming"));
        Assert.Equal(400, (int)(await t["ahmed"].Client.GetAsync($"{Api}/celebrations?scope=planet")).StatusCode);
        // Nobody else's birthday shows, and Laila cannot greet herself.
        Assert.DoesNotContain(Entries(all, "today"), e => e.Id == t["mona"].Id);
        Assert.False((await ListAsync(t["laila"].Client)).GetProperty("today")[0].GetProperty("canGreet").GetBoolean());

        // Greetings: on the day only, once per colleague however often they press.
        await OkAsync(t["ahmed"].Client.PostAsJsonAsync($"{Api}/celebrations/{t["laila"].Id}/greetings", new { kind = "birthday" }), HttpStatusCode.NoContent);
        await OkAsync(t["ahmed"].Client.PostAsJsonAsync($"{Api}/celebrations/{t["laila"].Id}/greetings", new { kind = "birthday" }), HttpStatusCode.NoContent);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from notifications.notification where user_id = '{t["laila"].Id}' and template_key = 'engagement.celebration.birthday'"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["ahmed"].Client.PostAsJsonAsync($"{Api}/celebrations/{t["kofi"].Id}/greetings", new { kind = "birthday" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["laila"].Client.PostAsJsonAsync($"{Api}/celebrations/{t["laila"].Id}/greetings", new { kind = "birthday" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["ahmed"].Client.PostAsJsonAsync($"{Api}/celebrations/{t["mona"].Id}/greetings", new { kind = "birthday" })).StatusCode);

        // Only day and month are stored, and opting out clears them.
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from information_schema.columns where table_schema = 'engagement' and table_name = 'celebration_preference' and column_name like '%year%'"));
        await OkAsync(BirthdayAsync(t["laila"].Client, null, null));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from engagement.celebration_preference where user_id = '{t["laila"].Id}' and birthday_month is not null"));
        Assert.DoesNotContain(Entries(await ListAsync(t["ahmed"].Client), "today"), e => e.Id == t["laila"].Id);

        // Birthdays switched off for the tenant hide them all.
        await OkAsync(BirthdayAsync(t["laila"].Client, Today.Month, Today.Day));
        await SetSettingsAsync(t, new { birthdays = false, anniversaries = false, joiners = true });
        Assert.Empty(Entries(await ListAsync(t["ahmed"].Client, "all"), "today"));
    }

    [Fact]
    public async Task Anniversaries_and_joiners_follow_the_tenant_settings_and_personal_choices()
    {
        var t = await SetupAsync();
        await world.Database.OwnerExecInTenantAsync(t.Id, "update people.profile set hire_date = @d where user_id = @u", ("d", Today.AddYears(-5)), ("u", t["laila"].Id));
        await world.Database.OwnerExecInTenantAsync(t.Id, "update people.profile set hire_date = @d where user_id = @u", ("d", Today.AddDays(-3)), ("u", t["ahmed"].Id));

        // Anniversaries are off by default; joiners are on.
        var before = await ListAsync(t["kofi"].Client, "all");
        Assert.DoesNotContain(Entries(before, "today"), e => e.Kind == "anniversary");
        Assert.Contains((t["ahmed"].Id, "joiner"), Entries(before, "joiners"));
        Assert.DoesNotContain(Entries(before, "joiners"), e => e.Id == t["laila"].Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.GetAsync($"{Api}/settings/celebrations")).StatusCode);
        var stale = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/celebrations") { Content = JsonContent.Create(new { birthdays = true, anniversaries = true, joiners = true }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"c99\"");
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.SendAsync(stale)).StatusCode);
        await SetSettingsAsync(t, new { birthdays = true, anniversaries = true, joiners = true });
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from audit.audit_event where action = '{AuditActions.EngagementCelebrationSettingsChanged}'"));

        var after = await ListAsync(t["kofi"].Client, "all");
        var anniversary = after.GetProperty("today").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "anniversary");
        Assert.Equal(t["laila"].Id, anniversary.GetProperty("person").GetProperty("id").GetGuid());
        Assert.Equal(5, anniversary.GetProperty("years").GetInt32());

        // Laila leaves her anniversary out.
        await OkAsync(BirthdayAsync(t["laila"].Client, null, null, anniversaryOptOut: true));
        Assert.DoesNotContain(Entries(await ListAsync(t["kofi"].Client, "all"), "today"), e => e.Id == t["laila"].Id);

        // Joiners off hides them.
        await SetSettingsAsync(t, new { birthdays = true, anniversaries = true, joiners = false });
        Assert.Empty((await ListAsync(t["kofi"].Client, "all")).GetProperty("joiners").EnumerateArray());
    }

    [Fact]
    public async Task Celebrations_stay_in_their_tenant()
    {
        var t = await SetupAsync();
        await OkAsync(BirthdayAsync(t["laila"].Client, Today.Month, Today.Day));
        var other = await SetupAsync();
        Assert.DoesNotContain(Entries(await ListAsync(other["ahmed"].Client, "all"), "today"), e => e.Id == t["laila"].Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other["ahmed"].Client.PostAsJsonAsync($"{Api}/celebrations/{t["laila"].Id}/greetings", new { kind = "birthday" })).StatusCode);
    }
}
