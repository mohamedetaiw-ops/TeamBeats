using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T017 (US8 scenarios 1 and 2, FR-016): a compliance officer asks for an evidence pack over policies they
/// manage and a date range; the background builds a PDF and a CSV with versions, approvals, attestation completion by
/// unit, attestations and linked controls; asking, sharing and downloads are audited; a ready pack is shared read-only
/// with a space until a date; packages without <c>policy.evidence</c> and other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyEvidenceTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";
    private const string Packs = "/api/v1/policies/evidence-packs";

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("pev-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "co", "legal", "ali", "auditor", "eman" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return t;
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

    private static Dictionary<string, string> Both(string text) => new() { ["en"] = text, ["ar"] = text + " ع" };

    private static async Task<Guid> PublishedAsync(TestTenant t, string code)
    {
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code, names = Both(code), route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var clause = new
        {
            level = 1, titles = Both("Use"),
            bodies = new Dictionary<string, object>
            {
                ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "Use it well." } } } } },
                ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "استخدمه جيدًا." } } } } },
            },
        };
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both(code), clauses = new[] { clause } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        return id;
    }

    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private static string DaysAgo(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-days).ToString("yyyy-MM-dd");

    private static string InDays(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    private static async Task<string> TextAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return text;
    }

    [Fact]
    public async Task Officers_build_a_pack_with_versions_approvals_completion_attestations_and_controls_and_share_it_with_a_space()
    {
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{aup}/controls", new { controls = new[] { new { framework = "iso27001", code = "5.10" } } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/campaigns", new { }), HttpStatusCode.Created);
        await world.DispatchTenantAsync(t.Id);
        await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{aup}/attest", new { locale = "en", channel = "web" }), HttpStatusCode.Created);

        // Asking is checked: policies the officer manages, a sensible range; employees cannot ask.
        Assert.Contains("validation.policies.evidence_policies", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync(Packs,
            new { policyIds = Array.Empty<Guid>(), from = DaysAgo(30), to = Today })));
        Assert.Contains("validation.policies.evidence_range", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync(Packs,
            new { policyIds = new[] { aup }, from = Today, to = DaysAgo(30) })));
        Assert.Contains("validation.policies.evidence_range", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync(Packs,
            new { policyIds = new[] { aup }, from = DaysAgo(2000), to = Today })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PostAsJsonAsync(Packs, new { policyIds = new[] { aup }, from = DaysAgo(30), to = Today })).StatusCode);

        var pack = await OkAsync(t["co"].Client.PostAsJsonAsync(Packs, new { policyIds = new[] { aup }, from = DaysAgo(30), to = InDays(1) }), HttpStatusCode.Created);
        var id = pack.GetProperty("id").GetGuid();
        Assert.Equal("queued", pack.GetProperty("status").GetString());
        Assert.False(pack.GetProperty("canShare").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyEvidenceExported, id.ToString());
        Assert.Contains("conflict.policies.evidence_not_ready", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync($"{Packs}/{id}/share",
            new { spaceId = Guid.NewGuid(), until = InDays(7) })));

        await world.DispatchTenantAsync(t.Id);
        var ready = await OkAsync(t["co"].Client.GetAsync($"{Packs}/{id}"));
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.True(ready.GetProperty("pdf").GetBoolean());
        Assert.True(ready.GetProperty("csv").GetBoolean());
        Assert.Equal("AUP", ready.GetProperty("policies")[0].GetProperty("code").GetString());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t["co"].Id), ("k", PolicyEvidence.ReadyTemplate)));

        var csv = await TextAsync(t["co"].Client, $"{Packs}/{id}/csv");
        Assert.Contains("\"Versions in force\",\"AUP\",\"1\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Approval trail\",\"AUP\",\"1\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Attestation completion by unit\",\"AUP\",\"1\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"1/", csv, StringComparison.Ordinal);
        Assert.Contains("\"Attestations\",\"AUP\",\"1\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Gulf\"", csv, StringComparison.Ordinal);
        Assert.Contains("iso27001 5.10", csv, StringComparison.Ordinal);
        var pdf = await TextAsync(t["co"].Client, $"{Packs}/{id}/pdf");
        Assert.StartsWith("%PDF", pdf, StringComparison.Ordinal);
        Assert.Contains("Policy evidence pack", pdf, StringComparison.Ordinal);
        Assert.Contains("Linked controls", pdf, StringComparison.Ordinal);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyEvidenceDownloaded, id.ToString());
        Assert.Single((await OkAsync(t["co"].Client.GetAsync(Packs))).EnumerateArray());
        Assert.Empty((await OkAsync(t["ali"].Client.GetAsync(Packs))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["ali"].Client.GetAsync($"{Packs}/{id}/csv")).StatusCode);

        // Shared with a space the officer owns, the space's readers open it until the end date; others still cannot.
        var space = (await OkAsync(t["co"].Client.PostAsJsonAsync("/api/v1/spaces", new
        {
            name = "Audit 2026", description = "External audit.", visibility = "private", template = "project", allowJoin = false,
            members = new object[] { new { userId = t["auditor"].Id, role = "viewer" } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var auditorSpace = (await OkAsync(t["auditor"].Client.PostAsJsonAsync("/api/v1/spaces", new
        {
            name = "Mine", description = "Not the officer's.", visibility = "private", template = "project", allowJoin = false, members = Array.Empty<object>(),
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Contains("validation.policies.evidence_space", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync($"{Packs}/{id}/share",
            new { spaceId = auditorSpace, until = InDays(7) })));
        Assert.Contains("validation.policies.evidence_until", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync($"{Packs}/{id}/share",
            new { spaceId = space, until = InDays(400) })));
        Assert.Equal(HttpStatusCode.NotFound, (await t["auditor"].Client.PutAsJsonAsync($"{Packs}/{id}/share", new { spaceId = space, until = InDays(7) })).StatusCode);
        var shared = await OkAsync(t["co"].Client.PutAsJsonAsync($"{Packs}/{id}/share", new { spaceId = space, until = InDays(7) }));
        Assert.Equal(space, shared.GetProperty("sharedSpace").GetProperty("id").GetGuid());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyEvidenceShared, id.ToString());

        var forAuditor = Assert.Single((await OkAsync(t["auditor"].Client.GetAsync($"{Packs}?spaceId={space}"))).EnumerateArray());
        Assert.False(forAuditor.GetProperty("canShare").GetBoolean());
        Assert.Equal(JsonValueKind.Null, forAuditor.GetProperty("sharedSpace").ValueKind);
        Assert.Contains("iso27001 5.10", await TextAsync(t["auditor"].Client, $"{Packs}/{id}/csv"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await t["eman"].Client.GetAsync($"{Packs}?spaceId={space}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["eman"].Client.GetAsync($"{Packs}/{id}/pdf")).StatusCode);

        // Once the share ends, the space's readers reach nothing; the officer still does.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update policies.evidence_pack set shared_until = now() - interval '1 minute' where id = @id", ("id", id));
        Assert.Empty((await OkAsync(t["auditor"].Client.GetAsync($"{Packs}?spaceId={space}"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["auditor"].Client.GetAsync($"{Packs}/{id}/csv")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["co"].Client.GetAsync($"{Packs}/{id}/csv")).StatusCode);

        // Stopping the share clears it.
        var stopped = await OkAsync(t["co"].Client.PutAsJsonAsync($"{Packs}/{id}/share", new { spaceId = (Guid?)null }));
        Assert.Equal(JsonValueKind.Null, stopped.GetProperty("sharedSpace").ValueKind);
    }

    [Fact]
    public async Task With_the_conversion_service_down_the_pack_has_its_csv_and_arabic_labels()
    {
        var t = await FreshAsync();
        (await t["co"].Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" })).EnsureSuccessStatusCode();
        var aup = await PublishedAsync(t, "AUP");
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Packs, new { policyIds = new[] { aup }, from = DaysAgo(10), to = Today }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        world.Factory.Gotenberg.HtmlDown = true;
        try
        {
            await world.DispatchTenantAsync(t.Id);
        }
        finally
        {
            world.Factory.Gotenberg.HtmlDown = false;
        }
        var ready = await OkAsync(t["co"].Client.GetAsync($"{Packs}/{id}"));
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.False(ready.GetProperty("pdf").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await t["co"].Client.GetAsync($"{Packs}/{id}/pdf")).StatusCode);
        var csv = await TextAsync(t["co"].Client, $"{Packs}/{id}/csv");
        Assert.Contains("الإصدارات السارية", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cells_that_a_spreadsheet_would_run_are_neutralised()
    {
        Assert.Equal("\"'=SUM(A1)\"", PolicyEvidence.Cell("=SUM(A1)"));
        Assert.Equal("\"'+1\"", PolicyEvidence.Cell("+1"));
        Assert.Equal("\"say \"\"hi\"\"\"", PolicyEvidence.Cell("say \"hi\""));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Without_policy_evidence_there_are_no_packs_and_other_tenants_reach_nothing()
    {
        var plain = await FreshAsync("workplace_plus");
        var policy = await PublishedAsync(plain, "AUP");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["co"].Client.PostAsJsonAsync(Packs, new { policyIds = new[] { policy }, from = DaysAgo(10), to = Today })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["co"].Client.GetAsync(Packs)).StatusCode);

        var t = await FreshAsync();
        var own = await PublishedAsync(t, "AUP");
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Packs, new { policyIds = new[] { own }, from = DaysAgo(10), to = Today }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await world.DispatchTenantAsync(t.Id);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.GetAsync($"{Packs}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.GetAsync($"{Packs}/{id}/csv")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.PutAsJsonAsync($"{Packs}/{id}/share", new { spaceId = (Guid?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.PostAsJsonAsync(Packs, new { policyIds = new[] { own }, from = DaysAgo(10), to = Today })).StatusCode);
        Assert.Empty((await OkAsync(other["co"].Client.GetAsync(Packs))).EnumerateArray());
    }
}
