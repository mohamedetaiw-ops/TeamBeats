using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R2 T026 (US6 scenario 6, FR-042, FR-043): when two editors change the same page, the second save is refused
/// with both versions and who saved the other; they merge the two, keep theirs, or take the stored one, and every
/// replaced draft is kept so nothing is lost. Editors of other units and other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PageEditConflictTests(TenantWorld world)
{
    private const string Pages = "/api/v1/experience/pages";

    private static readonly object Text = new { body = new { en = "Visitors sign in at the gate.", ar = "يسجل الزوار عند البوابة." } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static JsonNode With(JsonNode draft, params string[] placements)
    {
        var copy = draft.DeepClone();
        copy["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "main")!["placements"] = new JsonArray(placements
            .Select(id => (JsonNode)new JsonObject { ["id"] = id, ["componentRef"] = "reference.alpha.text@1", ["props"] = JsonSerializer.SerializeToNode(Text) }).ToArray());
        return copy;
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string id, JsonNode definition, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Pages}/{id}/draft") { Content = JsonContent.Create(new { definition }) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> MergeAsync(HttpClient client, string id, JsonNode definition, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Pages}/{id}/draft/merge") { Content = JsonContent.Create(new { definition }) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    private static string[] MainOf(JsonElement definition) =>
        definition.GetProperty("regions").EnumerateArray().First(r => r.GetProperty("id").GetString() == "main")
            .GetProperty("placements").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task A_second_save_gets_both_versions_merges_them_and_every_replaced_draft_is_kept()
    {
        var t = await world.CreateTenantAsync("pec-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var (name, unit) in new[] { ("sara", gulf), ("omar", gulf), ("far", africa) })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, name == "far" ? "Africa" : "Gulf");
            await world.AssignAsync(t, name, "page_editor", "org_subtree", unit);
        }
        var created = await t["sara"].Client.PostAsJsonAsync(Pages, new { templateId = "T01", slug = "c-" + Guid.NewGuid().ToString("N")[..10], orgUnitId = gulf });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var id = page["id"]!.GetValue<string>();
        var start = page["eTag"]!.GetValue<string>();
        var draft = page["draft"]!;

        // Both open the same version; Sara saves first.
        var sara = await OkAsync(SaveAsync(t["sara"].Client, id, With(draft, "rules"), start));
        var saraTag = sara.GetProperty("eTag").GetString()!;

        // Omar's save is refused with Sara's version and who saved it; his own stays with him.
        var stale = await SaveAsync(t["omar"].Client, id, With(draft, "visitors"), start);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(saraTag, problem.GetProperty("etag").GetString());
        Assert.Equal(t["sara"].Id, problem.GetProperty("currentBy").GetGuid());
        Assert.True(problem.TryGetProperty("currentAt", out _));
        Assert.Equal(["rules"], MainOf(problem.GetProperty("current")));

        // He merges: both placements, against Sara's version, nothing saved yet.
        var merged = await OkAsync(MergeAsync(t["omar"].Client, id, With(draft, "visitors"), start));
        Assert.Equal(saraTag, merged.GetProperty("eTag").GetString());
        Assert.Empty(merged.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(["rules", "visitors"], MainOf(merged.GetProperty("definition")));
        Assert.Equal(["rules"], MainOf((await OkAsync(t["sara"].Client.GetAsync($"{Pages}/{id}"))).GetProperty("draft")));
        var saved = await OkAsync(SaveAsync(t["omar"].Client, id, JsonNode.Parse(merged.GetProperty("definition").GetRawText())!, saraTag));

        // Sara now keeps hers over his: his merged version is still kept and can be opened again.
        var overwritten = await OkAsync(SaveAsync(t["sara"].Client, id, With(draft, "rules"), saved.GetProperty("eTag").GetString()!));
        var revisions = (await OkAsync(t["sara"].Client.GetAsync($"{Pages}/{id}/draft/revisions"))).EnumerateArray().ToList();
        Assert.Equal(revisions.Select(r => r.GetProperty("revision").GetInt32()).OrderDescending(), revisions.Select(r => r.GetProperty("revision").GetInt32()));
        Assert.Equal(3, revisions.Count);
        var his = revisions[0];
        Assert.Equal(t["omar"].Id, his.GetProperty("updatedBy").GetGuid());
        Assert.Equal(["rules", "visitors"], MainOf(await OkAsync(t["sara"].Client.GetAsync($"{Pages}/{id}/draft/revisions/{his.GetProperty("revision").GetInt32()}"))));
        Assert.NotEqual(saved.GetProperty("eTag").GetString(), overwritten.GetProperty("eTag").GetString());

        // A base that never existed, or one no longer kept, is not merged.
        Assert.Equal(HttpStatusCode.BadRequest, (await MergeAsync(t["omar"].Client, id, With(draft, "x"), "\"r999\"")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MergeAsync(t["omar"].Client, id, With(draft, "x"), "")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["sara"].Client.GetAsync($"{Pages}/{id}/draft/revisions/999")).StatusCode);

        // An editor of another unit and another tenant find nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await MergeAsync(t["far"].Client, id, With(draft, "x"), start)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["far"].Client.GetAsync($"{Pages}/{id}/draft/revisions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync($"{Pages}/{id}/draft/revisions")).StatusCode);
    }
}
