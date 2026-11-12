using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 029 T012, non-functional requirements: a board of 500 tasks loads under 1.5 seconds p95, and My tasks under
/// 800 ms p95 for a person with 200 open tasks across 10 spaces. Tasks are added through the API, as people add them.
/// Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SpacesPerformanceTests(TenantWorld world, ITestOutputHelper output)
{
    private const string Api = "/api/v1/spaces";

    private static double P95(List<double> timings)
    {
        timings.Sort();
        return timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<double> P95Async(HttpClient client, string url, int runs = 40)
    {
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync(url)).EnsureSuccessStatusCode();
        }
        var timings = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await client.GetAsync(url);
            watch.Stop();
            response.EnsureSuccessStatusCode();
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        return P95(timings);
    }

    [Fact]
    public async Task A_board_of_500_tasks_loads_under_1500_ms_p95_and_my_tasks_under_800_ms_p95()
    {
        var t = await world.CreateTenantAsync("spf-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "omar", "laila" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        var omar = t["omar"].Client;
        var laila = t["laila"].Id;

        async Task<Guid> SpaceAsync(string name) =>
            (await OkAsync(omar.PostAsJsonAsync(Api, new
            {
                name, description = "", visibility = "private", template = "project", members = new object[] { new { userId = laila, role = "member" } },
            }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // One busy board: 500 tasks, every fifth given to Laila with a due date.
        var board = await SpaceAsync("Data centre move");
        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        await Parallel.ForEachAsync(Enumerable.Range(0, 500), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, ct) =>
            await OkAsync(omar.PostAsJsonAsync($"{Api}/{board}/tasks", new
            {
                title = $"Move rack {i:000}", priority = i % 7 == 0 ? "high" : "normal", dueOn = i % 5 == 0 ? due : (DateOnly?)null,
                assignees = i % 5 == 0 ? new[] { laila } : Array.Empty<Guid>(),
            }, ct), HttpStatusCode.Created));
        // Nine more spaces with 11 or 12 of Laila's tasks each, so My tasks covers 200 open tasks in 10 spaces.
        for (var s = 0; s < 9; s++)
        {
            var space = await SpaceAsync($"Site {s + 1}");
            var count = s < 2 ? 12 : 11;
            for (var i = 0; i < count; i++)
            {
                await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = $"Survey room {i + 1}", dueOn = due.AddDays(i), assignees = new[] { laila } }),
                    HttpStatusCode.Created);
            }
        }

        var cards = (await OkAsync(omar.GetAsync($"{Api}/{board}/tasks"))).GetProperty("tasks").GetArrayLength();
        Assert.True(cards >= 500, $"The board has {cards} tasks");
        var boardP95 = await P95Async(omar, $"{Api}/{board}/tasks");
        output.WriteLine($"board of {cards} tasks p95 {boardP95:F1} ms");
        Assert.True(boardP95 < 1500, $"Board p95 {boardP95:F1} ms is over 1.5 seconds");

        var lailaClient = t["laila"].Client;
        var mine = (await OkAsync(lailaClient.GetAsync($"{Api}/my-tasks"))).GetArrayLength();
        Assert.True(mine >= 200, $"My tasks lists {mine} tasks");
        var mineP95 = await P95Async(lailaClient, $"{Api}/my-tasks");
        output.WriteLine($"my tasks ({mine}) p95 {mineP95:F1} ms");
        Assert.True(mineP95 < 800, $"My tasks p95 {mineP95:F1} ms is over 800 ms");

        // The assignment notifications are dispatched here, so tests that drain the shared outbox are not left behind them.
        await world.DispatchTenantAsync(t.Id, 400);
    }
}
