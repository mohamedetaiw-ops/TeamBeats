using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 T004 (US1, US2, US10): an agent inherits the platform and mandatory suites read-only and owns one suite whose
/// every save is a new version; thresholds may only be stricter than the tenant's and the platform's; datasets keep every
/// version; imports are checked and wait for confirmation; and nobody outside the agent's scope or tenant can read them.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationRegistryTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ev-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "fadi", "aip", "sec", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fadi", BuiltInRoles.AgentDesigner, "org_subtree", finance);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    private static async Task<Guid> InstallAsync(TestTenant t, string template = "hr_assistant", string user = "sara", string unit = "Hr") =>
        (await OkAsync(t[user].Client.PostAsJsonAsync("/api/v1/agents", new { templateId = template, orgUnitId = t.Unit(unit) }), HttpStatusCode.Created))
        .GetProperty("agent").GetProperty("id").GetGuid();

    private static object Metric(string key, double? threshold = null, string? severity = null, double weight = 1) => new { key, weight, threshold, severity };

    private static object Case(string input, string locale = "en", object? expect = null, Guid? personaId = null) =>
        new { input, locale, personaId, expect = expect ?? new { keyFacts = new[] { "25 days" } }, tags = new[] { "leave" } };

    private static async Task<Guid> DatasetAsync(TestTenant t, Guid agent, string user = "sara") =>
        (await OkAsync(t[user].Client.PostAsJsonAsync($"{Eval}/datasets", new { agentId = agent, names = new { en = "Acme leave rules", ar = "قواعد إجازات أكمي" } }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

    [Fact]
    public async Task An_agent_inherits_read_only_suites_and_each_save_of_its_own_suite_is_a_new_version()
    {
        var t = await FreshAsync();
        var agent = await InstallAsync(t);

        var view = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/agents/{agent}/suites"));
        var inherited = view.GetProperty("inherited").EnumerateArray().Select(s => s.GetProperty("key").GetString()).ToList();
        Assert.Equal(6, inherited.Count);
        Assert.Contains("template.hr_assistant", inherited);
        Assert.Equal(5, view.GetProperty("inherited").EnumerateArray().Count(s => s.GetProperty("mandatory").GetBoolean()));
        Assert.Equal(0, view.GetProperty("suite").GetProperty("version").GetInt32());
        Assert.True(view.GetProperty("canEdit").GetBoolean());
        var platform = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/platform/suites/template.hr_assistant"));
        Assert.Contains(platform.GetProperty("cases").EnumerateArray(), c => c.GetProperty("locale").GetString() == "ar");

        // The rule for each metric shows where its threshold and severity come from.
        var rule = view.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "answer_correctness");
        Assert.Equal(0.85, rule.GetProperty("threshold").GetDouble());
        Assert.Equal("platform", rule.GetProperty("thresholdLevel").GetString());

        var dataset = await DatasetAsync(t, agent);
        var saved = await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{agent}/suite",
            new { version = 0, metrics = new[] { Metric("answer_correctness", 0.9), Metric("refusal_correctness") }, datasets = new[] { dataset } }));
        Assert.Equal(1, saved.GetProperty("suite").GetProperty("version").GetInt32());
        Assert.Equal("agent", saved.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "answer_correctness").GetProperty("thresholdLevel").GetString());
        Assert.Equal("conflict.eval.suite_version", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{agent}/suite",
            new { version = 0, metrics = new[] { Metric("answer_correctness") } }), HttpStatusCode.Conflict));
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{agent}/suite", new { version = 1, metrics = new[] { Metric("answer_correctness", 0.95) } }));
        var first = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/agents/{agent}/suite/versions/1"));
        Assert.Equal(0.9, first.GetProperty("metrics")[0].GetProperty("threshold").GetDouble());
        Assert.Equal(dataset, first.GetProperty("datasets")[0].GetGuid());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalSuiteCreated));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalSuiteChanged));

        // Another agent's dataset cannot be part of this suite.
        var other = await InstallAsync(t, "enterprise_knowledge");
        var foreign = await DatasetAsync(t, other);
        Assert.Equal("validation.eval.suite_datasets", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{agent}/suite",
            new { version = 2, metrics = new[] { Metric("answer_correctness") }, datasets = new[] { foreign } }), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Thresholds_are_only_stricter_down_the_levels_and_tenants_cannot_weaken_the_platform()
    {
        var t = await FreshAsync();
        var agent = await InstallAsync(t);
        var suite = $"{Eval}/agents/{agent}/suite";
        Assert.Equal("validation.eval.threshold_below_platform",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync(suite, new { version = 0, metrics = new[] { Metric("answer_correctness", 0.8) } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.eval.severity_below_platform",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync(suite, new { version = 0, metrics = new[] { Metric("forbidden_source_rate", severity: "warning") } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.eval.judged_critical",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync(suite, new { version = 0, metrics = new[] { Metric("groundedness", severity: "critical") } }), HttpStatusCode.BadRequest));

        // The tenant raises groundedness to 0.95; a designer's 0.92 is refused naming the tenant level.
        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/settings", new { version = 0, thresholds = new[] { Metric("groundedness", 0.95) } }),
            HttpStatusCode.Forbidden));
        Assert.Equal("validation.eval.threshold_below_platform", await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/settings",
            new { version = 0, thresholds = new[] { Metric("groundedness", 0.5) } }), HttpStatusCode.BadRequest));
        var settings = await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/settings", new { version = 0, thresholds = new[] { Metric("groundedness", 0.95) } }));
        Assert.Equal(1, settings.GetProperty("version").GetInt32());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalPolicyChanged));
        Assert.Equal(1, (await OkAsync(t["sec"].Client.GetAsync($"{Eval}/settings"))).GetProperty("version").GetInt32());
        Assert.Equal("validation.eval.threshold_below_tenant",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync(suite, new { version = 0, metrics = new[] { Metric("groundedness", 0.92) } }), HttpStatusCode.BadRequest));
        var saved = await OkAsync(t["sara"].Client.PutAsJsonAsync(suite, new { version = 0, metrics = new[] { Metric("groundedness", 0.97) } }));
        Assert.Equal("agent", saved.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "groundedness").GetProperty("thresholdLevel").GetString());

        // Custom weights are an advanced feature; Workplace tenants still add their own cases and suites.
        var basic = await FreshAsync("workplace");
        var basicAgent = await InstallAsync(basic);
        Assert.Equal("validation.eval.weights_advanced", await ProblemAsync(basic["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{basicAgent}/suite",
            new { version = 0, metrics = new[] { Metric("answer_correctness", weight: 2) } }), HttpStatusCode.BadRequest));
        var dataset = await DatasetAsync(basic, basicAgent);
        await OkAsync(basic["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new { version = 0, cases = new[] { Case("How many leave days do I get?") } }));
        await OkAsync(basic["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{basicAgent}/suite", new { version = 0, metrics = new[] { Metric("answer_correctness") }, datasets = new[] { dataset } }));
        await OkAsync(t["sara"].Client.PutAsJsonAsync(suite, new { version = 1, metrics = new[] { Metric("answer_correctness", weight: 2) } }));
    }

    [Fact]
    public async Task Dataset_versions_are_kept_and_imports_wait_for_confirmation()
    {
        var t = await FreshAsync();
        var agent = await InstallAsync(t);
        var dataset = await DatasetAsync(t, agent);
        var cases = $"{Eval}/datasets/{dataset}/cases";

        var v1 = await OkAsync(t["sara"].Client.PutAsJsonAsync(cases, new { version = 0, cases = new[] { Case("How many leave days do I get?"), Case("كم يوما من الإجازة أستحق؟", "ar") } }));
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
        Assert.Equal(2, v1.GetProperty("cases").GetArrayLength());
        Assert.Equal("validation.eval.case_locale", await ProblemAsync(t["sara"].Client.PutAsJsonAsync(cases, new { version = 1, cases = new[] { Case("Combien?", "fr") } }), HttpStatusCode.BadRequest));
        Assert.Equal("conflict.eval.dataset_version", await ProblemAsync(t["sara"].Client.PutAsJsonAsync(cases, new { version = 0, cases = new[] { Case("x") } }), HttpStatusCode.Conflict));
        await OkAsync(t["sara"].Client.PutAsJsonAsync(cases, new { version = 1, cases = new[] { Case("Can I carry leave over?", expect: new { refusal = false, escalation = true }) } }));
        var kept = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/datasets/{dataset}?version=1"));
        Assert.Equal(2, kept.GetProperty("cases").GetArrayLength());
        Assert.Equal(2, kept.GetProperty("versions").GetArrayLength());
        Assert.Equal(2, await AuditCountAsync(t, AuditActions.EvalDatasetVersionCreated));

        // CSV import: invalid rows are listed with reasons and nothing changes until the person confirms.
        var csv = "input,locale,key_facts,refusal,tags\n" +
                  "What is the notice period?,en,30 days,no,notice\n" +
                  "Quelle est la période?,fr,30 jours,no,notice\n" +
                  "ما مدة الإشعار؟,ar,30 يوما,no,notice\n";
        var preview = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets/{dataset}/import", new { format = "csv", content = csv }));
        Assert.Equal(2, preview.GetProperty("valid").GetInt32());
        var error = preview.GetProperty("errors").EnumerateArray().Single();
        Assert.Equal(2, error.GetProperty("row").GetInt32());
        Assert.Equal("validation.eval.case_locale", error.GetProperty("key").GetString());
        Assert.Equal(2, (await OkAsync(t["sara"].Client.GetAsync($"{Eval}/datasets/{dataset}"))).GetProperty("version").GetInt32());
        var importId = preview.GetProperty("importId").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["fadi"].Client.PostAsync($"{Eval}/datasets/{dataset}/imports/{importId}/confirm", null)).StatusCode);
        var confirmed = await OkAsync(t["sara"].Client.PostAsync($"{Eval}/datasets/{dataset}/imports/{importId}/confirm", null));
        Assert.Equal(3, confirmed.GetProperty("version").GetInt32());
        Assert.Equal(3, confirmed.GetProperty("cases").GetArrayLength());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalDatasetImported));
        Assert.Equal(HttpStatusCode.NotFound, (await t["sara"].Client.PostAsync($"{Eval}/datasets/{dataset}/imports/{importId}/confirm", null)).StatusCode);

        // A JSON import checked before a newer save is stale.
        var json = JsonSerializer.Serialize(new[] { Case("Do I get Eid leave?") });
        var stale = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets/{dataset}/import", new { format = "json", content = json }));
        await OkAsync(t["sara"].Client.PutAsJsonAsync(cases, new { version = 3, cases = new[] { Case("Only this one") } }));
        Assert.Equal("conflict.eval.import_stale", await ProblemAsync(t["sara"].Client.PostAsync($"{Eval}/datasets/{dataset}/imports/{stale.GetProperty("importId").GetGuid()}/confirm", null),
            HttpStatusCode.Conflict));
        Assert.Equal("validation.eval.import_format", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets/{dataset}/import", new { format = "xlsx", content = "x" }),
            HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Personas_are_test_identities_inside_the_designers_scope_that_never_sign_in_or_show_up()
    {
        var t = await FreshAsync();
        var personas = $"{Eval}/personas";
        var persona = await OkAsync(t["sara"].Client.PostAsJsonAsync(personas,
            new { names = new { en = "HR employee", ar = "موظف موارد بشرية" }, orgUnitId = t.Unit("Hr"), roles = new[] { BuiltInRoles.Approver }, membershipType = "employee" }),
            HttpStatusCode.Created);
        var personaId = persona.GetProperty("id").GetGuid();
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalPersonaCreated));
        Assert.Equal("validation.eval.persona_role_privileged", await ProblemAsync(t["sara"].Client.PostAsJsonAsync(personas,
            new { names = new { en = "Admin" }, orgUnitId = t.Unit("Hr"), roles = new[] { BuiltInRoles.OrganizationAdministrator }, membershipType = "employee" }), HttpStatusCode.BadRequest));
        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.PostAsJsonAsync(personas,
            new { names = new { en = "Finance employee" }, orgUnitId = t.Unit("Finance"), roles = Array.Empty<string>(), membershipType = "employee" }), HttpStatusCode.Forbidden));
        Assert.Single((await OkAsync(t["sara"].Client.GetAsync(personas))).EnumerateArray());
        Assert.Empty((await OkAsync(t["fadi"].Client.GetAsync(personas))).EnumerateArray());

        // The identity has the persona's unit and role, and nothing that lets anyone see or use it as a person.
        var user = (await world.Database.TenantScalarAsync<Guid>(t.Id, "select user_id from evaluation.persona where id = @id", ("id", personaId)));
        Assert.True(await world.Database.TenantScalarAsync<bool>(t.Id, "select test_identity from identity.user_account where id = @u", ("u", user)));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from tenancy.org_membership where user_id = @u and org_unit_id = @unit",
            ("u", user), ("unit", t.Unit("Hr"))));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from identity.external_identity where user_id = @u", ("u", user)));
        var listed = await OkAsync(t["admin"].Client.GetAsync("/api/v1/admin/users"));
        Assert.DoesNotContain(user.ToString(), listed.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.GetAsync($"/api/v1/admin/users/{user}")).StatusCode);
        Assert.Equal("validation.role.test_identity", await ProblemAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "user", subjectId = user, roleKey = BuiltInRoles.TenantAdministrator, scopeType = "tenant" }), HttpStatusCode.BadRequest));

        // Cases can name the persona; an unknown persona is refused.
        var agent = await InstallAsync(t);
        var dataset = await DatasetAsync(t, agent);
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new { version = 0, cases = new[] { Case("What tasks wait for me?", personaId: personaId) } }));
        Assert.Equal("validation.eval.case_persona", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases",
            new { version = 1, cases = new[] { Case("x", personaId: Guid.NewGuid()) } }), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Suites_and_datasets_stay_inside_the_agents_scope_and_tenant()
    {
        var t = await FreshAsync();
        var agent = await InstallAsync(t);
        var dataset = await DatasetAsync(t, agent);

        // A designer of a sibling unit sees nothing; a security officer reads but cannot change.
        Assert.Equal(HttpStatusCode.NotFound, (await t["fadi"].Client.GetAsync($"{Eval}/agents/{agent}/suites")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["fadi"].Client.GetAsync($"{Eval}/datasets/{dataset}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["fadi"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new { version = 0, cases = new[] { Case("x") } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"{Eval}/agents/{agent}/suites")).StatusCode);
        var read = await OkAsync(t["sec"].Client.GetAsync($"{Eval}/agents/{agent}/suites"));
        Assert.False(read.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sec"].Client.PutAsJsonAsync($"{Eval}/agents/{agent}/suite", new { version = 0, metrics = new[] { Metric("answer_correctness") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sec"].Client.PostAsJsonAsync($"{Eval}/datasets", new { agentId = agent, names = new { en = "x" } })).StatusCode);
        Assert.Single((await OkAsync(t["sec"].Client.GetAsync($"{Eval}/datasets?agentId={agent}"))).EnumerateArray());

        // Another tenant cannot find the agent's suites, the dataset or the persona list's contents.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["sara"].Client.GetAsync($"{Eval}/agents/{agent}/suites")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Eval}/datasets/{dataset}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{Eval}/datasets/{dataset}/import", new { format = "csv", content = "input,locale\nx,en" })).StatusCode);
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(other.Id, "select count(*) from evaluation.dataset"));
    }
}
