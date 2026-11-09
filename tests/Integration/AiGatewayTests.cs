using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 013 T005, T006 and T009 (US1, US2, US3, FR-017, FR-019): the catalogue and model policy, defaults per use case,
/// the allowlist, the read-only platform default set, route changes held for the release gate, classification routing,
/// retries and fallback, output checks, streaming, embeddings and judge calls, and tenant isolation of the policy. The
/// fake OpenRouter counts what reaches the provider, so a denial is shown to send nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AiGatewayTests(TenantWorld world)
{
    private const string Small = "openai/gpt-4o-mini";
    private const string Large = "openai/gpt-4o";
    private const string Embedding = "openai/text-embedding-3-small";
    private const string Private = "fake/teambeats-large";

    private FakeOpenRouter Provider => world.Factory.OpenRouter;

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ai-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "layla", MembershipTypes.Employee);
        await world.AssignAsync(t, "layla", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SeedUserAsync(t, "sec", MembershipTypes.Employee);
        await world.AssignAsync(t, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<T> AsAsync<T>(TestTenant t, Func<IAiGateway, Task<T>> call) =>
        world.Factory.AsUserAsync(t.Id, t["admin"].Id, sp => call(sp.GetRequiredService<IAiGateway>()));

    private Task<AiResult> ChatAsync(TestTenant t, string text, DataClassification? classification = DataClassification.Internal, string useCase = AiUseCases.Assistant,
        string? model = null, IReadOnlyList<AiTool>? tools = null, JsonElement? schema = null, string? underTest = null) =>
        AsAsync(t, g => g.ChatAsync(new AiRequest(useCase, [new AiMessage(AiRoles.User, text)], classification, tools, schema, model, ModelUnderTest: underTest)));

    private static object Policy(string[] allowlist, Dictionary<string, string[]> routes, string[]? confidential = null) =>
        new { allowlist, routes, confidential = confidential ?? [] };

    private static Dictionary<string, string[]> Routes(string[] chat, string[]? assistant = null) =>
        AiUseCases.All.ToDictionary(u => u, u => u == AiUseCases.Embed ? new[] { Embedding } : u == AiUseCases.Assistant && assistant is not null ? assistant : chat);

    /// <summary>Saves a policy that leaves the assistant and agent routes as the platform has them, so it applies at once.</summary>
    private async Task<JsonElement> SaveAsync(TestTenant t, object policy, HttpStatusCode expected = HttpStatusCode.OK, string user = "layla")
    {
        var client = t[user].Client;
        var current = await client.GetAsync("/api/v1/ai/policy");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/ai/policy") { Content = JsonContent.Create(policy) };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
        return await OkAsync(client.SendAsync(request), expected);
    }

    private async Task MakePrivateConfidentialAsync() => await OkAsync(world.Platform.PutAsJsonAsync("/api/v1/platform/ai/catalogue", new
    {
        model = Private, provider = "openrouter", capabilities = new[] { "chat", "tools", "structured_output" }, contextTokens = 32000, regions = new[] { "eu" },
        zeroRetention = true, noTraining = true, maxClassification = "confidential", inputPrice = 1.0, outputPrice = 2.0,
    }));

    [Fact]
    public async Task Policy_administrator_sees_the_catalogue_and_others_are_refused()
    {
        var t = await FreshAsync();
        var catalogue = await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/catalogue"));
        var small = catalogue.EnumerateArray().Single(m => m.GetProperty("model").GetString() == Small);
        Assert.Equal("openrouter", small.GetProperty("provider").GetString());
        Assert.Equal("internal", small.GetProperty("maxClassification").GetString());
        Assert.Equal(0.15m, small.GetProperty("inputPrice").GetDecimal());
        Assert.False(small.GetProperty("zeroRetention").GetBoolean());

        await OkAsync(t["sec"].Client.GetAsync("/api/v1/ai/policy"));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync("/api/v1/ai/catalogue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync("/api/v1/ai/policy")).StatusCode);
        // The security officer views but does not change the policy.
        var view = await OkAsync(t["sec"].Client.GetAsync("/api/v1/ai/policy"));
        Assert.False(view.GetProperty("editable").GetBoolean());
        Assert.Equal("ai.policy.read_only_permission", view.GetProperty("readOnlyReason").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sec"].Client.PutAsJsonAsync("/api/v1/ai/policy", Policy([Small], Routes([Small])))).StatusCode);
    }

    [Fact]
    public async Task Without_model_choice_the_platform_default_set_applies_and_cannot_be_changed()
    {
        var t = await FreshAsync("workplace");
        var view = await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/policy"));
        Assert.Equal("platform", view.GetProperty("source").GetString());
        Assert.False(view.GetProperty("editable").GetBoolean());
        Assert.Equal("ai.policy.read_only_package", view.GetProperty("readOnlyReason").GetString());
        Assert.Equal(Small, view.GetProperty("effectiveRoutes").GetProperty("assistant")[0].GetString());

        var refused = await t["layla"].Client.PutAsJsonAsync("/api/v1/ai/policy", Policy([Small], Routes([Small])));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("not_in_package", await refused.Content.ReadAsStringAsync());

        var result = await ChatAsync(t, "What is our leave policy?");
        Assert.True(result.IsOk, result.Denial);
        Assert.Equal(Small, result.Completion!.Model);
    }

    [Fact]
    public async Task Defaults_per_use_case_serve_requests_and_a_model_outside_the_allowlist_sends_nothing()
    {
        var t = await FreshAsync();
        // Summaries get the larger model; the assistant and agents keep the platform route, so the change applies at once.
        var routes = Routes([Large, Small], assistant: [Small, Large]);
        routes[AiUseCases.Agent] = [Small, Large];
        var saved = await SaveAsync(t, Policy([Small, Large, Embedding], routes));
        Assert.True(saved.GetProperty("applied").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AiPolicyChanged);
        Assert.Equal(1, await world.Database.OutboxCountAsync("AiPolicyChanged", t.Id));

        var summary = await ChatAsync(t, "Summarize this memo.", useCase: AiUseCases.Summarize);
        Assert.Equal(Large, summary.Completion!.Model);
        Assert.False(summary.Completion.FallbackUsed);
        Assert.Equal(Large, Provider.LastModel);
        var assistant = await ChatAsync(t, "Hello");
        Assert.Equal(Small, assistant.Completion!.Model);
        Assert.Contains("You asked: Hello", assistant.Completion.Content);

        var before = Provider.Requests;
        var outside = await ChatAsync(t, "Hello", model: Private);
        Assert.Equal(AiDenials.ModelNotAllowed, outside.Denial);
        var unknown = await ChatAsync(t, "Hello", model: "someone/unknown-model");
        Assert.Equal(AiDenials.ModelNotAllowed, unknown.Denial);
        Assert.Equal(before, Provider.Requests);

        // A stale ETag is refused.
        using var stale = new HttpRequestMessage(HttpMethod.Put, "/api/v1/ai/policy") { Content = JsonContent.Create(Policy([Small], routes)) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"v0\"");
        Assert.Equal(HttpStatusCode.Conflict, (await t["layla"].Client.SendAsync(stale)).StatusCode);

        // Validation: a route model must be allowed; logging stays metadata only.
        await SaveAsync(t, Policy([Small], Routes([Large])), HttpStatusCode.BadRequest);
        await SaveAsync(t, new { logging = "full" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_change_to_the_assistant_route_is_held_for_the_gate_and_applies_with_an_audited_override_once_the_gate_cannot_test_it()
    {
        var t = await FreshAsync();
        var held = await SaveAsync(t, Policy([Small, Large, Embedding], Routes([Small, Large], assistant: [Large])), HttpStatusCode.Accepted);
        Assert.False(held.GetProperty("applied").GetBoolean());
        var change = held.GetProperty("routeChange");
        Assert.Equal("proposed", change.GetProperty("status").GetString());
        Assert.True(change.GetProperty("gateAvailable").GetBoolean());
        Assert.Equal("running", change.GetProperty("gateStatus").GetString());
        Assert.False(change.GetProperty("canOverride").GetBoolean());
        Assert.Contains("assistant", change.GetProperty("useCases").EnumerateArray().Select(u => u.GetString()));
        Assert.Equal(Large, change.GetProperty("after").GetProperty("assistant")[0].GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AiRouteChangeProposed);
        Assert.Equal(1, await world.Database.OutboxCountAsync("AiModelRouteChangeProposed", t.Id));

        // The current policy stays in force until the gate or an override.
        Assert.Equal(Small, (await ChatAsync(t, "Hello")).Completion!.Model);
        var id = change.GetProperty("id").GetGuid();
        // While the gate runs nobody can apply it.
        Assert.Equal(HttpStatusCode.Conflict,
            (await t["layla"].Client.PostAsJsonAsync($"/api/v1/ai/route-changes/{id}/override", new { reason = "Applying before the gate decides." })).StatusCode);

        // The proposer belongs to no unit, so the assistant has no test identity to run as: the gate reports it incomplete and lists why (spec 034 US5 scenario 4).
        await RouteGateFlow.StartAsync(world, t.Id, id);
        await RouteGateFlow.DecideAsync(world, t.Id, id);
        var pending = (await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/policy"))).GetProperty("pending");
        Assert.Equal("incomplete", pending.GetProperty("gateStatus").GetString());
        Assert.True(pending.GetProperty("canOverride").GetBoolean());
        Assert.Contains(RouteGates.AssistantNotTested, pending.GetProperty("failures").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await t["layla"].Client.PostAsJsonAsync($"/api/v1/ai/route-changes/{id}/override", new { reason = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t["sec"].Client.PostAsJsonAsync($"/api/v1/ai/route-changes/{id}/override", new { reason = "The gate is not built yet." })).StatusCode);
        var applied = await OkAsync(t["layla"].Client.PostAsJsonAsync($"/api/v1/ai/route-changes/{id}/override", new { reason = "The gate is not built yet; reviewed by hand." }));
        Assert.True(applied.GetProperty("applied").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AiRouteChangeOverridden, id.ToString());
        Assert.Equal(Large, (await ChatAsync(t, "Hello")).Completion!.Model);

        var list = await OkAsync(t["sec"].Client.GetAsync("/api/v1/ai/route-changes"));
        Assert.Equal("overridden", list[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict,
            (await t["layla"].Client.PostAsJsonAsync($"/api/v1/ai/route-changes/{id}/override", new { reason = "The gate is not built yet." })).StatusCode);
    }

    [Fact]
    public async Task Classification_routing_keeps_Confidential_and_Restricted_away_from_shared_models_by_default()
    {
        // SC-001: under the default policy nothing Confidential or Restricted reaches the OpenRouter adapter.
        var t = await FreshAsync();
        var before = Provider.Requests;
        Assert.Equal(AiDenials.ClassificationBlocked, (await ChatAsync(t, "Salary review", DataClassification.Confidential)).Denial);
        Assert.Equal(AiDenials.ClassificationBlocked, (await ChatAsync(t, "Board minutes", DataClassification.Restricted)).Denial);
        Assert.Equal(AiDenials.ClassificationBlocked, (await AsAsync(t, g => g.EmbedAsync(new EmbeddingRequest(["Board minutes"], DataClassification.Restricted)))).Denial);
        Assert.Equal(AiDenials.ClassificationMissing, (await ChatAsync(t, "Hello", classification: null)).Denial);
        Assert.Equal(before, Provider.Requests);

        // Confidential only for a zero retention, no training model the tenant allowed for it.
        await MakePrivateConfidentialAsync();
        await SaveAsync(t, Policy([Small, Large, Embedding, Private], Routes([Small, Large, Private]), confidential: [Small]), HttpStatusCode.BadRequest);
        var routes = Routes([Small, Large, Private]);
        routes[AiUseCases.Assistant] = [Small, Large];
        routes[AiUseCases.Agent] = [Small, Large];
        routes[AiUseCases.Summarize] = [Small, Private];
        await SaveAsync(t, Policy([Small, Large, Embedding, Private], routes, confidential: [Private]));
        var confidential = await ChatAsync(t, "Summarize the salary review", DataClassification.Confidential, AiUseCases.Summarize);
        Assert.True(confidential.IsOk, confidential.Denial);
        Assert.Equal(Private, confidential.Completion!.Model);
        Assert.Equal(Private, Provider.LastModel);
        // The assistant route has no Confidential model; Restricted stays blocked whatever the policy says.
        Assert.Equal(AiDenials.ClassificationBlocked, (await ChatAsync(t, "Salary review", DataClassification.Confidential)).Denial);
        Assert.Equal(AiDenials.ClassificationBlocked, (await ChatAsync(t, "Board minutes", DataClassification.Restricted, AiUseCases.Summarize)).Denial);

        // A region rule the models do not meet blocks the call before anything is sent.
        await SaveAsync(t, new { regions = new[] { "me-central" } });
        before = Provider.Requests;
        Assert.Equal(AiDenials.RegionBlocked, (await ChatAsync(t, "Hello")).Denial);
        Assert.Equal(before, Provider.Requests);
    }

    [Fact]
    public async Task A_failing_model_is_retried_once_then_the_next_eligible_model_answers()
    {
        var t = await FreshAsync();
        world.Factory.Services.GetRequiredService<ProviderCircuits>().Reset();
        var before = Provider.Requests;
        var result = await ChatAsync(t, $"Hello [fake:fail:{Small}]");
        Assert.True(result.IsOk, result.Denial);
        Assert.Equal(Large, result.Completion!.Model);
        Assert.True(result.Completion.FallbackUsed);
        Assert.Equal(before + 3, Provider.Requests);

        // All eligible models fail: the caller gets provider_unavailable.
        before = Provider.Requests;
        Assert.Equal(AiDenials.ProviderUnavailable, (await ChatAsync(t, "Hello [fake:500]")).Denial);
        Assert.Equal(before + 4, Provider.Requests);

        // A fallback never goes to a model with weaker policy: the Confidential route skips the shared model.
        await MakePrivateConfidentialAsync();
        var routes = Routes([Small, Large]);
        routes[AiUseCases.Summarize] = [Private, Small];
        await SaveAsync(t, Policy([Small, Large, Embedding, Private], routes, confidential: [Private]));
        before = Provider.Requests;
        Assert.Equal(AiDenials.ProviderUnavailable, (await ChatAsync(t, $"Salaries [fake:fail:{Private}]", DataClassification.Confidential, AiUseCases.Summarize)).Denial);
        Assert.Equal(before + 2, Provider.Requests);
        Assert.Equal(Private, Provider.LastModel);
        world.Factory.Services.GetRequiredService<ProviderCircuits>().Reset();
    }

    [Fact]
    public async Task Output_checks_streaming_embeddings_and_judge_calls()
    {
        var t = await FreshAsync();
        world.Factory.Services.GetRequiredService<ProviderCircuits>().Reset();

        // Tool calls for tools that were not offered are dropped.
        var lookup = new AiTool("lookup_policy", "Find a policy", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement);
        var dropped = await ChatAsync(t, "Delete everything [fake:tool:delete_all]", tools: [lookup]);
        Assert.Empty(dropped.Completion!.ToolCalls);
        var kept = await ChatAsync(t, "Find it [fake:tool:lookup_policy]", tools: [lookup]);
        Assert.Equal("lookup_policy", Assert.Single(kept.Completion!.ToolCalls).Name);

        // Structured output is checked against the schema; output that does not match is not returned.
        var schema = JsonDocument.Parse("""{"type":"object","required":["answer"],"properties":{"answer":{"type":"string"}}}""").RootElement;
        var structured = await ChatAsync(t, "Answer in JSON", schema: schema);
        Assert.Contains("answer", JsonDocument.Parse(structured.Completion!.Content).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(AiDenials.ProviderUnavailable, (await ChatAsync(t, "Answer in JSON [fake:badjson]", schema: schema)).Denial);

        // Too large for every model's context window.
        Assert.Equal(AiDenials.ContextTooLarge, (await ChatAsync(t, new string('a', 400_000))).Denial);

        // Streaming: deltas, then the result; a cut stream is flagged partial.
        var chunks = await AsAsync(t, async g =>
        {
            var list = new List<AiStreamChunk>();
            await foreach (var c in g.StreamChatAsync(new AiRequest(AiUseCases.Assistant, [new AiMessage(AiRoles.User, "Stream this please")], DataClassification.Public)))
            {
                list.Add(c);
            }
            return list;
        });
        Assert.True(chunks.Count > 2);
        Assert.False(chunks[^1].Result!.Completion!.Partial);
        Assert.Contains("Stream this please", string.Concat(chunks.Select(c => c.Delta)));
        var cut = await AsAsync(t, async g =>
        {
            AiResult? last = null;
            await foreach (var c in g.StreamChatAsync(new AiRequest(AiUseCases.Assistant, [new AiMessage(AiRoles.User, "Cut me off [fake:cut]")], DataClassification.Public)))
            {
                last = c.Result ?? last;
            }
            return last!;
        });
        Assert.True(cut.Completion!.Partial);
        Assert.True(cut.Completion.Usage.OutputTokens > 0);

        // Embeddings use the embed route.
        var embedded = await AsAsync(t, g => g.EmbedAsync(new EmbeddingRequest(["Annual leave", "Sick leave"], DataClassification.Internal)));
        Assert.True(embedded.IsOk, embedded.Denial);
        Assert.Equal(2, embedded.Vectors!.Count);
        Assert.Equal(Embedding, embedded.Model);

        // FR-017: the judge differs from the model under test, and judge calls take no tools.
        var judged = await ChatAsync(t, "Grade this answer", useCase: AiUseCases.Evaluation, underTest: Large);
        Assert.Equal(Small, judged.Completion!.Model);
        Assert.Equal(AiDenials.InvalidRequest, (await ChatAsync(t, "Grade", useCase: AiUseCases.Evaluation, model: Large, underTest: Large)).Denial);
        Assert.Equal(AiDenials.InvalidRequest, (await ChatAsync(t, "Grade", useCase: AiUseCases.Evaluation, tools: [lookup])).Denial);
    }

    [Fact]
    public async Task One_tenants_policy_never_applies_to_or_shows_in_another()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        var routes = Routes([Large, Small], assistant: [Small, Large]);
        routes[AiUseCases.Agent] = [Small, Large];
        await SaveAsync(a, Policy([Small, Large, Embedding], routes));

        Assert.Equal(Large, (await ChatAsync(a, "Summarize", useCase: AiUseCases.Summarize)).Completion!.Model);
        Assert.Equal(Small, (await ChatAsync(b, "Summarize", useCase: AiUseCases.Summarize)).Completion!.Model);
        var view = await OkAsync(b["layla"].Client.GetAsync("/api/v1/ai/policy"));
        Assert.Equal("platform", view.GetProperty("source").GetString());
        Assert.Equal(0, view.GetProperty("version").GetInt32());
        Assert.Equal(0, await world.Database.CountInTenantAsync(b.Id, "select count(*) from ai.model_policy"));

        // Another tenant's route change is not found.
        var held = await SaveAsync(a, Policy([Small, Large, Embedding], Routes([Small, Large], assistant: [Large])), HttpStatusCode.Accepted);
        var id = held.GetProperty("routeChange").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await b["layla"].Client.PostAsJsonAsync($"/api/v1/ai/route-changes/{id}/override", new { reason = "Trying another tenant's change." })).StatusCode);
    }

    [Fact]
    public async Task Usage_is_recorded_per_call_and_shown_by_dimension_with_org_scope_and_the_five_person_minimum()
    {
        var t = await FreshAsync();
        var gulf = await world.CreateUnitAsync(t, "Gulf", null, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", null, "subsidiary");
        var people = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            await world.SeedUserAsync(t, $"g{i}", MembershipTypes.Employee);
            await world.AddMembershipAsync(t, $"g{i}", "Gulf");
            people.Add($"g{i}");
        }
        await world.SeedUserAsync(t, "e0", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "e0", "Egypt");
        await world.SeedUserAsync(t, "gadmin", MembershipTypes.Employee);
        await world.AssignAsync(t, "gadmin", BuiltInRoles.AiPolicyAdministrator, "org_subtree", gulf);

        foreach (var name in people.Append("e0"))
        {
            var r = await world.Factory.AsUserAsync(t.Id, t[name].Id, sp => sp.GetRequiredService<IAiGateway>()
                .ChatAsync(new AiRequest(AiUseCases.Assistant, [new AiMessage(AiRoles.User, "How many leave days do I have?")], DataClassification.Internal)));
            Assert.True(r.IsOk, r.Denial);
        }
        await world.Factory.AsUserAsync(t.Id, t["e0"].Id, sp => sp.GetRequiredService<IAiGateway>()
            .ChatAsync(new AiRequest(AiUseCases.Assistant, [new AiMessage(AiRoles.User, "Salary")], DataClassification.Confidential)));
        // The record survives a caller that never commits: it is written in its own transaction.
        Assert.Equal(7, await world.Database.CountInTenantAsync(t.Id, "select count(*) from ai.usage_record"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from ai.usage_record where model is not null and cost <= 0"));

        var byModel = await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/usage?groupBy=model"));
        Assert.Equal("tenant", byModel.GetProperty("scope").GetString());
        var row = Assert.Single(byModel.GetProperty("rows").EnumerateArray());
        Assert.Equal(Small, row.GetProperty("key").GetString());
        Assert.Equal(6, row.GetProperty("requests").GetInt64());
        Assert.True(row.GetProperty("cost").GetDecimal() > 0);
        var denied = Assert.Single(byModel.GetProperty("denied").EnumerateArray());
        Assert.Equal(AiDenials.ClassificationBlocked, denied.GetProperty("reason").GetString());
        foreach (var group in new[] { "day", "use_case", "provider", "unit", "agent" })
        {
            await OkAsync(t["sec"].Client.GetAsync($"/api/v1/ai/usage?groupBy={group}"));
        }
        var byUser = await OkAsync(t["sec"].Client.GetAsync("/api/v1/ai/usage?groupBy=user"));
        Assert.Equal(6, byUser.GetProperty("rows").GetArrayLength());
        Assert.Equal(6, byUser.GetProperty("labels").EnumerateObject().Count());

        // The Gulf administrator sees Gulf only, never named users, and only groups of five people or more.
        var scoped = await OkAsync(t["gadmin"].Client.GetAsync("/api/v1/ai/usage?groupBy=unit"));
        Assert.Equal("units", scoped.GetProperty("scope").GetString());
        var unit = Assert.Single(scoped.GetProperty("rows").EnumerateArray());
        Assert.Equal(gulf.ToString(), unit.GetProperty("key").GetString());
        Assert.Contains("Gulf", scoped.GetProperty("labels").GetProperty(gulf.ToString()).GetRawText());
        Assert.Equal(5, scoped.GetProperty("totals").GetProperty("requests").GetInt64());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["gadmin"].Client.GetAsync("/api/v1/ai/usage?groupBy=user")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync("/api/v1/ai/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["layla"].Client.GetAsync("/api/v1/ai/usage?groupBy=colour")).StatusCode);
        _ = egypt;

        // Another tenant sees none of it.
        var other = await FreshAsync();
        Assert.Equal(0, (await OkAsync(other["layla"].Client.GetAsync("/api/v1/ai/usage"))).GetProperty("totals").GetProperty("requests").GetInt64());
    }

    [Fact]
    public async Task The_monthly_request_quota_refuses_further_calls_once_reached()
    {
        var t = await FreshAsync();
        // New tenants take the latest published version, so the package's capabilities are published again at the end.
        var current = (await OkAsync(t["admin"].Client.GetAsync("/api/v1/tenant/entitlements"))).GetProperty("capabilities");
        var capped = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.GetRawText())!
            .ToDictionary(p => p.Key, p => (object)p.Value);
        capped[TeamBeats.Entitlements.Contracts.Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 2 };
        var draft = await OkAsync(world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace_plus/versions", new { capabilities = capped }), HttpStatusCode.Created);
        var version = draft.GetProperty("version").GetInt32();
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace_plus/versions/{version}:publish", null)).EnsureSuccessStatusCode();
        (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/subscription", new { packageKey = "workplace_plus", version })).EnsureSuccessStatusCode();
        var restore = await OkAsync(world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace_plus/versions", new { capabilities = current }), HttpStatusCode.Created);
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace_plus/versions/{restore.GetProperty("version").GetInt32()}:publish", null)).EnsureSuccessStatusCode();

        Assert.True((await ChatAsync(t, "One")).IsOk);
        Assert.True((await ChatAsync(t, "Two")).IsOk);
        var before = Provider.Requests;
        Assert.Equal(AiDenials.QuotaExhausted, (await ChatAsync(t, "Three")).Denial);
        Assert.Equal(before, Provider.Requests);
        // Judge calls of platform suites are charged to the platform and still run.
        var judge = await AsAsync(t, g => g.ChatAsync(new AiRequest(AiUseCases.Evaluation, [new AiMessage(AiRoles.User, "Grade")], DataClassification.Internal, PlatformCharged: true)));
        Assert.True(judge.IsOk, judge.Denial);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from ai.usage_record where charged_to = 'platform'"));
    }

    [Fact]
    public async Task Debug_windows_store_Public_and_Internal_text_encrypted_for_security_officers_only()
    {
        var t = await FreshAsync();
        // Metadata only by default: no text is stored without a window.
        await ChatAsync(t, "Before the window");
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from ai.debug_log"));

        Assert.Equal(HttpStatusCode.BadRequest, (await t["layla"].Client.PostAsJsonAsync("/api/v1/ai/debug-windows", new { useCase = "assistant", hours = 100 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sec"].Client.PostAsJsonAsync("/api/v1/ai/debug-windows", new { useCase = "assistant", hours = 2 })).StatusCode);
        var window = await OkAsync(t["layla"].Client.PostAsJsonAsync("/api/v1/ai/debug-windows", new { useCase = "assistant", hours = 2 }), HttpStatusCode.Created);
        var id = window.GetProperty("id").GetGuid();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AiLoggingWindowOpened, id.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await t["layla"].Client.PostAsJsonAsync("/api/v1/ai/debug-windows", new { useCase = "assistant", hours = 1 })).StatusCode);

        await ChatAsync(t, "Where is the travel policy?");
        await ChatAsync(t, "Public question", DataClassification.Public);
        await ChatAsync(t, "Summarize this", useCase: AiUseCases.Summarize);
        await ChatAsync(t, "Board minutes", DataClassification.Restricted);
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count(*) from ai.debug_log"));
        // Stored encrypted: the plain question is not in the row.
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from ai.debug_log where position(convert_to('travel policy', 'UTF8') in ciphertext) > 0"));

        var entries = await OkAsync(t["sec"].Client.GetAsync($"/api/v1/ai/debug-windows/{id}/entries"));
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Contains(entries.EnumerateArray(), e => e.GetProperty("text").GetString()!.Contains("Where is the travel policy?"));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["layla"].Client.GetAsync($"/api/v1/ai/debug-windows/{id}/entries")).StatusCode);

        var closed = await OkAsync(t["layla"].Client.DeleteAsync($"/api/v1/ai/debug-windows/{id}"));
        Assert.False(closed.GetProperty("open").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AiLoggingWindowClosed, id.ToString());
        await ChatAsync(t, "After the window");
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count(*) from ai.debug_log"));

        // Entries past 30 days are neither shown nor kept.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update ai.debug_log set at = at - interval '31 days'");
        Assert.Equal(0, (await OkAsync(t["sec"].Client.GetAsync($"/api/v1/ai/debug-windows/{id}/entries"))).GetArrayLength());

        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["sec"].Client.GetAsync($"/api/v1/ai/debug-windows/{id}/entries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["layla"].Client.DeleteAsync($"/api/v1/ai/debug-windows/{id}")).StatusCode);
    }

    [Fact]
    public async Task Platform_operator_maintains_the_catalogue_and_tenants_cannot()
    {
        var t = await FreshAsync();
        await MakePrivateConfidentialAsync();
        var catalogue = await OkAsync(world.Platform.GetAsync("/api/v1/platform/ai/catalogue"));
        Assert.Contains(catalogue.GetProperty("models").EnumerateArray(), m => m.GetProperty("model").GetString() == Private);
        await using (var conn = new Npgsql.NpgsqlConnection(world.Database.OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("select count(*) from audit.platform_audit_event where action = @a and target_id = @m", conn);
            cmd.Parameters.AddWithValue("a", AuditActions.AiCatalogueChanged);
            cmd.Parameters.AddWithValue("m", Private);
            Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) >= 1);
        }
        // Restricted can never be a catalogue ceiling, and a route needs models of the right capability.
        Assert.Equal(HttpStatusCode.BadRequest, (await world.Platform.PutAsJsonAsync("/api/v1/platform/ai/catalogue", new
        {
            model = "fake/teambeats-small", provider = "openrouter", capabilities = new[] { "chat" }, contextTokens = 8000, maxClassification = "restricted",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await world.Platform.PutAsJsonAsync("/api/v1/platform/ai/routes/embed", new { models = new[] { Small } })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync("/api/v1/platform/ai/catalogue", new { model = "x/y" })).StatusCode);
    }
}
