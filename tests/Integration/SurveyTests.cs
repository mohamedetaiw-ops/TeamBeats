using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T007 for surveys (US3 scenarios 2 to 5, FR-005, FR-009, FR-010, edge case "audience shrinks below
/// threshold"): an author with survey publishing on an org unit builds and publishes a survey for that unit and the
/// units below it; people in the audience answer once; anonymous responses keep no user and the "has responded" marks
/// go at close; results under the threshold stay hidden in views, group filters and the export, including a group that
/// could be worked out from the others; named responses show names only to those who manage the survey. Surveys are
/// not in Workplace. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SurveyTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString()!)).ToArray()
            : [];
    }

    private static readonly string[] Finance = ["fa", "fb", "fc", "fd", "fe", "ff"];
    private static readonly string[] Hr = ["ha", "hb"];

    /// <summary>Holding over Gulf and Africa; Gulf has Finance (six people) and HR (two). Mona publishes surveys for Gulf.</summary>
    private async Task<TestTenant> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sv-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Finance", gulf, "department");
        await world.CreateUnitAsync(t, "HR", gulf, "department");
        foreach (var name in Finance.Concat(Hr).Concat(["mona", "gulfie", "kofi", "amal"]))
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in Finance)
        {
            await world.AddMembershipAsync(t, name, "Finance");
        }
        foreach (var name in Hr)
        {
            await world.AddMembershipAsync(t, name, "HR");
        }
        await world.AddMembershipAsync(t, "mona", "Gulf");
        await world.AddMembershipAsync(t, "gulfie", "Gulf");
        await world.AddMembershipAsync(t, "kofi", "Africa");
        await world.AddMembershipAsync(t, "amal", "Africa");
        if (package != "workplace")
        {
            await world.AssignAsync(t, "mona", "organization_administrator", "org_subtree", gulf);
            await world.AssignAsync(t, "amal", "organization_administrator", "org_subtree", africa);
        }
        return t;
    }

    private static object Pulse(Guid unit, bool anonymous = true, int? threshold = null, DateTimeOffset? closesAt = null) => new
    {
        orgUnitId = unit,
        title = "How is your week going?",
        intro = "A short pulse check.",
        anonymous,
        threshold,
        closesAt = closesAt ?? DateTimeOffset.UtcNow.AddDays(7),
        questions = new object[]
        {
            new { kind = "single", prompt = "Do you have what you need to do your job?", required = true, options = new[] { "Yes", "Partly", "No" } },
            new { kind = "scale", prompt = "How would you rate your workload?", required = false, options = Array.Empty<string>() },
            new { kind = "text", prompt = "What would make next week better?", required = false, options = Array.Empty<string>() },
        },
    };

    private static async Task<(Guid Id, JsonElement View)> PublishedAsync(TestTenant t, object survey)
    {
        var draft = await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Api}/surveys", survey), HttpStatusCode.Created);
        var id = draft.GetProperty("id").GetGuid();
        var view = await OkAsync(t["mona"].Client.PostAsync($"{Api}/surveys/{id}:publish", null));
        return (id, view);
    }

    private static Guid QuestionId(JsonElement survey, string kind) =>
        survey.GetProperty("questions").EnumerateArray().First(q => q.GetProperty("kind").GetString() == kind).GetProperty("id").GetGuid();

    private static Guid OptionId(JsonElement survey, string label) =>
        survey.GetProperty("questions").EnumerateArray().SelectMany(q => q.GetProperty("options").EnumerateArray())
            .First(o => o.GetProperty("label").GetString() == label).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> AnswerAsync(HttpClient client, JsonElement survey, string choice, int? rating = null, string? text = null)
    {
        var answers = new List<object> { new { questionId = QuestionId(survey, "single"), optionIds = new[] { OptionId(survey, choice) } } };
        if (rating is { } r)
        {
            answers.Add(new { questionId = QuestionId(survey, "scale"), value = r });
        }
        if (text is not null)
        {
            answers.Add(new { questionId = QuestionId(survey, "text"), text });
        }
        return client.PostAsJsonAsync($"{Api}/surveys/{survey.GetProperty("id").GetGuid()}/responses", new { answers });
    }

    [Fact]
    public async Task Anonymous_responses_keep_no_identity_and_the_marks_go_at_close()
    {
        var t = await SetupAsync();
        var (id, survey) = await PublishedAsync(t, Pulse(t.Unit("Gulf")));
        Assert.Equal("open", survey.GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementSurveyPublished, id.ToString());

        // People in Gulf and the units below it see the survey; Africa does not even learn it exists.
        var listed = await OkAsync(t["fa"].Client.GetAsync($"{Api}/surveys"));
        Assert.Contains(listed.GetProperty("toAnswer").EnumerateArray(), s => s.GetProperty("id").GetGuid() == id);
        Assert.DoesNotContain((await OkAsync(t["kofi"].Client.GetAsync($"{Api}/surveys"))).GetProperty("toAnswer").EnumerateArray(), s => s.GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await t["kofi"].Client.GetAsync($"{Api}/surveys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AnswerAsync(t["kofi"].Client, survey, "Yes")).StatusCode);

        // A required question must be answered, and answers must fit the question.
        var noRequired = await t["fa"].Client.PostAsJsonAsync($"{Api}/surveys/{id}/responses", new { answers = new[] { new { questionId = QuestionId(survey, "scale"), value = 3 } } });
        Assert.Contains("validation.engagement.survey_required", await ErrorKeysAsync(noRequired));
        Assert.Contains("validation.engagement.survey_answer", await ErrorKeysAsync(await AnswerAsync(t["fa"].Client, survey, "Yes", rating: 9)));

        var answered = await OkAsync(AnswerAsync(t["fa"].Client, survey, "Yes", 4, "More focus time"));
        Assert.True(answered.GetProperty("responded").GetBoolean());
        Assert.False(answered.GetProperty("canRespond").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await AnswerAsync(t["fa"].Client, survey, "No")).StatusCode);
        foreach (var name in Finance.Skip(1))
        {
            await OkAsync(AnswerAsync(t[name].Client, survey, name == "fb" ? "No" : "Partly", 3));
        }
        foreach (var name in Hr)
        {
            await OkAsync(AnswerAsync(t[name].Client, survey, "No", 2, "Fewer meetings"));
        }

        // Nothing in the responses names or dates a person; only the marks say who answered, kept apart.
        Assert.Equal(8, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.survey_response where survey_id = @s", ("s", id)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from engagement.survey_response where survey_id = @s and (user_id is not null or submitted_at is not null)", ("s", id)));
        Assert.Equal(8, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.survey_responded where survey_id = @s", ("s", id)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where target_id = @s and action <> 'engagement.survey_published'", ("s", id.ToString())));

        // An anonymous survey shows no results while it is open, so nobody can watch one answer arrive.
        var open = await OkAsync(t["mona"].Client.GetAsync($"{Api}/surveys/{id}/results"));
        Assert.False(open.GetProperty("visible").GetBoolean());
        Assert.Equal(8, open.GetProperty("responses").GetInt32());
        Assert.Empty(open.GetProperty("questions").EnumerateArray());

        // Responders, and publishers for another unit, do not see results.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["fa"].Client.GetAsync($"{Api}/surveys/{id}/results")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["amal"].Client.GetAsync($"{Api}/surveys/{id}/results")).StatusCode);

        var closed = await OkAsync(t["mona"].Client.PostAsync($"{Api}/surveys/{id}:close", null));
        Assert.Equal("closed", closed.GetProperty("status").GetString());
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.survey_responded where survey_id = @s", ("s", id)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementSurveyClosed, id.ToString());
        Assert.Equal(1, await world.Database.OutboxCountAsync("SurveyClosed", t.Id));

        var results = await OkAsync(t["mona"].Client.GetAsync($"{Api}/surveys/{id}/results"));
        Assert.True(results.GetProperty("visible").GetBoolean());
        var single = results.GetProperty("questions").EnumerateArray().Single(q => q.GetProperty("kind").GetString() == "single");
        Assert.Equal(8, single.GetProperty("answered").GetInt32());
        Assert.Equal(3, single.GetProperty("options").EnumerateArray().Single(o => o.GetProperty("label").GetString() == "No").GetProperty("count").GetInt32());
        var texts = results.GetProperty("questions").EnumerateArray().Single(q => q.GetProperty("kind").GetString() == "text").GetProperty("texts");
        Assert.Equal(new[] { "Fewer meetings", "Fewer meetings", "More focus time" }, texts.EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task Results_under_the_threshold_stay_hidden_in_views_filters_and_exports()
    {
        var t = await SetupAsync();
        var (id, survey) = await PublishedAsync(t, Pulse(t.Unit("Gulf")));
        foreach (var name in Finance)
        {
            await OkAsync(AnswerAsync(t[name].Client, survey, "Yes", 4, "=HYPERLINK(\"x\")"));
        }
        await OkAsync(AnswerAsync(t["ha"].Client, survey, "No", 1, "Only HR would say this"));
        // The survey passes its end date and closes the first time anyone looks.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update engagement.survey set closes_at = now() - interval '1 minute' where id = @s", ("s", id));
        Assert.Equal("closed", (await OkAsync(t["mona"].Client.GetAsync($"{Api}/surveys/{id}"))).GetProperty("status").GetString());

        // Overall 7 reaches 5, but HR has 1. Showing Finance alone would give HR away by subtraction, so it hides too.
        var results = await OkAsync(t["mona"].Client.GetAsync($"{Api}/surveys/{id}/results"));
        Assert.True(results.GetProperty("visible").GetBoolean());
        var groups = results.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.GetProperty("visible").GetBoolean()));
        Assert.All(groups, g => Assert.Equal(JsonValueKind.Null, g.GetProperty("responses").ValueKind));
        foreach (var unit in new[] { t.Unit("Finance"), t.Unit("HR") })
        {
            var filtered = await OkAsync(t["mona"].Client.GetAsync($"{Api}/surveys/{id}/results?group={unit}"));
            Assert.False(filtered.GetProperty("visible").GetBoolean());
            Assert.Empty(filtered.GetProperty("questions").EnumerateArray());
        }

        // The export holds the whole survey only, no group lines, and text answers with no names, safe for spreadsheets.
        var export = await t["mona"].Client.GetAsync($"{Api}/surveys/{id}/results.csv");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await export.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("group,question,answer,count", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("\"\",", l));
        Assert.Contains("\"\",\"Do you have what you need to do your job?\",\"Yes\",6", lines);
        Assert.Contains(lines, l => l.Contains("\"'=HYPERLINK(\"\"x\"\")\""));
        Assert.DoesNotContain(t["ha"].Id.ToString(), csv);

        // A survey that never reaches the threshold shows nothing at all, export included.
        var (small, smallView) = await PublishedAsync(t, Pulse(t.Unit("Gulf")));
        await OkAsync(AnswerAsync(t["fa"].Client, smallView, "Yes"));
        await OkAsync(AnswerAsync(t["fb"].Client, smallView, "No"));
        await OkAsync(t["mona"].Client.PostAsync($"{Api}/surveys/{small}:close", null));
        var hidden = await OkAsync(t["mona"].Client.GetAsync($"{Api}/surveys/{small}/results"));
        Assert.False(hidden.GetProperty("visible").GetBoolean());
        Assert.Empty(hidden.GetProperty("questions").EnumerateArray());
        var smallCsv = Encoding.UTF8.GetString(await (await t["mona"].Client.GetAsync($"{Api}/surveys/{small}/results.csv")).Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        Assert.Equal("group,question,answer,count\n", smallCsv);
    }

    [Fact]
    public async Task Named_responses_show_names_only_to_those_who_manage_the_survey()
    {
        var t = await SetupAsync();
        var (id, survey) = await PublishedAsync(t, Pulse(t.Unit("Gulf"), anonymous: false));
        await OkAsync(AnswerAsync(t["fa"].Client, survey, "Partly", 5, "All good"));
        await OkAsync(AnswerAsync(t["gulfie"].Client, survey, "Yes"));
        Assert.Equal(HttpStatusCode.Conflict, (await AnswerAsync(t["fa"].Client, survey, "No")).StatusCode);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.survey_responded where survey_id = @s", ("s", id)));

        var responses = await OkAsync(t["mona"].Client.GetAsync($"{Api}/surveys/{id}/responses"));
        Assert.Equal(new[] { t["fa"].Id, t["gulfie"].Id }.Order(), responses.EnumerateArray().Select(r => r.GetProperty("person").GetProperty("id").GetGuid()).Order());
        // A tenant administrator holds survey publishing everywhere, so sees them too; a responder does not.
        Assert.Equal(2, (await OkAsync(t["admin"].Client.GetAsync($"{Api}/surveys/{id}/responses"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["fa"].Client.GetAsync($"{Api}/surveys/{id}/responses")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["amal"].Client.GetAsync($"{Api}/surveys/{id}/responses")).StatusCode);

        // A named survey's results show to its managers while it runs; there are no names in them.
        var results = await OkAsync(t["mona"].Client.GetAsync($"{Api}/surveys/{id}/results"));
        Assert.True(results.GetProperty("visible").GetBoolean());
        Assert.Equal(2, results.GetProperty("responses").GetInt32());

        // An anonymous survey has no list of responses at all.
        var (anon, _) = await PublishedAsync(t, Pulse(t.Unit("Gulf")));
        Assert.Equal(HttpStatusCode.NotFound, (await t["mona"].Client.GetAsync($"{Api}/surveys/{anon}/responses")).StatusCode);
    }

    [Fact]
    public async Task Drafts_change_until_published_and_only_publishers_on_the_unit_build_surveys()
    {
        var t = await SetupAsync();
        var gulf = t.Unit("Gulf");

        // Survey publishing is checked on the unit: Mona builds for Gulf, not for Africa; employees build none.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.PostAsJsonAsync($"{Api}/surveys", Pulse(t.Unit("Africa")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["fa"].Client.PostAsJsonAsync($"{Api}/surveys", Pulse(gulf))).StatusCode);
        Assert.False((await OkAsync(t["fa"].Client.GetAsync($"{Api}/surveys"))).GetProperty("canCreate").GetBoolean());
        Assert.True((await OkAsync(t["mona"].Client.GetAsync($"{Api}/surveys"))).GetProperty("canCreate").GetBoolean());

        var bad = await t["mona"].Client.PostAsJsonAsync($"{Api}/surveys", new
        {
            orgUnitId = gulf, title = "", anonymous = true, threshold = 2, closesAt = DateTimeOffset.UtcNow.AddDays(-1),
            questions = new object[] { new { kind = "single", prompt = "Pick", options = new[] { "Only one" } }, new { kind = "essay", prompt = "Hm" } },
        });
        var keys = await ErrorKeysAsync(bad);
        Assert.Contains("validation.engagement.survey_title", keys);
        Assert.Contains("validation.engagement.survey_threshold", keys);
        Assert.Contains("validation.engagement.survey_closes", keys);
        Assert.Contains("validation.engagement.survey_options", keys);
        Assert.Contains("validation.engagement.survey_question_kind", keys);

        // A draft has no end date yet and cannot be published like that; nobody but its managers sees it.
        var draft = await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Api}/surveys", new
        {
            orgUnitId = gulf, title = "Draft", anonymous = true, questions = new object[] { new { kind = "text", prompt = "Anything else?" } },
        }), HttpStatusCode.Created);
        var id = draft.GetProperty("id").GetGuid();
        Assert.True(draft.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await t["fa"].Client.GetAsync($"{Api}/surveys/{id}")).StatusCode);
        Assert.Contains("validation.engagement.survey_closes", await ErrorKeysAsync(await t["mona"].Client.PostAsync($"{Api}/surveys/{id}:publish", null)));

        var edited = await OkAsync(t["mona"].Client.PutAsJsonAsync($"{Api}/surveys/{id}", Pulse(gulf, threshold: 8)));
        Assert.Equal(3, edited.GetProperty("questions").GetArrayLength());
        Assert.Equal(8, edited.GetProperty("threshold").GetInt32());
        await OkAsync(t["mona"].Client.PostAsync($"{Api}/surveys/{id}:publish", null));
        Assert.Equal(HttpStatusCode.Conflict, (await t["mona"].Client.PutAsJsonAsync($"{Api}/surveys/{id}", Pulse(gulf))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t["mona"].Client.PostAsync($"{Api}/surveys/{id}:publish", null)).StatusCode);

        // Engagement administrators set the tenant threshold within 3 to 50; surveys cannot go below it.
        var settings = await t["admin"].Client.GetAsync($"{Api}/settings/surveys");
        Assert.Equal(5, (await OkAsync(Task.FromResult(settings))).GetProperty("threshold").GetInt32());
        var etag = settings.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync($"{Api}/settings/surveys")).StatusCode);
        var tooLow = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/surveys") { Content = JsonContent.Create(new { threshold = 2 }) };
        tooLow.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Contains("validation.engagement.survey_threshold", await ErrorKeysAsync(await t["admin"].Client.SendAsync(tooLow)));
        var raise = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/surveys") { Content = JsonContent.Create(new { threshold = 10 }) };
        raise.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(10, (await OkAsync(t["admin"].Client.SendAsync(raise))).GetProperty("threshold").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementSurveyThresholdChanged);
        var stale = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/surveys") { Content = JsonContent.Create(new { threshold = 12 }) };
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.SendAsync(stale)).StatusCode);
        Assert.Contains("validation.engagement.survey_threshold", await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync($"{Api}/surveys", Pulse(gulf, threshold: 6))));
        Assert.Equal(10, (await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Api}/surveys", Pulse(gulf)), HttpStatusCode.Created)).GetProperty("threshold").GetInt32());
    }

    [Fact]
    public async Task Surveys_stay_in_their_tenant_and_out_of_workplace()
    {
        var t = await SetupAsync();
        var (id, survey) = await PublishedAsync(t, Pulse(t.Unit("Gulf")));
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/surveys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/surveys/{id}/results")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AnswerAsync(other["fa"].Client, survey, "Yes")).StatusCode);
        Assert.DoesNotContain((await OkAsync(other["fa"].Client.GetAsync($"{Api}/surveys"))).GetProperty("toAnswer").EnumerateArray(), s => s.GetProperty("id").GetGuid() == id);

        var w = await SetupAsync("workplace");
        var denied = await w["admin"].Client.PostAsJsonAsync($"{Api}/surveys", Pulse(w.Unit("Gulf")));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("entitlement.not_in_package", await denied.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await w["fa"].Client.GetAsync($"{Api}/surveys")).StatusCode);
    }
}
