using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R2 T029 (FR-007): editors resize regions within the layout's limits. The widths are kept with the draft, go
/// live with the release, and a width past the limits or one that would make regions of a row overlap is refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class RegionResizeTests(TenantWorld world)
{
    private const string Pages = "/api/v1/experience/pages";

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, JsonNode page, object? spans)
    {
        var draft = page["draft"]!.DeepClone();
        draft["spans"] = spans is null ? null : JsonSerializer.SerializeToNode(spans);
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Pages}/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        return client.SendAsync(request);
    }

    private static async Task<string[]> CodesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).ToArray();
    }

    [Fact]
    public async Task A_resized_region_is_kept_published_and_never_overlaps_its_neighbour()
    {
        var admin = world.A["admin"].Client;
        var created = await admin.PostAsJsonAsync(Pages, new { templateId = "T03", slug = "rz-" + Guid.NewGuid().ToString("N")[..10] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var id = page["id"]!.GetValue<string>();

        // The editor reads the layout's limits with the page.
        var view = JsonNode.Parse(await admin.GetStringAsync($"{Pages}/{id}"))!;
        var aside = view["layoutRegions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "aside")!;
        Assert.Equal(4, aside["span"]!.GetValue<int>());
        Assert.Equal(3, aside["minSpan"]!.GetValue<int>());
        Assert.Equal(5, aside["maxSpan"]!.GetValue<int>());

        // Past the limits, overlapping, or on a region that does not resize: refused, and nothing changes.
        Assert.Contains("validation.page.region_span_invalid", await CodesAsync(await SaveAsync(admin, page, new { aside = 6, main = 6 })));
        Assert.Contains("validation.page.region_span_overlap", await CodesAsync(await SaveAsync(admin, page, new { aside = 5, main = 9 })));
        Assert.Contains("validation.page.region_unknown", await CodesAsync(await SaveAsync(admin, page, new { hero = 6 })));

        // Within the limits it is kept with the draft and drawn by the preview and the release.
        var saved = await SaveAsync(admin, page, new { aside = 3, main = 9 });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var draft = JsonNode.Parse(await admin.GetStringAsync($"{Pages}/{id}"))!["draft"]!;
        Assert.Equal(3, draft["spans"]!["aside"]!.GetValue<int>());
        var preview = JsonDocument.Parse(await admin.GetStringAsync($"{Pages}/{id}/preview")).RootElement;
        Assert.Equal(3, preview.GetProperty("regions").EnumerateArray().First(r => r.GetProperty("id").GetString() == "aside").GetProperty("span").GetInt32());
        var published = await admin.PostAsJsonAsync($"{Pages}/{id}/publish", new { reason = "Narrow side" });
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
        var shown = JsonDocument.Parse(await admin.GetStringAsync($"/api/v1/experience/render?slug={page["slug"]}")).RootElement;
        var spans = shown.GetProperty("regions").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!, r => r.GetProperty("span").GetInt32());
        Assert.Equal(3, spans["aside"]);
        Assert.Equal(9, spans["main"]);

        // Another tenant cannot resize it.
        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(world.B["admin"].Client, page, new { aside = 4, main = 8 })).StatusCode);
    }
}
