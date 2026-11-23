using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R2 T028 (FR-022): a template designer exports a template as a documented JSON document that carries structure
/// and policies and nothing tenant-specific, and imports one as a new template whose first version is a draft, checked as any
/// draft. Other tenants and people who do not design templates reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TemplateTransferTests(TenantWorld world)
{
    private const string Templates = "/api/v1/experience/templates";

    private static string NewId() => "tpx-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<JsonNode> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("errors").GetProperty(field)[0].GetProperty("key").GetString()!;
    }

    [Fact]
    public async Task An_exported_template_carries_structure_and_policies_only_and_imports_elsewhere_as_a_draft()
    {
        var a = world.A;
        var id = NewId();
        var created = await OkAsync(a["admin"].Client.PostAsJsonAsync(Templates, new
        {
            id, basedOn = "T02",
            names = new Dictionary<string, string> { ["en"] = "Gulf news", ["ar"] = "أخبار الخليج" },
            descriptions = new Dictionary<string, string> { ["en"] = "News pages for the Gulf companies." },
        }));
        // Lock the first region and distribute the template, so the document can be checked for what it leaves out.
        var regions = created["latest"]!["regions"]!.DeepClone().AsArray();
        regions[0]!["policy"]!["protection"] = "protected";
        var variants = created["latest"]!["variants"]!.DeepClone();
        await OkAsync(a["admin"].Client.PutAsJsonAsync($"{Templates}/{id}/draft", new { names = created["latest"]!["names"], descriptions = created["latest"]!["descriptions"], variants, regions }));
        await OkAsync(a["admin"].Client.PutAsJsonAsync($"{Templates}/{id}/distribution", new { orgUnitIds = new[] { a.Unit("Gulf") } }));

        var response = await a["admin"].Client.GetAsync($"{Templates}/{id}/export");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var document = JsonNode.Parse(text)!;
        Assert.Equal("teambeats.template", document["format"]!.GetValue<string>());
        Assert.Equal(1, document["formatVersion"]!.GetValue<int>());
        Assert.Equal("T02", document["basedOn"]!.GetValue<string>());
        Assert.Equal("protected", document["regions"]![0]!["policy"]!["protection"]!.GetValue<string>());
        // Nothing tenant-specific: no tenant, org unit or person ids, and no distribution.
        Assert.DoesNotContain(a.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(a.Unit("Gulf").ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(a["admin"].Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(document["distribution"]);
        await world.Database.AssertAuditedAsync(a.Id, AuditActions.TemplateExported, id);

        // Another tenant imports it as a new draft with its own id, distributed nowhere.
        var b = world.B;
        var newId = NewId();
        var imported = await OkAsync(b["admin"].Client.PostAsJsonAsync($"{Templates}/import", new { document, id = newId }));
        Assert.Equal(newId, imported["id"]!.GetValue<string>());
        Assert.Equal("draft", imported["versions"]![0]!["state"]!.GetValue<string>());
        Assert.Single(imported["versions"]!.AsArray());
        Assert.Empty(imported["distribution"]!.AsArray());
        Assert.Equal("protected", imported["latest"]!["regions"]![0]!["policy"]!["protection"]!.GetValue<string>());
        Assert.Equal("أخبار الخليج", imported["latest"]!["names"]!["ar"]!.GetValue<string>());
        await world.Database.AssertAuditedAsync(b.Id, AuditActions.TemplateImported, newId);

        // Importing under an id already used is refused; so is a document that is not a template or does not validate, and nothing is kept.
        Assert.Equal(HttpStatusCode.Conflict, (await b["admin"].Client.PostAsJsonAsync($"{Templates}/import", new { document, id = newId })).StatusCode);
        var wrong = document.DeepClone();
        wrong["format"] = "something.else";
        Assert.Equal("validation.template.document_format", await ErrorAsync(await b["admin"].Client.PostAsJsonAsync($"{Templates}/import", new { document = wrong, id = NewId() }), "document"));
        var badId = NewId();
        var invalid = document.DeepClone();
        invalid["variants"] = new JsonArray("not-a-variant");
        Assert.Equal("validation.template.variants_invalid", await ErrorAsync(await b["admin"].Client.PostAsJsonAsync($"{Templates}/import", new { document = invalid, id = badId }), "variants"));
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.GetAsync($"{Templates}/{badId}")).StatusCode);
        var unknownBase = document.DeepClone();
        unknownBase["basedOn"] = "T99";
        Assert.Equal("validation.page.template_unknown", await ErrorAsync(await b["admin"].Client.PostAsJsonAsync($"{Templates}/import", new { document = unknownBase, id = NewId() }), "basedOn"));

        // Another tenant cannot export it; someone who does not design templates can neither export nor import.
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.GetAsync($"{Templates}/{id}/export")).StatusCode);
        var name = "tpx_" + Guid.NewGuid().ToString("N")[..6];
        await world.SeedUserAsync(a, name, MembershipTypes.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await a[name].Client.GetAsync($"{Templates}/{id}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a[name].Client.PostAsJsonAsync($"{Templates}/import", new { document, id = NewId() })).StatusCode);
    }
}
