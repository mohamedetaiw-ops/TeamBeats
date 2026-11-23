using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T023 (FR-003): a policy kept as a spec 011 controlled document. The draft's clauses come from the released
/// version's text, one clause a paragraph, in the one language of that version; the version shows the document number,
/// its version and the reader page; a newer release refuses submit until the draft is saved again; a document that is not
/// released, not controlled, unreadable or another tenant's is refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyDocumentTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Docs = "/api/v1/documents";

    private sealed record Org(TestTenant T, Guid Library, Guid Type, Guid Plain);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pdc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var name in new[] { "lm", "hs", "pm", "qa", "co", "legal", "cai" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, name == "cai" ? "Cairo" : "Dubai");
        }
        await world.AssignAsync(t, "lm", BuiltInRoles.OrganizationAdministrator, "org_subtree", dubai);
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        Assert.Equal(HttpStatusCode.Created, (await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "doc_contributor", names = new Dictionary<string, string> { ["en"] = "Contributor" },
            permissions = new[] { Permissions.DocView, Permissions.DocContribute }, delegable = true,
        })).StatusCode);
        await world.AssignAsync(t, "hs", "doc_contributor", "org_unit", dubai);
        var library = Id(await OkAsync(t["lm"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Policies", ["ar"] = "السياسات" }, scope = "org_unit", scopeId = dubai,
        }), HttpStatusCode.Created));
        var type = Id(await OkAsync(t["lm"].Client.PostAsJsonAsync($"{Libraries}/{library}/types", new
        {
            names = new Dictionary<string, string> { ["en"] = "Policy", ["ar"] = "سياسة" },
            fields = Array.Empty<object>(),
            controlled = new
            {
                numberPrefix = "pol-", numberDigits = 3, readConfirmation = false, reviewMonths = 12,
                steps = new[] { new { approvers = new[] { t["pm"].Id } }, new { approvers = new[] { t["qa"].Id } } },
            },
        }), HttpStatusCode.Created));
        var plain = Id(await OkAsync(t["lm"].Client.PostAsJsonAsync($"{Libraries}/{library}/types", new
        {
            names = new Dictionary<string, string> { ["en"] = "Note", ["ar"] = "ملاحظة" }, fields = Array.Empty<object>(),
        }), HttpStatusCode.Created));
        return new Org(t, library, type, plain);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetProperty("key").GetString()!)).ToArray();
        }
        return body.TryGetProperty("detailKey", out var detail) ? [detail.GetString()!] : [body.GetProperty("code").GetString()!];
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static ByteArrayContent Body(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<JsonElement[]> TasksAsync(HttpClient client, string source) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(i => i.GetProperty("source").GetString() == source).ToArray();

    private static async Task DecideAsync(HttpClient client, string source)
    {
        var task = Assert.Single(await TasksAsync(client, source)).GetProperty("taskId").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome = "approve" }));
    }

    private static async Task<Guid> UploadAsync(Org o, string text, bool controlled = true) =>
        Id(await OkAsync(o.T["hs"].Client.PostAsync(
            $"{Libraries}/{o.Library}/files?name=conduct.txt&type={(controlled ? o.Type : o.Plain)}&publish={(controlled ? "false" : "true")}", Body(text)), HttpStatusCode.Created));

    private static async Task ReleaseAsync(Org o, Guid document)
    {
        await OkAsync(o.T["hs"].Client.PostAsJsonAsync($"{Docs}/{document}/release", new { }));
        await DecideAsync(o.T["pm"].Client, "document_release");
        await DecideAsync(o.T["qa"].Client, "document_release");
    }

    private static async Task<Guid> PolicyAsync(TestTenant t, string code) =>
        Id(await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code, names = new Dictionary<string, string> { ["en"] = code, ["ar"] = code + " ع" },
            route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));

    private static readonly Dictionary<string, string> English = new() { ["en"] = "Code of conduct" };

    [Fact]
    public async Task A_released_document_becomes_the_draft_and_a_newer_release_needs_the_draft_saved_again()
    {
        var o = await FreshAsync();
        var t = o.T;
        var doc = await UploadAsync(o, "Treat people well\nRespect everyone at work.\n\nDeclare gifts\nDeclare any gift over the limit.");
        await ReleaseAsync(o, doc);
        var policy = await PolicyAsync(t, "COC");

        var draft = await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new { titles = English, documentId = doc }));
        Assert.Equal(["en"], draft.GetProperty("locales").EnumerateArray().Select(l => l.GetString()!).ToArray());
        var clauses = draft.GetProperty("clauses").EnumerateArray().ToArray();
        Assert.Equal(2, clauses.Length);
        Assert.Equal("Treat people well", clauses[0].GetProperty("titles").GetProperty("en").GetString());
        Assert.Equal("Declare gifts", clauses[1].GetProperty("titles").GetProperty("en").GetString());
        Assert.Contains("Declare any gift over the limit.", clauses[1].GetProperty("bodies").GetProperty("en").ToString());
        var source = draft.GetProperty("document");
        Assert.Equal(doc, source.GetProperty("documentId").GetGuid());
        Assert.Equal("POL-001", source.GetProperty("number").GetString());
        Assert.Equal("1.0", source.GetProperty("label").GetString());
        Assert.Equal($"/documents/{doc}", source.GetProperty("link").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyDraftSaved, policy.ToString());

        // The document is revised and released again before the policy goes for approval: submit refuses the stale copy.
        await OkAsync(t["hs"].Client.PostAsync($"{Docs}/{doc}/checkout", null));
        await OkAsync(t["hs"].Client.PostAsync($"{Docs}/{doc}/checkin?name=conduct.txt",
            Body("Treat people well\nRespect everyone at work.\n\nDeclare gifts\nDeclare every gift.\n\nSpeak up\nReport concerns.")));
        await ReleaseAsync(o, doc);
        Assert.Contains("conflict.policies.document_changed",
            await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { })));

        var again = await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new { titles = English, documentId = doc }));
        Assert.Equal(3, again.GetProperty("clauses").GetArrayLength());
        Assert.Equal("2.0", again.GetProperty("document").GetProperty("label").GetString());
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { }));
        await DecideAsync(t["legal"].Client, "policy_approval");

        // Readers get the policy with the controlled document it is kept as.
        var current = await OkAsync(t["cai"].Client.GetAsync($"{Policies}/{policy}/current"));
        Assert.Equal("POL-001", current.GetProperty("version").GetProperty("document").GetProperty("number").GetString());
        Assert.Equal(3, current.GetProperty("version").GetProperty("clauses").GetArrayLength());
    }

    [Fact]
    public async Task Clauses_two_languages_unreleased_uncontrolled_unreadable_and_other_tenants_documents_are_refused()
    {
        var o = await FreshAsync();
        var t = o.T;
        var doc = await UploadAsync(o, "Treat people well\nRespect everyone at work.");
        var policy = await PolicyAsync(t, "COC");
        Task<HttpResponseMessage> Save(object body) => t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", body);

        // Not released yet: there is no version in force to take the text from.
        Assert.Contains("validation.policies.document", await ErrorKeysAsync(await Save(new { titles = English, documentId = doc })));
        await ReleaseAsync(o, doc);

        var clause = new
        {
            level = 1, titles = English,
            bodies = new Dictionary<string, object> { ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "x" } } } } } },
        };
        Assert.Contains("validation.policies.document_clauses", await ErrorKeysAsync(await Save(new { titles = English, clauses = new[] { clause }, documentId = doc })));
        Assert.Contains("validation.policies.document_locale",
            await ErrorKeysAsync(await Save(new { titles = new Dictionary<string, string> { ["en"] = "Code", ["ar"] = "مدونة" }, documentId = doc })));

        // A document that is not controlled, or one the author cannot read, or a made-up id.
        var plain = await UploadAsync(o, "Just a note.", controlled: false);
        Assert.Contains("validation.policies.document", await ErrorKeysAsync(await Save(new { titles = English, documentId = plain })));
        Assert.Contains("validation.policies.document", await ErrorKeysAsync(await Save(new { titles = English, documentId = Guid.NewGuid() })));
        var other = await FreshAsync();
        var theirs = await UploadAsync(other, "Their rules\nNot yours.");
        await ReleaseAsync(other, theirs);
        Assert.Contains("validation.policies.document", await ErrorKeysAsync(await Save(new { titles = English, documentId = theirs })));

        // Nothing was saved by the refusals.
        Assert.Equal(HttpStatusCode.NotFound, (await t["co"].Client.GetAsync($"{Policies}/{policy}/draft")).StatusCode);
    }
}
