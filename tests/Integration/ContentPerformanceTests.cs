using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 T024, section 15: the feed p95 under 800 ms for a reader in 50 units with 10,000 published items in the
/// tenant; the critical check on page loads under 20 ms p95; a critical announcement to 100,000 people tracked within two
/// minutes. Items and members are written straight into the tables, so the measurements cover the endpoints and the
/// background step as they run for users.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentPerformanceTests(TenantWorld world, ITestOutputHelper output)
{
    private const int Items = 10_000;
    private const int Units = 50;
    private const int Audience = 100_000;

    private static double P95(List<double> timings)
    {
        timings.Sort();
        return timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
    }

    private async Task<List<double>> TimeAsync(HttpClient client, string url, int rounds)
    {
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync(url)).EnsureSuccessStatusCode();
        }
        var timings = new List<double>();
        for (var i = 0; i < rounds; i++)
        {
            var clock = Stopwatch.StartNew();
            var response = await client.GetAsync(url);
            clock.Stop();
            response.EnsureSuccessStatusCode();
            timings.Add(clock.Elapsed.TotalMilliseconds);
        }
        return timings;
    }

    private Task SeedAsync(TestTenant t, string sql, object args) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            await Sql.ExecAsync(session, sql, args);
            await session.CommitAsync();
            return true;
        });

    [Fact]
    public async Task Feed_p95_stays_under_800_ms_and_the_critical_check_under_20_ms_at_10000_items()
    {
        var t = await world.CreateTenantAsync("cpf-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "reader", MembershipTypes.Employee);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var units = new List<Guid>();
        for (var i = 0; i < Units; i++)
        {
            units.Add(await world.CreateUnitAsync(t, $"Unit{i}", holding, "department"));
            await world.AddMembershipAsync(t, "reader", $"Unit{i}");
        }
        var elsewhere = await world.CreateUnitAsync(t, "Elsewhere", holding, "department");
        var reader = t["reader"].Client;
        (await reader.GetAsync("/api/v1/content/feed")).EnsureSuccessStatusCode();
        var sites = await (await t["admin"].Client.GetAsync("/api/v1/content/sites")).ReadAsync<JsonElement>();
        var site = sites[0].GetProperty("id").GetGuid();

        // A third of the items target a unit the reader is not in, a third one of the reader's units, the rest everyone;
        // two are critical banners the reader has not confirmed.
        await SeedAsync(t,
            """
            insert into content.content_item (tenant_id, id, site_id, type, primary_locale, audience, status, published_version, published_at, created_by)
            select platform.current_tenant_id(), md5('item' || i)::uuid, @site, (array['news','announcement','page'])[1 + i % 3], 'en',
                   case i % 3 when 0 then jsonb_build_object('anyOf', jsonb_build_array(jsonb_build_object('orgUnits', jsonb_build_array(jsonb_build_object('id', @elsewhere::text, 'subtree', true)))))
                              when 1 then jsonb_build_object('anyOf', jsonb_build_array(jsonb_build_object('orgUnits', jsonb_build_array(jsonb_build_object('id', (@units::uuid[])[1 + i % 50]::text, 'subtree', true)))))
                              else '{}'::jsonb end,
                   'published', 1, now() - make_interval(mins => i), @author
            from generate_series(1, @n) as i;
            insert into content.content_version (tenant_id, item_id, number, variants, author_user_id, published_at)
            select platform.current_tenant_id(), md5('item' || i)::uuid, 1,
                   jsonb_build_object('en', jsonb_build_object('title', 'Item ' || i, 'summary', 'Summary ' || i,
                     'body', jsonb_build_object('blocks', jsonb_build_array(jsonb_build_object('type', 'paragraph', 'children', jsonb_build_array(jsonb_build_object('text', repeat('Text ', 40)))))))),
                   @author, now() - make_interval(mins => i)
            from generate_series(1, @n) as i;
            update content.content_item set critical = '{"presentation":"banner","requireAck":true,"remindLaterMax":0,"deadlineAt":null,"dismissible":false}'::jsonb
            where id in (md5('item2')::uuid, md5('item5')::uuid);
            insert into content.audience_member (tenant_id, item_id, user_id)
            select platform.current_tenant_id(), id, @reader from content.content_item where critical is not null;
            analyze content.content_item;
            analyze content.content_version;
            """, new { site, elsewhere, units = units.ToArray(), n = Items, author = t["admin"].Id, reader = t["reader"].Id });
        Assert.True(await world.Database.CountInTenantAsync(t.Id, "select count(*) from content.content_item where status = 'published'") >= Items);

        var feed = await TimeAsync(reader, "/api/v1/content/feed?limit=20", 40);
        var page = await (await reader.GetAsync("/api/v1/content/feed?limit=20")).ReadAsync<JsonElement>();
        Assert.Equal(20, page.GetProperty("items").GetArrayLength());
        var pending = await TimeAsync(reader, "/api/v1/content/critical/pending", 60);
        var banners = (await (await reader.GetAsync("/api/v1/content/critical/pending")).ReadAsync<JsonElement>()).GetProperty("banners").GetArrayLength();
        Assert.Equal(2, banners);
        output.WriteLine($"feed p95 {P95(feed):F1} ms, critical check p95 {P95(pending):F1} ms");
        Assert.True(P95(feed) < 800, $"Feed p95 {P95(feed):F1} ms is over 800 ms");
        Assert.True(P95(pending) < 20, $"Critical check p95 {P95(pending):F1} ms is over 20 ms");
    }

    [Fact]
    public async Task A_critical_announcement_to_100000_people_is_tracked_within_two_minutes()
    {
        var t = await world.CreateTenantAsync("cpa-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into tenancy.tenant_membership (tenant_id, user_id, membership_type, status)
            select @t, gen_random_uuid(), 'employee', 'active' from generate_series(1, @n)
            """, ("t", t.Id), ("n", Audience));
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync("/api/v1/content/items", new
        {
            type = "announcement",
            variants = new Dictionary<string, object> { ["en"] = new { title = "Evacuation drill", body = new { blocks = Array.Empty<object>() } } },
            critical = new { presentation = "banner", requireAck = true },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();

        var clock = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { material = true })).StatusCode);
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        while (await world.Database.CountInTenantAsync(t.Id, "select count(*) from content.audience_member where item_id = @id", ("id", id)) < Audience
               && clock.Elapsed < TimeSpan.FromMinutes(2))
        {
            await dispatcher.DispatchBatchAsync(100, CancellationToken.None);
        }
        clock.Stop();
        var tracked = await world.Database.CountInTenantAsync(t.Id, "select count(*) from content.audience_member where item_id = @id", ("id", id));
        output.WriteLine($"{tracked} people tracked in {clock.Elapsed.TotalSeconds:F1} s");
        // The notices follow in batches of their own; they are dropped here so they do not slow other tests that
        // dispatch the shared outbox.
        Assert.Equal((Audience + 1 + 499) / 500, await world.Database.OwnerScalarAsync<long>(
            "select count(*) from messaging.outbox where tenant_id = @t and message_type = 'CriticalNoticeBatch' and dispatched_at is null", ("t", t.Id)));
        await world.Database.OwnerExecAsync("delete from messaging.outbox where tenant_id = @t and message_type = 'CriticalNoticeBatch'", ("t", t.Id));
        Assert.True(tracked >= Audience, $"Only {tracked} of {Audience} people tracked after {clock.Elapsed.TotalSeconds:F0} s");
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(2));
    }
}
