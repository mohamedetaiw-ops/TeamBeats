using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T003 and T004: declared events only, with unknown events and fields dropped; pseudonymous keys from a
/// per-tenant salt with the person's unit at event time; nonessential events stop when the tenant turns them off while
/// essential errors continue without a key; the policy is changed only by tenant administrators and audited; module
/// events go through the outbox; aggregates count views, unique readers and active people; raw events past retention are
/// removed; and nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AnalyticsPipelineTests(TenantWorld world)
{
    private const string Events = "/api/v1/analytics/events";
    private const string Policy = "/api/v1/analytics/policy";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("an-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        await world.CreateUnitAsync(t, "Plant", null, "subsidiary");
        foreach (var name in new[] { "ali", "bea" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "ali", "Plant");
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<JsonElement> SendAsync(HttpClient client, params object[] events) =>
        OkAsync(client.PostAsJsonAsync(Events, new { events }), HttpStatusCode.Accepted);

    private static object Viewed(Guid item, string source = "content") => new { name = AnalyticsEvents.ContentViewed, fields = new { source, itemId = item } };

    private Task<long> CountAsync(TestTenant t, string sql, params (string, object)[] args) => world.Database.CountInTenantAsync(t.Id, sql, args);

    private Task<int> AggregateAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var read = await sp.GetRequiredService<AnalyticsAggregator>().RunAsync(5000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return read;
        });

    [Fact]
    public async Task Only_declared_events_and_fields_are_kept_with_a_pseudonymous_key_and_the_unit_at_event_time()
    {
        var t = await FreshAsync();
        var item = Guid.NewGuid();
        var result = await SendAsync(t["ali"].Client,
            Viewed(item),
            new { name = AnalyticsEvents.ContentViewed, fields = new { source = "content", itemId = item, title = "Evacuation procedure" } },
            new { name = "keystrokes.counted", fields = new { count = 3 } },
            new { name = AnalyticsEvents.ContentViewed, fields = new { source = "intranet", itemId = item } },
            new { name = AnalyticsEvents.ContentViewed, fields = new { source = "content" } },
            new { name = AnalyticsEvents.SearchQueried, fields = new { results = "many" } },
            new { name = AnalyticsEvents.PageViewed, channel = "server", fields = new { area = "home" } },
            new { name = AnalyticsEvents.PageViewed, occurredAt = DateTimeOffset.UtcNow.AddDays(-8), fields = new { area = "home" } },
            new { name = AnalyticsEvents.PageViewed, channel = "mobile", occurredAt = DateTimeOffset.UtcNow.AddDays(-6), fields = new { area = "home" } });
        Assert.Equal(3, result.GetProperty("accepted").GetInt32());
        Assert.Equal(6, result.GetProperty("dropped").GetInt32());

        // The undeclared field is gone; no free text is ever stored.
        Assert.Equal(0, await CountAsync(t, "select count(*) from analytics.event where fields ? 'title'"));
        Assert.Equal(2, await CountAsync(t, "select count(*) from analytics.event where name = 'content.viewed' and fields->>'itemId' = @i", ("i", item.ToString())));
        Assert.Equal(1, await CountAsync(t, "select count(*) from analytics.event where channel = 'mobile' and occurred_at < now() - interval '5 days'"));

        // The key is 32 hex characters, the same for one person and never the user ID; the unit is the person's at the time.
        var keys = await CountAsync(t, "select count(distinct user_key) from analytics.event");
        Assert.Equal(1, keys);
        var key = await world.Database.ScalarInTenantAsync<string>(t.Id, "select user_key from analytics.event limit 1");
        Assert.Matches("^[0-9a-f]{32}$", key!);
        Assert.DoesNotContain(t["ali"].Id.ToString("N")[..12], key!, StringComparison.Ordinal);
        Assert.Equal(3, await CountAsync(t, "select count(*) from analytics.event where org_unit_id = @u", ("u", t.Unit("Plant"))));
        Assert.Equal(Pseudonyms.Key((await world.Database.ScalarInTenantAsync<byte[]>(t.Id, "select salt from analytics.tenant_key"))!, t["ali"].Id), key);

        // A person without a unit is counted under no unit; batches are at most 20 events.
        await SendAsync(t["bea"].Client, Viewed(item));
        Assert.Equal(1, await CountAsync(t, "select count(*) from analytics.event where org_unit_id is null and user_key is not null"));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await t["bea"].Client.PostAsJsonAsync(Events, new { events = Enumerable.Range(0, 21).Select(_ => Viewed(item)).ToArray() })).StatusCode);

        // The same person ID in another tenant gets a different key, because each tenant has its own salt.
        var other = await world.CreateTenantAsync("an-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        await SendAsync(other["admin"].Client, Viewed(item));
        var otherSalt = (await world.Database.ScalarInTenantAsync<byte[]>(other.Id, "select salt from analytics.tenant_key"))!;
        Assert.NotEqual(key, Pseudonyms.Key(otherSalt, t["ali"].Id));
        Assert.Equal(1, await world.Database.CountInTenantAsync(other.Id, "select count(*) from analytics.event"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.Id, "select count(*) from analytics.event where fields->>'itemId' = @i and org_unit_id is not null",
            ("i", item.ToString())));
    }

    [Fact]
    public async Task Turning_nonessential_analytics_off_stops_people_events_and_keeps_errors_without_a_key()
    {
        var t = await FreshAsync();
        // Only tenant administrators change the policy; employees cannot read it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.GetAsync(Policy)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PutAsJsonAsync(Policy, new { nonessential = false, version = 0 })).StatusCode);
        var policy = await OkAsync(t["admin"].Client.GetAsync(Policy));
        Assert.True(policy.GetProperty("nonessential").GetBoolean());
        Assert.Equal(13, policy.GetProperty("rawRetentionMonths").GetInt32());
        Assert.Equal(5, policy.GetProperty("minGroupSize").GetInt32());
        Assert.False(policy.GetProperty("individual").GetBoolean());
        Assert.True(policy.GetProperty("canChange").GetBoolean());

        // Limits: retention 3 to 25 months, groups never below 5, individual analytics stay off, and the version locks.
        var invalid = await t["admin"].Client.PutAsJsonAsync(Policy, new { rawRetentionMonths = 2, minGroupSize = 4, individual = true, version = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var keys = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()).RootElement.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order().ToArray();
        Assert.Equal(["individual", "minGroupSize", "rawRetentionMonths"], keys);
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PutAsJsonAsync(Policy, new { nonessential = false, version = 7 })).StatusCode);

        var off = await OkAsync(t["admin"].Client.PutAsJsonAsync(Policy, new { nonessential = false, rawRetentionMonths = 6, version = 0 }));
        Assert.False(off.GetProperty("nonessential").GetBoolean());
        Assert.Equal(1, off.GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AnalyticsPrivacySettingChanged);

        var item = Guid.NewGuid();
        var result = await SendAsync(t["ali"].Client, Viewed(item),
            new { name = AnalyticsEvents.UiError, fields = new { kind = "boundary", release = "2026.11.9", component = "core.news_list@2", correlationId = "abc123" } });
        Assert.Equal(1, result.GetProperty("accepted").GetInt32());
        Assert.Equal(0, await CountAsync(t, "select count(*) from analytics.event where name = 'content.viewed'"));
        Assert.Equal(1, await CountAsync(t, "select count(*) from analytics.event where name = 'ui.error' and user_key is null and org_unit_id is null and location is null"));
    }

    [Fact]
    public async Task Module_events_go_through_the_outbox_and_aggregates_count_views_readers_and_active_people()
    {
        var t = await FreshAsync();
        var item = Guid.NewGuid();
        await SendAsync(t["ali"].Client, Viewed(item), Viewed(item), new { name = AnalyticsEvents.PageViewed, fields = new { area = "home" } });
        await SendAsync(t["bea"].Client, Viewed(item));

        // A module emits as the signed-in person through the outbox; an undeclared event or field is a programming error.
        await world.Factory.AsUserAsync(t.Id, t["bea"].Id, async sp =>
        {
            var events = sp.GetRequiredService<IAnalyticsEvents>();
            await events.EmitAsync(AnalyticsEvents.SearchQueried, new Dictionary<string, object?> { ["results"] = 4, ["kind"] = "policy" });
            await Assert.ThrowsAsync<ArgumentException>(() => events.EmitAsync("search.typed", new Dictionary<string, object?>()));
            await Assert.ThrowsAsync<ArgumentException>(() => events.EmitAsync(AnalyticsEvents.SearchQueried, new Dictionary<string, object?> { ["results"] = 1, ["query"] = "salary" }));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        Assert.Equal(1, await world.Database.OutboxCountAsync(nameof(AnalyticsEventEmitted), t.Id));
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
        Assert.Equal(1, await CountAsync(t, "select count(*) from analytics.event where name = 'search.queried' and channel = 'server' and (fields->>'results')::int = 4"));

        Assert.Equal(5, await AggregateAsync(t));
        Assert.Equal(0, await AggregateAsync(t));
        Assert.Equal(3, await CountAsync(t, "select coalesce(sum(views), 0) from analytics.item_daily where item_id = @i", ("i", item)));
        Assert.Equal(2, await CountAsync(t, "select coalesce(sum(readers), 0) from analytics.item_daily where item_id = @i", ("i", item)));
        Assert.Equal(1, await CountAsync(t, "select coalesce(sum(readers), 0) from analytics.item_daily where item_id = @i and org_unit_id = @u", ("i", item), ("u", t.Unit("Plant"))));

        // A second visit adds a view, never a second reader.
        await SendAsync(t["ali"].Client, Viewed(item));
        Assert.Equal(1, await AggregateAsync(t));
        Assert.Equal(4, await CountAsync(t, "select coalesce(sum(views), 0) from analytics.item_daily where item_id = @i", ("i", item)));
        Assert.Equal(2, await CountAsync(t, "select count(*) from analytics.item_reader where item_id = @i", ("i", item)));

        // Two people were active this week: one in the plant, one with no unit.
        Assert.Equal(2, await CountAsync(t, "select active_users from analytics.adoption_weekly where org_unit_id = @all", ("all", AnalyticsAggregator.WholeTenant)));
        Assert.Equal(1, await CountAsync(t, "select active_users from analytics.adoption_weekly where org_unit_id = @u", ("u", t.Unit("Plant"))));

        // Another tenant's run sees none of this tenant's events.
        var other = await world.CreateTenantAsync("an-y-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(0, await AggregateAsync(other));
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.Id, "select count(*) from analytics.item_daily"));
    }

    [Fact]
    public async Task Raw_events_past_the_tenants_retention_are_removed_and_aggregates_stay()
    {
        var t = await FreshAsync();
        var item = Guid.NewGuid();
        await SendAsync(t["ali"].Client, Viewed(item), Viewed(item));
        await AggregateAsync(t);
        await world.Database.OwnerExecInTenantAsync(t.Id,
            "update analytics.event set occurred_at = now() - interval '14 months' where seq = (select min(seq) from analytics.event)");
        var removed = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var n = await sp.GetRequiredService<AnalyticsRetention>().PurgeAsync(DateTimeOffset.UtcNow, 1000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });
        Assert.Equal(1, removed);
        Assert.Equal(1, await CountAsync(t, "select count(*) from analytics.event"));
        Assert.Equal(2, await CountAsync(t, "select coalesce(sum(views), 0) from analytics.item_daily where item_id = @i", ("i", item)));
    }
}
