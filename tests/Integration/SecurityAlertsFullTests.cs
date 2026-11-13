using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.PublicApi;
using TeamBeats.Modules.Security;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 R2 slice 3 (T024, T025; US5 scenarios 3 and 5, FR-013): a guest opening a Confidential item, an app's burst
/// of refused calls, an app's hourly call volume far over its average, and the tenant thresholds. Each tenant sees only
/// its own alerts; the plan decides who may change thresholds. Names and addresses are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecurityAlertsFullTests(TenantWorld world)
{
    private const string Settings = "/api/v1/security/alert-settings";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("alf-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "omar", "officer", "integrator", "laila" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "officer", "security_officer", "tenant", null);
        await world.AssignAsync(t, "integrator", "integration_administrator", "tenant", null);
        return t;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        while (await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0)
        {
        }
    }

    private static async Task<JsonElement[]> AlertsAsync(HttpClient client, string kind) =>
        (await OkAsync(client.GetAsync("/api/v1/security/alerts"))).EnumerateArray().Where(a => a.GetProperty("kind").GetString() == kind).ToArray();

    private Task<long> NotifiedAsync(TestTenant t, string who, string template) => world.Database.CountInTenantAsync(t.Id,
        "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[who].Id), ("k", template));

    [Fact]
    public async Task Thresholds_are_read_with_the_alerts_and_changed_only_on_plans_with_advanced_alerts()
    {
        var t = await FreshAsync();
        var basic = await FreshAsync("workplace");

        var defaults = await OkAsync(t["officer"].Client.GetAsync(Settings));
        Assert.Equal((200, 50, 5.0, "security"), (defaults.GetProperty("downloadFiles").GetInt32(), defaults.GetProperty("apiDeniedCount").GetInt32(),
            defaults.GetProperty("apiVolumeFactor").GetDouble(), defaults.GetProperty("guestConfidentialNotify").GetString()));
        // Security officers read; only security managers change.
        Assert.False(defaults.GetProperty("editable").GetBoolean());
        Assert.True((await OkAsync(t["admin"].Client.GetAsync(Settings))).GetProperty("editable").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.GetAsync(Settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["officer"].Client.PutAsJsonAsync(Settings, new { apiDeniedCount = 20 })).StatusCode);

        // Values outside their ranges are refused, each with its own key.
        var bad = await t["admin"].Client.PutAsJsonAsync(Settings, new { apiDeniedCount = 1, apiVolumeFactor = 500, workStart = "18:00", workEnd = "08:00", guestConfidentialNotify = "nobody" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadAsStringAsync();
        foreach (var key in new[] { "validation.security.alert_threshold", "validation.security.work_hours", "validation.security.guest_notify" })
        {
            Assert.Contains(key, body, StringComparison.Ordinal);
        }

        var saved = await OkAsync(t["admin"].Client.PutAsJsonAsync(Settings, new { apiDeniedCount = 20, apiVolumeFactor = 3.5, guestConfidentialNotify = "both" }));
        Assert.Equal((20, 3.5, "both", 200), (saved.GetProperty("apiDeniedCount").GetInt32(), saved.GetProperty("apiVolumeFactor").GetDouble(),
            saved.GetProperty("guestConfidentialNotify").GetString(), saved.GetProperty("downloadFiles").GetInt32()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityAlertSettingsChanged);

        // Another tenant keeps its defaults; Workplace reads them but cannot change them.
        var other = await OkAsync(basic["admin"].Client.GetAsync(Settings));
        Assert.Equal(50, other.GetProperty("apiDeniedCount").GetInt32());
        Assert.False(other.GetProperty("editable").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.PutAsJsonAsync(Settings, new { apiDeniedCount = 20 })).StatusCode);
    }

    [Fact]
    public async Task A_guest_opening_a_Confidential_item_alerts_security_officers_or_the_sponsor_as_the_tenant_chose()
    {
        var t = await FreshAsync();
        var omar = t["omar"].Client;
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/spaces/manage/settings",
            new { creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, guestConfidential = true, version = 0 }));
        var created = await OkAsync(omar.PostAsJsonAsync("/api/v1/spaces", new { name = "Audit 2027", description = "Audit.", visibility = "private", template = "project" }),
            HttpStatusCode.Created);
        var space = created.GetProperty("id").GetGuid();
        var library = created.GetProperty("libraryId").GetGuid();
        async Task<Guid> Upload(string name, string classification) =>
            (await OkAsync(omar.PostAsync($"/api/v1/documents/libraries/{library}/files?name={name}&publish=true&classification={classification}",
                new ByteArrayContent(Encoding.UTF8.GetBytes("Figures for " + name))), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var ledger = await Upload("ledger.txt", "confidential");
        var plan = await Upload("plan.txt", "internal");
        await OkAsync(omar.PutAsJsonAsync($"/api/v1/spaces/{space}/guests/files", new { confidential = true }));
        await world.SeedUserAsync(t, "karim", MembershipTypes.Guest);
        await OkAsync(omar.PostAsJsonAsync($"/api/v1/spaces/{space}/guests", new { email = t["karim"].Email }));
        var karim = t["karim"].Client;

        async Task Download(Guid id)
        {
            var token = await OkAsync(karim.GetAsync($"/api/v1/documents/{id}/download-token"));
            Assert.Equal(HttpStatusCode.OK, (await karim.GetAsync(token.GetProperty("url").GetString())).StatusCode);
            await DispatchAsync();
        }

        // An Internal item raises nothing; a Confidential one raises one alert per guest, item and day.
        await Download(plan);
        Assert.Empty(await AlertsAsync(t["officer"].Client, AlertKinds.GuestConfidentialAccess));
        await Download(ledger);
        await Download(ledger);
        var alert = Assert.Single(await AlertsAsync(t["officer"].Client, AlertKinds.GuestConfidentialAccess));
        Assert.Equal(t["karim"].Id, alert.GetProperty("subjectUserId").GetGuid());
        Assert.Equal((ledger, space), (alert.GetProperty("facts").GetProperty("documentId").GetGuid(), alert.GetProperty("facts").GetProperty("spaceId").GetGuid()));

        // By default the security officers are told, not the sponsor.
        var securityTemplate = AlertNotifier.Template(AlertKinds.GuestConfidentialAccess);
        Assert.Equal(1, await NotifiedAsync(t, "officer", securityTemplate));
        Assert.Equal(0, await NotifiedAsync(t, "omar", GuestConfidentialRule.SponsorTemplate));

        // Sponsor only: Omar hears about the next item, the officers do not.
        await OkAsync(t["admin"].Client.PutAsJsonAsync(Settings, new { guestConfidentialNotify = "sponsor" }));
        await Download(await Upload("payroll-summary.txt", "confidential"));
        Assert.Equal(2, (await AlertsAsync(t["officer"].Client, AlertKinds.GuestConfidentialAccess)).Length);
        Assert.Equal(1, await NotifiedAsync(t, "omar", GuestConfidentialRule.SponsorTemplate));
        Assert.Equal(1, await NotifiedAsync(t, "officer", securityTemplate));

        // Without advanced alerts nothing is raised for the same event.
        var basic = await FreshAsync("workplace");
        await world.Factory.InScopeAsync(basic.Id, async sp =>
        {
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new GuestOpenedClassified(Guid.NewGuid(), Guid.NewGuid(), null, basic["laila"].Id, "confidential"), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        await DispatchAsync();
        Assert.Empty(await AlertsAsync(basic["officer"].Client, AlertKinds.GuestConfidentialAccess));
    }

    private sealed record App(string Id, string ClientId, string Secret);

    private static async Task<App> AppAsync(TestTenant t, string name)
    {
        var app = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name, kind = "application", scopes = new[] { "content:read" } }), HttpStatusCode.Created);
        var id = app.GetProperty("id").GetString()!;
        var issued = await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created);
        return new App(id, app.GetProperty("clientId").GetString()!, issued.GetProperty("secret").GetString()!);
    }

    private async Task<string> TokenAsync(TestTenant t, App app) =>
        (await OkAsync(world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = app.ClientId, ["client_secret"] = app.Secret, ["scope"] = "content:read",
        })))).GetProperty("access_token").GetString()!;

    [Fact]
    public async Task Refused_app_calls_over_the_threshold_alert_security_officers_and_API_administrators_once()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        await OkAsync(t["admin"].Client.PutAsJsonAsync(Settings, new { apiDeniedCount = 5 }));
        var app = await AppAsync(t, "Ledger sync");
        var token = await TokenAsync(t, app);

        async Task CallAsync(int times)
        {
            for (var i = 0; i < times; i++)
            {
                // /me needs profile:read, which an application app never holds: authorization refuses it.
                var request = new HttpRequestMessage(HttpMethod.Get, "/api/public/v1/me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                Assert.Equal(HttpStatusCode.Forbidden, (await world.Factory.TenantClient(t.Key).SendAsync(request)).StatusCode);
            }
            await DispatchAsync();
        }

        await CallAsync(4);
        Assert.Empty(await AlertsAsync(t["officer"].Client, AlertKinds.ApiDeniedBurst));
        await CallAsync(3);
        var alert = Assert.Single(await AlertsAsync(t["officer"].Client, AlertKinds.ApiDeniedBurst));
        Assert.Equal(app.Id, alert.GetProperty("facts").GetProperty("appId").GetString());
        Assert.Equal("Ledger sync", alert.GetProperty("facts").GetProperty("appName").GetString());
        Assert.Equal(5, alert.GetProperty("facts").GetProperty("count").GetInt32());

        // Security officers get the security text; API administrators get the apps page.
        Assert.Equal(1, await NotifiedAsync(t, "officer", AlertNotifier.Template(AlertKinds.ApiDeniedBurst)));
        Assert.Equal(1, await NotifiedAsync(t, "integrator", AlertNotifier.AppTemplate(AlertKinds.ApiDeniedBurst)));
        Assert.Equal(0, await NotifiedAsync(t, "laila", AlertNotifier.Template(AlertKinds.ApiDeniedBurst)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k and link = '/admin/api/apps'",
            ("u", t["integrator"].Id), ("k", AlertNotifier.AppTemplate(AlertKinds.ApiDeniedBurst))));
        Assert.Empty(await AlertsAsync(other["officer"].Client, AlertKinds.ApiDeniedBurst));
    }

    [Fact]
    public async Task An_hour_far_over_an_apps_30_day_average_raises_one_volume_alert()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var busy = await AppAsync(t, "Report export");
        var quiet = await AppAsync(t, "Room sync");
        var now = DateTimeOffset.UtcNow;
        var hourEnd = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var hourStart = hourEnd.AddHours(-1);

        // Two calls an hour for 30 days, then 150 in the last hour for one app and 60 for the other.
        async Task Calls(string app, int count, DateTimeOffset from, string step) => await world.Database.OwnerExecInTenantAsync(t.Id,
            $"""
            insert into publicapi.call_log (tenant_id, id, at, app_id, user_id, operation, status, error_class, latency_ms, correlation_id)
            select platform.current_tenant_id(), gen_random_uuid(), @from + (g * interval '{step}'), @app::uuid, null, 'GET /api/public/v1/content', 200, null, 5, null
            from generate_series(0, @n - 1) g
            """, ("from", from), ("app", app), ("n", count));
        foreach (var app in new[] { busy.Id, quiet.Id })
        {
            await Calls(app, 1440, hourStart.AddDays(-30), "30 minutes");
        }
        await Calls(busy.Id, 150, hourStart, "10 seconds");
        await Calls(quiet.Id, 60, hourStart, "30 seconds");

        async Task<int> WatchAsync(TestTenant tenant) => await world.Factory.InScopeAsync(tenant.Id, async sp =>
        {
            var spikes = await sp.GetRequiredService<CallLog>().WatchVolumeAsync(hourEnd, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return spikes;
        });

        Assert.Equal(1, await WatchAsync(t));
        Assert.Equal(1, await WatchAsync(t));
        Assert.Equal(0, await WatchAsync(other));
        await DispatchAsync();
        var alert = Assert.Single(await AlertsAsync(t["officer"].Client, AlertKinds.ApiVolumeSpike));
        Assert.Equal(busy.Id, alert.GetProperty("facts").GetProperty("appId").GetString());
        Assert.Equal(150, alert.GetProperty("facts").GetProperty("calls").GetInt64());
        Assert.Equal(1, await NotifiedAsync(t, "integrator", AlertNotifier.AppTemplate(AlertKinds.ApiVolumeSpike)));
        Assert.Empty(await AlertsAsync(other["officer"].Client, AlertKinds.ApiVolumeSpike));

        // A factor of 100 (200 calls) stops the same hour from counting.
        await OkAsync(t["admin"].Client.PutAsJsonAsync(Settings, new { apiVolumeFactor = 100 }));
        Assert.Equal(0, await WatchAsync(t));
    }
}
