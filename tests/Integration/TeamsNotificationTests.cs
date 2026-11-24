using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 015 US5 (R2): Teams activity notifications through the Microsoft 365 connection. A person with Teams on for a
/// category gets an activity notification that opens the item in TeamBeats; when the app is not installed for them,
/// they have no Microsoft 365 account or the feature is off, the message goes by email and the delivery says why.
/// Microsoft Graph is the fake of the test factory.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TeamsNotificationTests(TenantWorld world)
{
    private const string Preferences = "/api/v1/me/notification-preferences";

    private sealed record Setup(TestTenant T, Guid Directory, Guid Connection, string SaraObjectId);

    private async Task<Setup> SetupAsync(bool linkSara = true)
    {
        var t = await world.CreateTenantAsync("tm-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        var directory = Guid.NewGuid();
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features = new[] { "teams" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connection = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        world.Factory.Graph.Consent(directory, ["TeamsActivity.Send"]);
        var start = await admin.GetAsync($"/api/v1/integrations/connections/{connection}/features/teams/consent");
        var query = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query);
        var relay = await world.Factory.PlatformClient().GetAsync(
            $"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(query["state"])}");
        var client = world.Factory.TenantClient(t.Key, t["admin"].Token);
        client.DefaultRequestHeaders.Add("Cookie", start.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith("tb_consent=", StringComparison.Ordinal)));
        await client.GetAsync(relay.Headers.Location!.PathAndQuery);

        // Sara is linked under the directory by her Entra object ID, as directory sync or Entra sign-in links her.
        var oid = Guid.NewGuid().ToString("D");
        if (linkSara)
        {
            await world.Database.OwnerExecInTenantAsync(t.Id,
                "insert into identity.external_identity (tenant_id, id, issuer, subject, user_id) values (@t, @id, @i, @s, @u)",
                ("t", t.Id), ("id", Guid.NewGuid()), ("i", $"https://login.microsoftonline.com/{directory:D}/v2.0"), ("s", oid), ("u", t["sara"].Id));
        }
        return new Setup(t, directory, connection, oid);
    }

    private Task SendAsync(TestTenant t, string category, Guid user) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest([user], category, "system.sample", "/p/home",
                new Dictionary<string, string> { ["label"] = "HR" }));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private Task<int> DeliverAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var count = await sp.GetRequiredService<TeamsDelivery>().DeliverDueAsync(DateTimeOffset.UtcNow, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return count;
        });

    private Task<long> DeliveriesAsync(TestTenant t, string channel, string status, string? error = null) =>
        world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.delivery where channel = @c and status = @s and (@e::text is null or last_error = @e)",
            ("c", channel), ("s", status), ("e", (object?)error ?? DBNull.Value));

    private static Task<HttpResponseMessage> TeamsOnAsync(TestUser user, string category) =>
        user.Client.PutAsJsonAsync(Preferences, new { categories = new[] { new { category, email = false, digest = false, teams = true } } });

    [Fact]
    public async Task A_person_with_Teams_on_gets_an_activity_notification_that_opens_the_item()
    {
        var s = await SetupAsync();
        var sara = s.T["sara"];
        world.Factory.Graph.InstallTeamsApp(s.Directory, s.SaraObjectId);

        var prefs = await (await sara.Client.GetAsync(Preferences)).ReadAsync<JsonElement>();
        Assert.True(prefs.GetProperty("teamsAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await TeamsOnAsync(sara, "announcement")).StatusCode);
        prefs = await (await sara.Client.GetAsync(Preferences)).ReadAsync<JsonElement>();
        Assert.True(prefs.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("category").GetString() == "announcement").GetProperty("teams").GetBoolean());

        await SendAsync(s.T, "announcement", sara.Id);
        // A category with Teams off sends nothing to Teams.
        await SendAsync(s.T, "comment", sara.Id);
        Assert.Equal(1, await DeliverAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "teams", "sent"));
        Assert.Equal(0, await DeliveriesAsync(s.T, "email", "pending"));

        var activity = Assert.Single(world.Factory.Graph.TeamsActivities, a => a.Directory == s.Directory.ToString("D"));
        Assert.Equal(s.SaraObjectId, activity.UserId);
        Assert.Equal($"https://{s.T.Key}.{ApiFactory.BaseDomain}/p/home", activity.WebUrl);
        Assert.False(string.IsNullOrWhiteSpace(activity.Topic));
        Assert.Equal(0, await DeliverAsync(s.T));
    }

    [Fact]
    public async Task Without_the_app_or_an_account_the_message_goes_by_email_and_the_delivery_says_why()
    {
        var s = await SetupAsync();
        var sara = s.T["sara"];
        await TeamsOnAsync(sara, "announcement");

        // The TeamBeats app is not installed for Sara: Teams refuses, email takes it.
        await SendAsync(s.T, "announcement", sara.Id);
        Assert.Equal(1, await DeliverAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "teams", "dropped", "teams_not_installed"));
        Assert.Equal(1, await DeliveriesAsync(s.T, "email", "pending"));

        // A person with no Microsoft 365 account linked: the same fallback, with that reason.
        var unlinked = await SetupAsync(linkSara: false);
        await TeamsOnAsync(unlinked.T["sara"], "announcement");
        await SendAsync(unlinked.T, "announcement", unlinked.T["sara"].Id);
        Assert.Equal(1, await DeliverAsync(unlinked.T));
        Assert.Equal(1, await DeliveriesAsync(unlinked.T, "teams", "dropped", "teams_not_linked"));
        Assert.Equal(1, await DeliveriesAsync(unlinked.T, "email", "pending"));
    }

    [Fact]
    public async Task Turning_the_connection_off_falls_back_to_email_and_stops_new_Teams_deliveries()
    {
        var s = await SetupAsync();
        var sara = s.T["sara"];
        world.Factory.Graph.InstallTeamsApp(s.Directory, s.SaraObjectId);
        await TeamsOnAsync(sara, "announcement");
        await SendAsync(s.T, "announcement", sara.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await s.T["admin"].Client.PostAsync($"/api/v1/integrations/connections/{s.Connection}:disable", null)).StatusCode);
        Assert.Equal(1, await DeliverAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "teams", "dropped", "teams_off"));
        Assert.Equal(1, await DeliveriesAsync(s.T, "email", "pending"));
        Assert.DoesNotContain(world.Factory.Graph.TeamsActivities, a => a.Directory == s.Directory.ToString("D"));

        var prefs = await (await sara.Client.GetAsync(Preferences)).ReadAsync<JsonElement>();
        Assert.False(prefs.GetProperty("teamsAvailable").GetBoolean());
        await SendAsync(s.T, "announcement", sara.Id);
        Assert.Equal(1, await world.Database.CountInTenantAsync(s.T.Id, "select count(*) from notifications.delivery where channel = 'teams'"));
    }

    [Fact]
    public async Task Teams_stays_inside_its_tenant()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        world.Factory.Graph.InstallTeamsApp(a.Directory, a.SaraObjectId);
        world.Factory.Graph.InstallTeamsApp(b.Directory, b.SaraObjectId);
        await TeamsOnAsync(a.T["sara"], "announcement");
        await TeamsOnAsync(b.T["sara"], "announcement");

        // Tenant A's sender cannot reach tenant B's Sara: she is not an active member of A.
        await SendAsync(a.T, "announcement", b.T["sara"].Id);
        Assert.Equal(0, await DeliverAsync(a.T));
        Assert.Equal(0, await DeliverAsync(b.T));
        Assert.DoesNotContain(world.Factory.Graph.TeamsActivities, x => x.UserId == b.SaraObjectId);

        await SendAsync(a.T, "announcement", a.T["sara"].Id);
        Assert.Equal(1, await DeliverAsync(a.T));
        var sent = Assert.Single(world.Factory.Graph.TeamsActivities, x => x.Directory == a.Directory.ToString("D"));
        Assert.Equal(a.SaraObjectId, sent.UserId);
        Assert.DoesNotContain(world.Factory.Graph.TeamsActivities, x => x.Directory == b.Directory.ToString("D"));
    }
}
