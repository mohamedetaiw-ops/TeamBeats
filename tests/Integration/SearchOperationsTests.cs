using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Search;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 012 FR-006 and FR-013: indexing lag is measured per consumer and a change handled after the 5 minute objective
/// is counted for the operators' alert; every first results page records a pseudonymous query event with the number of
/// results and the filter, never the query text.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SearchOperationsTests(TenantWorld world)
{
    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Indexing_after_the_objective_is_counted_and_query_events_never_keep_the_query()
    {
        var t = await world.CreateTenantAsync("so-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var admin = t["admin"].Client;
        var processor = world.Factory.Services.GetRequiredService<MessageProcessor>();
        Assert.Equal(SearchIndexer.LagObjective, processor.ObjectiveFor("search.index_content_published"));
        Assert.Null(processor.ObjectiveFor("analytics.write_event"));

        var exceeded = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "TeamBeats.Messaging" && instrument.Name == "messaging.consumer.lag_exceeded")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "consumer" && tag.Value is string name)
                {
                    lock (exceeded)
                    {
                        exceeded.Add(name);
                    }
                }
            }
        });
        listener.Start();

        // A publication whose event waited ten minutes is indexed, and counted as over the objective.
        var created = await OkAsync(admin.PostAsJsonAsync("/api/v1/content/items", new
        {
            type = "news", variants = new Dictionary<string, object> { ["en"] = new { title = "Shuttle timetable", body = Body("The shuttle leaves at six.") } },
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(admin.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { }));
        await world.Database.OwnerExecAsync(
            "update messaging.outbox set occurred_at = occurred_at - interval '10 minutes' where tenant_id = @t and dispatched_at is null", ("t", t.Id));
        await DispatchAsync();
        lock (exceeded)
        {
            Assert.Contains("search.index_content_published", exceeded);
        }

        // Query events: the first page only, with the results and the filter; the words searched are never stored.
        var page = await OkAsync(admin.GetAsync("/api/v1/search?q=shuttle%20timetable"));
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        await OkAsync(admin.GetAsync("/api/v1/search?q=shuttle&kind=content"));
        await OkAsync(admin.GetAsync("/api/v1/search?q=shuttle&page=2"));
        await DispatchAsync();
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count(*) from analytics.event where name = @n and user_key is not null",
            ("n", AnalyticsEvents.SearchQueried)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from analytics.event where name = @n and fields->>'results' = '1' and fields->>'kind' = 'content'", ("n", AnalyticsEvents.SearchQueried)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from analytics.event where fields::text ilike '%shuttle%'"));
    }
}
