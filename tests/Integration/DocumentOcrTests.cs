using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 R2 FR-020 (T026): the text of PDFs and pictures is read after upload by the text service and indexed for
/// search. A PDF's own text layer is always read; scanned pages and pictures are recognized by OCR only where the tenant
/// has <c>docs.ocr</c>, in English and Arabic. A restored version keeps the text, the service being down changes
/// nothing for the upload, and another tenant never finds the text. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DocumentOcrTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";

    private FakeTika Tika => world.Factory.Tika;

    private async Task<(TestTenant T, Guid Library)> FreshAsync(string package)
    {
        var t = await world.CreateTenantAsync("ocr-" + Guid.NewGuid().ToString("N")[..8], package);
        var library = await OkAsync(t["admin"].Client.PostAsJsonAsync(Libraries, new { names = new Dictionary<string, string> { ["en"] = "Archive" }, scope = "tenant" }),
            HttpStatusCode.Created);
        return (t, library.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Guid> UploadAsync(TestTenant t, Guid library, string name, string content) =>
        (await OkAsync(t["admin"].Client.PostAsync($"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish=true",
            new ByteArrayContent(Encoding.UTF8.GetBytes(content))), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static async Task<Guid[]> FindAsync(TestTenant t, string q)
    {
        var page = await OkAsync(t["admin"].Client.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}"));
        return page.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("itemId").GetGuid()).ToArray();
    }

    private Task<string?> TextAsync(TestTenant t, Guid document) =>
        world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IDbSession>().ScalarAsync<string>(
            "select v.text_extract from documents.document_version v join documents.document d on d.current_version_id = v.id where d.id = @document",
            new { document }, CancellationToken.None));

    [Fact]
    public async Task Scans_and_pictures_are_recognized_where_the_tenant_has_OCR_and_found_in_search()
    {
        var (t, library) = await FreshAsync("workplace_plus");
        var scan = await UploadAsync(t, library, "inspection.pdf", "%PDF-1.7\ntext:Inspection report cover\nscan:Sprinkler valves tested in zone four");
        var picture = await UploadAsync(t, library, "whiteboard.png", "PNG\nscan:Evacuation assembly point beside gate seven");
        var arabic = await UploadAsync(t, library, "sign.jpg", "JPG\nscan:نقطة التجمع عند البوابة السابعة");
        var recognitions = Tika.Recognitions;
        await DispatchAsync();

        Assert.True(Tika.Recognitions >= recognitions + 3);
        Assert.Equal("eng+ara", Tika.LastLanguages);
        Assert.Equal("Inspection report cover\n\nSprinkler valves tested in zone four", await TextAsync(t, scan));
        Assert.Contains(scan, await FindAsync(t, "sprinkler valves"));
        Assert.Contains(picture, await FindAsync(t, "evacuation assembly"));
        Assert.Contains(arabic, await FindAsync(t, "البوابة"));

        // Restoring the first version keeps its text; the new version of the same file needs no second read.
        await OkAsync(t["admin"].Client.PostAsync($"/api/v1/documents/{scan}/checkout", null));
        var second = await OkAsync(t["admin"].Client.PostAsync($"/api/v1/documents/{scan}/checkin?publish=true&name=inspection.pdf",
            new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.7\nscan:Hydrant pressure recorded"))));
        await DispatchAsync();
        Assert.Contains(scan, await FindAsync(t, "hydrant pressure"));
        Assert.DoesNotContain(scan, await FindAsync(t, "sprinkler valves"));
        var first = (await OkAsync(t["admin"].Client.GetAsync($"/api/v1/documents/{scan}/versions"))).EnumerateArray()
            .Single(v => v.GetProperty("label").GetString() == "1.0").GetProperty("id").GetGuid();
        Assert.Equal("2.0", second.GetProperty("effective").GetProperty("label").GetString());
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/documents/{scan}/restore", new { versionId = first }));
        Assert.Equal("Inspection report cover\n\nSprinkler valves tested in zone four", await TextAsync(t, scan));

        // Another tenant never finds the text.
        var (other, _) = await FreshAsync("workplace_plus");
        Assert.DoesNotContain(scan, await FindAsync(other, "sprinkler valves"));
    }

    [Fact]
    public async Task Without_OCR_only_a_PDFs_own_text_is_read_and_a_service_outage_changes_nothing()
    {
        var (t, library) = await FreshAsync("workplace");
        var recognitions = Tika.Recognitions;
        var scan = await UploadAsync(t, library, "manual.pdf", "%PDF-1.7\ntext:Boiler manual\nscan:Pressure relief valve setting");
        var picture = await UploadAsync(t, library, "label.png", "PNG\nscan:Boiler room access code");
        await DispatchAsync();

        Assert.Equal("Boiler manual", await TextAsync(t, scan));
        Assert.Null(await TextAsync(t, picture));
        Assert.Equal(recognitions, Tika.Recognitions);
        Assert.Contains(scan, await FindAsync(t, "boiler manual"));
        Assert.Empty(await FindAsync(t, "relief valve"));
        Assert.Empty(await FindAsync(t, "access code"));

        // The service is down: the upload is stored as usual and the file stays findable by its name.
        Tika.Down = true;
        try
        {
            var down = await UploadAsync(t, library, "rota.pdf", "%PDF-1.7\ntext:Night shift rota");
            await DispatchAsync();
            Assert.Null(await TextAsync(t, down));
            Assert.Contains(down, await FindAsync(t, "rota"));
        }
        finally
        {
            Tika.Down = false;
        }
    }
}
