using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.DevFakes.FakeMicrosoftGraph;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 018 (R1) User Stories 2 to 4: My mail, My calendar and a colleague's free and busy. All content is fictional.</summary>
[Collection(WorldCollection.Name)]
public sealed class MailCalendarTests(TenantWorld world)
{
    private FakeMicrosoftGraph Graph => world.Factory.Graph;

    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");

    private sealed record Setup(TestTenant Tenant, Guid Connection, Guid Directory);

    private async Task<Setup> SetupAsync(string[] features, string package = "workplace_plus", params string[] users)
    {
        var t = await world.CreateTenantAsync("mc-" + Guid.NewGuid().ToString("N")[..8], package);
        var directory = Guid.NewGuid();
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features,
        });
        var connection = package == "workplace" ? Guid.Empty : (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        foreach (var f in features)
        {
            await AdminConsentAsync(t, connection, directory, f);
        }
        Graph.ConsentDelegated(directory, "User.Read", "Mail.ReadBasic", "Mail.Read", "Calendars.ReadBasic");
        foreach (var u in users.Prepend("employee"))
        {
            await world.SeedUserAsync(t, u, MembershipTypes.Employee);
        }
        return new Setup(t, connection, directory);
    }

    private async Task AdminConsentAsync(TestTenant t, Guid connection, Guid directory, string feature)
    {
        var start = await t["admin"].Client.GetAsync($"/api/v1/integrations/connections/{connection}/features/{feature}/consent");
        var state = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query)["state"];
        var relay = await world.Factory.PlatformClient().GetAsync(
            $"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(state)}");
        var done = await Client(t.Key, t["admin"].Token, "tb_consent", Cookie(start, "tb_consent")!).GetAsync(relay.Headers.Location!.PathAndQuery);
        Assert.EndsWith("consent=granted", done.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    private HttpClient Client(string tenantKey, string session, string cookie, string value)
    {
        var client = world.Factory.ClientFor($"{tenantKey}.{ApiFactory.BaseDomain}");
        client.DefaultRequestHeaders.Add("Cookie", $"tb_session={session}; {cookie}={value}");
        return client;
    }

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith(name + "=", StringComparison.Ordinal)).Select(v => v[(name.Length + 1)..]).FirstOrDefault()
            : null;

    private async Task ConnectAsync(TestTenant t, string user = "employee")
    {
        var start = await t[user].Client.GetAsync("/api/v1/me/outlook/connect");
        var answer = Graph.Authorize(start.Headers.Location!);
        var relay = await world.Factory.PlatformClient().GetAsync("/api/v1/integrations/outlook/callback" + answer);
        var done = await Client(t.Key, t[user].Token, "tb_outlook", Cookie(start, "tb_outlook")!).GetAsync(relay.Headers.Location!.PathAndQuery);
        Assert.EndsWith("outlook=connected", done.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    private static Task<JsonElement> GetAsync(TestTenant t, string url, string user = "employee") => t[user].Client.GetFromJsonAsync<JsonElement>(url);

    private static string State(JsonElement e) => e.GetProperty("state").GetString()!;

    private static DateTimeOffset TodayDubai()
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Dubai).Date;
        return new DateTimeOffset(local, Dubai.GetUtcOffset(local));
    }

    private FakeMailbox SeedMail(TestTenant t, string user = "employee")
    {
        var box = Graph.MailboxFor(t[user].Email);
        var now = DateTimeOffset.UtcNow;
        box.UnreadCount = 7;
        for (var i = 0; i < 12; i++)
        {
            box.Messages.Add(new FakeMessage($"m{i}", $"Sender {i}", $"sender{i}@contoso.example", $"Subject {i}", now.AddMinutes(-i * 10),
                IsRead: i % 2 == 1, Importance: i == 0 ? "high" : "normal", HasAttachments: i == 1, Preview: "Hello\u0007 <b>team</b>\r\n\tsee  you", Focused: i % 3 != 2));
        }
        return box;
    }

    /// <summary>A published home from H01 with nothing placed, so the add panel can be read for its personal region.</summary>
    private static async Task<Guid> HomeAsync(TestTenant t)
    {
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "H01", slug = "home-" + Guid.NewGuid().ToString("N")[..8] });
        var page = await created.ReadAsync<JsonElement>();
        var id = page.GetProperty("id").GetGuid();
        var published = await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{id}/publish", new { reason = "home" });
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
        return id;
    }

    [Fact]
    public async Task My_mail_and_My_calendar_are_offered_only_while_their_connection_feature_is_on()
    {
        var s = await SetupAsync(["mail"]);
        var home = await HomeAsync(s.Tenant);
        async Task<Dictionary<string, JsonElement>> PanelAsync() =>
            (await GetAsync(s.Tenant, $"/api/v1/me/pages/{home}/add-panel?region=grid")).EnumerateArray().ToDictionary(i => i.GetProperty("componentRef").GetString()!);

        var panel = await PanelAsync();
        Assert.True(panel["outlook.my_mail@1"].GetProperty("eligible").GetBoolean());
        Assert.Equal("validation.personalization.feature_off", panel["outlook.my_calendar@1"].GetProperty("reasonKey").GetString());
        Assert.Equal("personal", panel["outlook.my_mail@1"].GetProperty("category").GetString());

        Assert.True((await s.Tenant["admin"].Client.PostAsync($"/api/v1/integrations/connections/{s.Connection}/features/mail:disable", null)).IsSuccessStatusCode);
        Assert.Equal("validation.personalization.feature_off", (await PanelAsync())["outlook.my_mail@1"].GetProperty("reasonKey").GetString());
    }

    [Fact]
    public async Task My_mail_shows_unread_count_and_latest_focused_messages_with_markers_and_links()
    {
        var s = await SetupAsync(["mail"]);
        SeedMail(s.Tenant);
        await ConnectAsync(s.Tenant);

        var mail = await GetAsync(s.Tenant, "/api/v1/me/mail");
        Assert.Equal("ok", State(mail));
        Assert.Equal(7, mail.GetProperty("unreadCount").GetInt32());
        var messages = mail.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["m0", "m1", "m3", "m4", "m6"], messages.Select(m => m.GetProperty("id").GetString()!).ToArray());
        Assert.Equal("high", messages[0].GetProperty("importance").GetString());
        Assert.False(messages[0].GetProperty("isRead").GetBoolean());
        Assert.True(messages[1].GetProperty("hasAttachments").GetBoolean());
        Assert.Equal("Sender 0", messages[0].GetProperty("sender").GetString());
        Assert.StartsWith("https://outlook.office365.com/owa/", messages[0].GetProperty("webLink").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, messages[0].GetProperty("preview").ValueKind);
        Assert.Equal("https://outlook.office.com/mail/deeplink/compose", mail.GetProperty("composeLink").GetString());
        Assert.True(mail.GetProperty("asOf").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));

        var all = await GetAsync(s.Tenant, "/api/v1/me/mail?view=all&top=10");
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"m{i}").ToArray(), all.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant["employee"].Client.GetAsync("/api/v1/me/mail?top=11")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant["employee"].Client.GetAsync("/api/v1/me/mail?view=other")).StatusCode);
    }

    [Fact]
    public async Task The_admin_maximum_caps_the_list_and_the_preview_line_is_plain_text_only_when_on()
    {
        var s = await SetupAsync(["mail"]);
        SeedMail(s.Tenant);
        var admin = s.Tenant["admin"].Client;
        var options = $"/api/v1/integrations/connections/{s.Connection}/features/mail/options";
        (await admin.PutAsJsonAsync(options, new { maxMessages = 3 })).EnsureSuccessStatusCode();
        await ConnectAsync(s.Tenant);
        var capped = await GetAsync(s.Tenant, "/api/v1/me/mail?view=all&top=10");
        Assert.Equal(3, capped.GetProperty("messages").GetArrayLength());

        (await admin.PutAsJsonAsync(options, new { previewLine = true })).EnsureSuccessStatusCode();
        await AdminConsentAsync(s.Tenant, s.Connection, s.Directory, "mail");
        Assert.Equal("not_connected", State(await GetAsync(s.Tenant, "/api/v1/me/mail")));
        await ConnectAsync(s.Tenant);
        var mail = await GetAsync(s.Tenant, "/api/v1/me/mail?view=all&top=10&refresh=true");
        Assert.Equal("Hello <b>team</b> see you", mail.GetProperty("messages")[0].GetProperty("preview").GetString());
    }

    [Fact]
    public async Task No_mailbox_not_connected_and_feature_off_are_states_not_errors()
    {
        var s = await SetupAsync(["mail", "calendar"]);
        Assert.Equal("not_connected", State(await GetAsync(s.Tenant, "/api/v1/me/mail")));
        Assert.Equal("not_connected", State(await GetAsync(s.Tenant, "/api/v1/me/calendar")));
        Graph.MailboxFor(s.Tenant["employee"].Email).Exists = false;
        await ConnectAsync(s.Tenant);
        Assert.Equal("no_mailbox", State(await GetAsync(s.Tenant, "/api/v1/me/mail")));
        Assert.Equal("no_mailbox", State(await GetAsync(s.Tenant, "/api/v1/me/calendar")));
        Assert.Equal("turned_off", State(await GetAsync(s.Tenant, $"/api/v1/people/{s.Tenant["admin"].Id}/availability")));
    }

    [Fact]
    public async Task Graph_failures_show_degraded_with_the_last_data_and_its_age_and_retry_429()
    {
        var s = await SetupAsync(["mail"]);
        SeedMail(s.Tenant);
        await ConnectAsync(s.Tenant);

        // One 429 is retried as Retry-After asks.
        Graph.DelegatedFaults.Enqueue(429);
        var first = await GetAsync(s.Tenant, "/api/v1/me/mail");
        Assert.Equal("ok", State(first));
        var asOf = first.GetProperty("asOf").GetDateTimeOffset();

        // Graph down on a refresh: the last data of this session, marked degraded, with the time it was read.
        for (var i = 0; i < 3; i++)
        {
            Graph.DelegatedFaults.Enqueue(503);
        }
        var degraded = await GetAsync(s.Tenant, "/api/v1/me/mail?refresh=true");
        Assert.Equal("degraded", State(degraded));
        Assert.Equal(asOf, degraded.GetProperty("asOf").GetDateTimeOffset());
        Assert.Equal(7, degraded.GetProperty("unreadCount").GetInt32());

        // Nothing shown yet this session: degraded with no data.
        for (var i = 0; i < 6; i++)
        {
            Graph.DelegatedFaults.Enqueue(503);
        }
        var empty = await GetAsync(s.Tenant, "/api/v1/me/mail?view=all");
        Assert.Equal("degraded", State(empty));
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("asOf").ValueKind);
        Assert.Equal(0, empty.GetProperty("messages").GetArrayLength());
        Graph.DelegatedFaults.Clear();
    }

    [Fact]
    public async Task Reads_are_cached_per_employee_and_refresh_is_limited()
    {
        var s = await SetupAsync(["mail"], users: "colleague");
        SeedMail(s.Tenant);
        Graph.MailboxFor(s.Tenant["colleague"].Email).UnreadCount = 2;
        await ConnectAsync(s.Tenant);
        await ConnectAsync(s.Tenant, "colleague");

        int Calls() => Graph.DelegatedCalls.Count;
        await GetAsync(s.Tenant, "/api/v1/me/mail");
        var before = Calls();
        Graph.MailboxFor(s.Tenant["employee"].Email).UnreadCount = 9;
        Assert.Equal(7, (await GetAsync(s.Tenant, "/api/v1/me/mail")).GetProperty("unreadCount").GetInt32());
        Assert.Equal(before, Calls());

        // Another employee of the same tenant never gets this cache.
        Assert.Equal(2, (await GetAsync(s.Tenant, "/api/v1/me/mail", "colleague")).GetProperty("unreadCount").GetInt32());

        Assert.Equal(9, (await GetAsync(s.Tenant, "/api/v1/me/mail?refresh=true")).GetProperty("unreadCount").GetInt32());
        for (var i = 0; i < 8; i++)
        {
            await GetAsync(s.Tenant, "/api/v1/me/mail?refresh=true");
        }
        Graph.MailboxFor(s.Tenant["employee"].Email).UnreadCount = 11;
        Assert.Equal(9, (await GetAsync(s.Tenant, "/api/v1/me/mail?refresh=true")).GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task My_calendar_applies_the_display_rules()
    {
        var s = await SetupAsync(["calendar"]);
        var today = TodayDubai();
        var tomorrow = today.AddDays(1);
        var box = Graph.MailboxFor(s.Tenant["employee"].Email);
        var tomorrowDate = TimeZoneInfo.ConvertTime(tomorrow, Dubai).Date;
        box.Events.AddRange(
        [
            new FakeEvent("standup-1", "Stand-up", today.AddHours(9), today.AddHours(9.25), JoinUrl: "https://teams.example/join/1"),
            new FakeEvent("standup-2", "Stand-up", tomorrow.AddHours(9), tomorrow.AddHours(9.25), JoinUrl: "https://teams.example/join/2"),
            new FakeEvent("holiday", "Office closed", new DateTimeOffset(tomorrowDate, TimeSpan.Zero), new DateTimeOffset(tomorrowDate.AddDays(1), TimeSpan.Zero), AllDay: true, ShowAs: "free"),
            new FakeEvent("cancelled", "Old review", tomorrow.AddHours(11), tomorrow.AddHours(12), Cancelled: true),
            new FakeEvent("declined", "Optional talk", tomorrow.AddHours(13), tomorrow.AddHours(14), Response: "declined"),
            new FakeEvent("maybe", "Design sync", tomorrow.AddHours(15), tomorrow.AddHours(16), ShowAs: "tentative", Response: "tentativelyAccepted", Location: "Room 4"),
            new FakeEvent("private", "Doctor", tomorrow.AddHours(17), tomorrow.AddHours(18), Sensitivity: "private"),
            new FakeEvent("far", "Planning", today.AddDays(10).AddHours(10), today.AddDays(10).AddHours(11)),
        ]);
        await ConnectAsync(s.Tenant);

        var calendar = await GetAsync(s.Tenant, "/api/v1/me/calendar");
        Assert.Equal("ok", State(calendar));
        Assert.Equal("Asia/Dubai", calendar.GetProperty("timeZone").GetString());
        Assert.Equal(today, calendar.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(today.AddDays(7), calendar.GetProperty("to").GetDateTimeOffset());
        var items = calendar.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["standup-1", "holiday", "standup-2", "maybe", "private"], items.Select(i => i.GetProperty("id").GetString()!).ToArray());
        Assert.True(items[1].GetProperty("free").GetBoolean());
        Assert.True(items[1].GetProperty("isAllDay").GetBoolean());
        Assert.Equal("https://teams.example/join/2", items[2].GetProperty("onlineJoinUrl").GetString());
        Assert.True(items[3].GetProperty("tentative").GetBoolean());
        Assert.Equal("Room 4", items[3].GetProperty("location").GetString());
        Assert.Equal("Doctor", items[4].GetProperty("subject").GetString());
        Assert.True(items[4].GetProperty("isPrivate").GetBoolean());
        Assert.All(items, i => Assert.Equal("outlook", i.GetProperty("source").GetString()));

        var hidden = await GetAsync(s.Tenant, "/api/v1/me/calendar?hidePrivate=true");
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("items")[4].GetProperty("subject").ValueKind);

        var fortnight = await GetAsync(s.Tenant, "/api/v1/me/calendar?days=14");
        Assert.Contains(fortnight.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "far");
        var oneDay = await GetAsync(s.Tenant, "/api/v1/me/calendar?days=1");
        Assert.Equal(["standup-1"], oneDay.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant["employee"].Client.GetAsync("/api/v1/me/calendar?days=15")).StatusCode);

        // The administrator's maximum range caps what is read.
        (await s.Tenant["admin"].Client.PutAsJsonAsync($"/api/v1/integrations/connections/{s.Connection}/features/calendar/options", new { maxDays = 3 })).EnsureSuccessStatusCode();
        var capped = await GetAsync(s.Tenant, "/api/v1/me/calendar?days=14&refresh=true");
        Assert.DoesNotContain(capped.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "far");
    }

    [Fact]
    public async Task My_calendar_is_degraded_when_graph_is_down()
    {
        var s = await SetupAsync(["calendar"]);
        await ConnectAsync(s.Tenant);
        for (var i = 0; i < 3; i++)
        {
            Graph.DelegatedFaults.Enqueue(502);
        }
        var calendar = await GetAsync(s.Tenant, "/api/v1/me/calendar");
        Assert.Equal("degraded", State(calendar));
        Assert.Equal(0, calendar.GetProperty("items").GetArrayLength());
        Graph.DelegatedFaults.Clear();
    }

    [Fact]
    public async Task Free_and_busy_shows_status_blocks_only_for_a_visible_colleague()
    {
        var s = await SetupAsync(["free_busy"], users: ["sara", "omar"]);
        await ConnectAsync(s.Tenant);
        var now = DateTimeOffset.UtcNow;
        Graph.Schedule(s.Tenant["sara"].Email,
            new FakeScheduleItem("busy", now.AddMinutes(-30), now.AddMinutes(5)),
            new FakeScheduleItem("busy", now.AddMinutes(5), now.AddMinutes(10)),
            new FakeScheduleItem("free", now.AddMinutes(10), now.AddMinutes(15)),
            new FakeScheduleItem("oof", now.AddMinutes(15), now.AddMinutes(20)));
        // Blocks after midnight in the tenant's time zone are not today.
        string[] expected = TodayDubai().AddDays(1) - now > TimeSpan.FromMinutes(21) ? ["busy", "oof"] : ["busy"];

        var sara = await GetAsync(s.Tenant, $"/api/v1/people/{s.Tenant["sara"].Id}/availability");
        Assert.Equal("ok", State(sara));
        Assert.True(sara.GetProperty("shared").GetBoolean());
        var blocks = sara.GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal(expected, blocks.Select(b => b.GetProperty("status").GetString()!).ToArray());
        Assert.Equal(["status", "start", "end"], blocks[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(blocks[0].GetProperty("start").GetDateTimeOffset() >= now.AddSeconds(-5));

        // A colleague who does not share free and busy: nothing, not an error.
        var omar = await GetAsync(s.Tenant, $"/api/v1/people/{s.Tenant["omar"].Id}/availability");
        Assert.Equal("ok", State(omar));
        Assert.False(omar.GetProperty("shared").GetBoolean());
        Assert.Equal(0, omar.GetProperty("blocks").GetArrayLength());

        // A user of another tenant, or nobody: not found.
        var other = await SetupAsync(["free_busy"]);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant["employee"].Client.GetAsync($"/api/v1/people/{other.Tenant["employee"].Id}/availability")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant["employee"].Client.GetAsync($"/api/v1/people/{Guid.NewGuid()}/availability")).StatusCode);
    }

    [Fact]
    public async Task Every_mail_and_calendar_endpoint_refuses_without_the_entitlement()
    {
        var s = await SetupAsync([], package: "workplace");
        var client = s.Tenant["employee"].Client;
        foreach (var url in new[] { "/api/v1/me/mail", "/api/v1/me/calendar", $"/api/v1/people/{s.Tenant["admin"].Id}/availability" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("entitlement.not_in_package", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(s.Tenant.Key).GetAsync(url)).StatusCode);
        }
    }
}
