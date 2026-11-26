using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 018 R3: Teams presence on profiles, a connection feature off by default with delegated <c>Presence.Read.All</c>,
/// read as the viewer, with the visibility of free and busy. Only the availability and a few activities are given.
/// All content is fictional.
/// </summary>
public sealed partial class MailCalendarTests
{
    private async Task<Setup> PresenceAsync(bool on = true)
    {
        var s = await SetupAsync(on ? ["presence"] : ["free_busy"], users: ["sara", "omar"]);
        Graph.AddDelegatedConsent(s.Directory, "Presence.Read.All");
        await ConnectAsync(s.Tenant);
        return s;
    }

    [Fact]
    public async Task A_colleagues_Teams_presence_shows_its_availability_and_activity_only()
    {
        var s = await PresenceAsync();
        Graph.SetPresence(s.Tenant["sara"].Email, "Busy", "InACall");
        Graph.SetPresence(s.Tenant["omar"].Email, "BeRightBack", "BeRightBack");

        var sara = await GetAsync(s.Tenant, $"/api/v1/people/{s.Tenant["sara"].Id}/presence");
        Assert.Equal(("ok", "busy", "in_a_call"), (State(sara), sara.GetProperty("availability").GetString(), sara.GetProperty("activity").GetString()));
        // Never the status message Teams holds.
        Assert.Equal(["state", "availability", "activity"], sara.EnumerateObject().Select(p => p.Name).ToArray());
        var omar = await GetAsync(s.Tenant, $"/api/v1/people/{s.Tenant["omar"].Id}/presence");
        Assert.Equal(("away", JsonValueKind.Null), (omar.GetProperty("availability").GetString(), omar.GetProperty("activity").ValueKind));

        // Kept for a minute per viewer: a second look reads Teams no more.
        var reads = Graph.PresenceReads.Count(a => a == s.Tenant["sara"].Email);
        await GetAsync(s.Tenant, $"/api/v1/people/{s.Tenant["sara"].Id}/presence");
        Assert.Equal(reads, Graph.PresenceReads.Count(a => a == s.Tenant["sara"].Email));
    }

    [Fact]
    public async Task Presence_is_a_state_without_the_feature_and_follows_the_profile_visibility_rule()
    {
        var off = await PresenceAsync(on: false);
        var none = await GetAsync(off.Tenant, $"/api/v1/people/{off.Tenant["sara"].Id}/presence");
        Assert.Equal(("turned_off", JsonValueKind.Null), (State(none), none.GetProperty("availability").ValueKind));
        Assert.DoesNotContain(Graph.PresenceReads, a => a == off.Tenant["sara"].Email);

        var s = await PresenceAsync();
        // A user of another tenant, or nobody: not found, and nothing is read from Teams.
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant["employee"].Client.GetAsync($"/api/v1/people/{off.Tenant["sara"].Id}/presence")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant["employee"].Client.GetAsync($"/api/v1/people/{Guid.NewGuid()}/presence")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(s.Tenant.Key).GetAsync($"/api/v1/people/{s.Tenant["sara"].Id}/presence")).StatusCode);

        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t, false);
            delete from authz.role_permission where permission_key = 'people.view';
            update authz.grants_version set version = version + 1;
            """, ("t", s.Tenant.Id.ToString()));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant["employee"].Client.GetAsync($"/api/v1/people/{s.Tenant["sara"].Id}/presence")).StatusCode);
        Assert.DoesNotContain(Graph.PresenceReads, a => a == s.Tenant["sara"].Email);
    }
}
