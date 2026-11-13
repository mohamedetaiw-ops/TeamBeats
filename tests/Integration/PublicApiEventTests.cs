using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Integrations.Webhooks;
using TeamBeats.Modules.PublicApi;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 T016 and T017, US4 and FR-018 (slice 5b): event subscriptions of application apps. A notification carries
/// identifiers and a link only, is signed by the spec 017 webhook runtime and goes only to an app that may read the
/// resource; only the public event catalogue may be subscribed to; deliveries failing for 3 days pause the subscription
/// and tell the API administrators; the app lists and removes its own subscriptions; another tenant's events never
/// reach it. Receivers are the fake REST hosts of the test factory; secrets are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicApiEventTests(TenantWorld world)
{
    private const string SigningValue = "fictional-event-signing-value";

    private FakeRestApi Rest => world.Factory.Rest;

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ev-" + Guid.NewGuid().ToString("N")[..6], package);
        var group = await world.CreateUnitAsync(t, "Group", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", group, "subsidiary");
        await world.CreateUnitAsync(t, "Egypt", group, "subsidiary");
        await world.SeedUserAsync(t, "gia", MembershipTypes.Employee);
        return t;
    }

    private static string Origin(TestTenant t) => $"https://{t.Key}.{ApiFactory.BaseDomain}";

    private static string Host() => $"ev-{Guid.NewGuid():N}"[..18] + ".rest.test";

    private async Task<(string Host, string SecretRef)> ReceiverAsync(TestTenant t, HttpStatusCode answer = HttpStatusCode.NoContent)
    {
        var host = Host();
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        Rest.On(host, _ => new HttpResponseMessage(answer));
        var reference = SecretReferences.For(t.Id, "api-events");
        world.Factory.Secrets.Set(reference, SigningValue);
        return (host, reference);
    }

    /// <summary>An application app with the scopes and App reader at Gulf; its ID, client ID and secret.</summary>
    private static async Task<(Guid Id, string ClientId, string Secret)> AppAsync(TestTenant t, string[] scopes, string kind = "application")
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Org feed " + Guid.NewGuid().ToString("N")[..4], kind, scopes,
            redirectUris = kind == "application" ? Array.Empty<string>() : [Redirect],
        }), HttpStatusCode.Created);
        var id = Guid.Parse(app.GetProperty("id").GetString()!);
        if (kind != "application")
        {
            return (id, app.GetProperty("clientId").GetString()!, "");
        }
        var secret = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created))
            .GetProperty("secret").GetString()!;
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "app", subjectId = id, roleKey = BuiltInRoles.AppReader, scopeType = "org_unit", scopeId = t.Unit("Gulf") }), HttpStatusCode.Created);
        return (id, app.GetProperty("clientId").GetString()!, secret);
    }

    private async Task<string> TokenAsync(TestTenant t, string clientId, string secret, string scope) =>
        (await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret, ["scope"] = scope,
        })))).GetProperty("access_token").GetString()!;

    private static Task<HttpResponseMessage> SubscribeAsync(TestTenant t, Guid appId, string eventType, string host, string secretRef, string by = "admin") =>
        t[by].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{appId}/subscriptions", new { eventType, url = $"https://{host}/events", secretRef });

    private Task MovedAsync(TestTenant t, Guid unit) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<PublicEventHandlers>().HandleAsync(new OrganizationUnitMoved(unit, null, null, 1), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private Task<int> RunDueAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var n = await sp.GetRequiredService<ConnectorJobs>().RunDueAsync(DateTimeOffset.UtcNow.AddHours(1), 50, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });

    private Task FinishedAsync(TestTenant t, Guid endpoint, string outcome) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<SubscriptionHealth>().HandleAsync(
                new ConnectorActionCompleted(Guid.NewGuid(), endpoint, WebhookPack.DeliverAction, outcome, null, null, null, null), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private async Task<Guid> EndpointAsync(TestTenant t, Guid subscription) =>
        await world.Database.TenantScalarAsync<Guid>(t.Id, "select endpoint_id from publicapi.event_subscription where id = @id", ("id", subscription));

    [Fact]
    public async Task A_notification_is_thin_signed_and_sent_only_for_resources_the_app_can_read()
    {
        var t = await FreshAsync();
        var (host, secretRef) = await ReceiverAsync(t);
        var (appId, _, _) = await AppAsync(t, [ScopeCatalogue.OrgRead, ScopeCatalogue.EventsSubscribe]);
        var created = await JsonAsync(await SubscribeAsync(t, appId, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef), HttpStatusCode.Created);
        var subscription = created.GetProperty("id").GetGuid();
        Assert.Equal("active", created.GetProperty("status").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id and changes->>'host' = @h",
            ("a", AuditActions.ApiSubscriptionCreated), ("id", subscription.ToString()), ("h", host)));

        // Gulf is within the app's role; Egypt is not, so its event is never sent (US4 scenario 2).
        await MovedAsync(t, t.Unit("Gulf"));
        await MovedAsync(t, t.Unit("Egypt"));
        Assert.Equal(1, await RunDueAsync(t));

        var sent = Assert.Single(Rest.SentTo(host));
        var ts = long.Parse(sent.Headers[WebhookSignature.TimestampHeader], CultureInfo.InvariantCulture);
        Assert.Equal(WebhookSignature.Compute(SigningValue, ts, sent.Body!), sent.Headers[WebhookSignature.SignatureHeader]);
        Assert.Equal(PublicEventCatalogue.OrganizationUnitMoved, sent.Headers[WebhookSignature.EventHeader]);
        Assert.True(sent.Body!.Length < 4096);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(["data", "id", "occurredAt", "type"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(["eventId", "resourceId", "resourceType", "resourceUrl", "version"], data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(PublicResourceTypes.OrgUnit, data.GetProperty("resourceType").GetString());
        Assert.Equal(t.Unit("Gulf"), data.GetProperty("resourceId").GetGuid());
        Assert.Equal($"{Origin(t)}/api/public/v1/org-units/{t.Unit("Gulf")}", data.GetProperty("resourceUrl").GetString());

        var deliveries = (await JsonAsync(await t["admin"].Client.GetAsync($"/api/v1/api-access/subscriptions/{subscription}/deliveries"))).EnumerateArray().ToList();
        Assert.Equal("succeeded", Assert.Single(deliveries).GetProperty("status").GetString());
        // The endpoint belongs to the subscription: the Connections page does not list it and cannot create one.
        var connections = await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/integrations/pack-connections"));
        Assert.DoesNotContain(connections.EnumerateArray(), c => c.GetProperty("kind").GetString() == ApiEventPack.KindName);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/pack-connections",
            new { kind = ApiEventPack.KindName, displayName = "X", settings = new { url = $"https://{host}/x" } })).StatusCode);

        // Another tenant's events never reach this app, and its administrators do not see the subscription.
        var other = await FreshAsync();
        await MovedAsync(other, other.Unit("Gulf"));
        await RunDueAsync(other);
        Assert.Single(Rest.SentTo(host));
        Assert.Empty((await JsonAsync(await other["admin"].Client.GetAsync("/api/v1/api-access/subscriptions"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"/api/v1/api-access/subscriptions/{subscription}/deliveries")).StatusCode);
    }

    [Fact]
    public async Task Only_catalogue_events_and_apps_with_the_scopes_may_be_subscribed()
    {
        var t = await FreshAsync();
        var (host, secretRef) = await ReceiverAsync(t);
        var (appId, _, _) = await AppAsync(t, [ScopeCatalogue.OrgRead, ScopeCatalogue.EventsSubscribe]);

        async Task<string> RefusedAsync(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        // Not in the catalogue (US4 scenario 5), not readable with the app's scopes, or not an allowlisted HTTPS URL.
        Assert.Contains("validation.api.event_unknown", await RefusedAsync(await SubscribeAsync(t, appId, "UserTaskCreated", host, secretRef)), StringComparison.Ordinal);
        Assert.Contains("validation.api.event_read_scope",
            await RefusedAsync(await SubscribeAsync(t, appId, PublicEventCatalogue.RequestCompleted, host, secretRef)), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.host_not_allowed",
            await RefusedAsync(await SubscribeAsync(t, appId, PublicEventCatalogue.OrganizationUnitMoved, Host(), secretRef)), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.secret_ref", await RefusedAsync(await SubscribeAsync(t, appId, PublicEventCatalogue.OrganizationUnitMoved, host,
            SecretReferences.For(Guid.NewGuid(), "api-events"))), StringComparison.Ordinal);

        // Application apps with events:subscribe only.
        var (without, _, _) = await AppAsync(t, [ScopeCatalogue.OrgRead]);
        Assert.Contains("validation.api.events_scope", await RefusedAsync(await SubscribeAsync(t, without, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef)),
            StringComparison.Ordinal);
        var (delegated, _, _) = await AppAsync(t, [ScopeCatalogue.OrgRead], "delegated_public");
        Assert.Contains("validation.api.events_application_only",
            await RefusedAsync(await SubscribeAsync(t, delegated, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef)), StringComparison.Ordinal);

        // Only an API administrator, only in the app's own tenant, only where the package includes event subscriptions.
        Assert.Equal(HttpStatusCode.Forbidden, (await SubscribeAsync(t, appId, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef, "gia")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await SubscribeAsync(other, appId, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef)).StatusCode);
        var basic = await FreshAsync("workplace");
        var (basicApp, _, _) = await AppAsync(basic, [ScopeCatalogue.OrgRead]);
        Assert.Equal(HttpStatusCode.Forbidden, (await SubscribeAsync(basic, basicApp, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef)).StatusCode);
        Assert.All((await JsonAsync(await basic["admin"].Client.GetAsync("/api/v1/api-access/event-catalogue"))).EnumerateArray(),
            e => Assert.False(e.GetProperty("available").GetBoolean()));

        // A second subscription of the same event to the same URL is refused.
        await JsonAsync(await SubscribeAsync(t, appId, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await SubscribeAsync(t, appId, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef)).StatusCode);
        Assert.Equal(0, await world.Database.CountInTenantAsync(basic.Id, "select count(*) from publicapi.event_subscription"));
    }

    [Fact]
    public async Task Deliveries_failing_for_3_days_pause_the_subscription_and_tell_the_administrators()
    {
        var t = await FreshAsync();
        var (host, secretRef) = await ReceiverAsync(t, HttpStatusCode.ServiceUnavailable);
        var (appId, _, _) = await AppAsync(t, [ScopeCatalogue.OrgRead, ScopeCatalogue.EventsSubscribe]);
        var subscription = (await JsonAsync(await SubscribeAsync(t, appId, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var endpoint = await EndpointAsync(t, subscription);

        await FinishedAsync(t, endpoint, ConnectorOutcomes.Failed);
        Assert.NotNull(await world.Database.TenantScalarAsync<DateTime?>(t.Id, "select failing_since from publicapi.event_subscription where id = @id", ("id", subscription)));
        // A delivery that succeeds clears the failure.
        await FinishedAsync(t, endpoint, ConnectorOutcomes.Succeeded);
        Assert.Null(await world.Database.TenantScalarAsync<DateTime?>(t.Id, "select failing_since from publicapi.event_subscription where id = @id", ("id", subscription)));

        await FinishedAsync(t, endpoint, ConnectorOutcomes.Failed);
        await world.Database.TenantScalarAsync<int>(t.Id,
            "with u as (update publicapi.event_subscription set failing_since = now() - interval '3 days 1 hour' where id = @id returning 1) select count(*)::int from u",
            ("id", subscription));
        await FinishedAsync(t, endpoint, ConnectorOutcomes.Failed);

        var paused = (await JsonAsync(await t["admin"].Client.GetAsync($"/api/v1/api-access/subscriptions?appId={appId}"))).EnumerateArray().Single();
        Assert.Equal("paused", paused.GetProperty("status").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiSubscriptionPaused), ("id", subscription.ToString())));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t["admin"].Id), ("k", SubscriptionHealth.AdminTemplate)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t["gia"].Id), ("k", SubscriptionHealth.AdminTemplate)));

        // A paused subscription gets nothing until it is resumed.
        await MovedAsync(t, t.Unit("Gulf"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from integrations.run where connection_id = @e", ("e", endpoint)));
        var resumed = await JsonAsync(await t["admin"].Client.PostAsync($"/api/v1/api-access/subscriptions/{subscription}:resume", null));
        Assert.Equal("active", resumed.GetProperty("status").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiSubscriptionResumed), ("id", subscription.ToString())));
        Rest.On(host, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await MovedAsync(t, t.Unit("Gulf"));
        Assert.Equal(1, await RunDueAsync(t));
        Assert.Single(Rest.SentTo(host));

        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.DeleteAsync($"/api/v1/api-access/subscriptions/{subscription}")).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiSubscriptionDeleted), ("id", subscription.ToString())));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from integrations.connection where id = @e", ("e", endpoint)));
    }

    [Fact]
    public async Task An_app_reads_the_catalogue_and_lists_and_removes_only_its_own_subscriptions()
    {
        var t = await FreshAsync();
        var (host, secretRef) = await ReceiverAsync(t);
        var (appId, clientId, secret) = await AppAsync(t, [ScopeCatalogue.OrgRead, ScopeCatalogue.EventsSubscribe]);
        var (otherApp, otherClient, otherSecret) = await AppAsync(t, [ScopeCatalogue.OrgRead, ScopeCatalogue.EventsSubscribe]);
        var mine = (await JsonAsync(await SubscribeAsync(t, appId, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await JsonAsync(await SubscribeAsync(t, otherApp, PublicEventCatalogue.OrganizationUnitMoved, host, secretRef), HttpStatusCode.Created);

        var token = await TokenAsync(t, clientId, secret, $"{ScopeCatalogue.OrgRead} {ScopeCatalogue.EventsSubscribe}");
        var catalogue = await JsonAsync(await SendAsync(world, t, token, HttpMethod.Get, "/api/public/v1/events"));
        Assert.Equal(PublicEventCatalogue.All.Select(e => e.Type), catalogue.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("type").GetString()));
        var own = await JsonAsync(await SendAsync(world, t, token, HttpMethod.Get, "/api/public/v1/event-subscriptions"));
        Assert.Equal(mine, Assert.Single(own.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // Another app's subscription is not found; without events:subscribe the operations are refused.
        var otherToken = await TokenAsync(t, otherClient, otherSecret, $"{ScopeCatalogue.OrgRead} {ScopeCatalogue.EventsSubscribe}");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(world, t, otherToken, HttpMethod.Delete, $"/api/public/v1/event-subscriptions/{mine}")).StatusCode);
        var narrow = await TokenAsync(t, clientId, secret, ScopeCatalogue.OrgRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(world, t, narrow, HttpMethod.Get, "/api/public/v1/event-subscriptions")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(world, t, token, HttpMethod.Delete, $"/api/public/v1/event-subscriptions/{mine}")).StatusCode);
        Assert.Empty((await JsonAsync(await SendAsync(world, t, token, HttpMethod.Get, "/api/public/v1/event-subscriptions"))).GetProperty("items").EnumerateArray());
        Assert.Single((await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/subscriptions"))).EnumerateArray());
    }
}
