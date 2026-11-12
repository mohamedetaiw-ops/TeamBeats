using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Integrations.Lms;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 T010 (US6, FR-015): the LMS connector over the generic LMS REST pack. The catalogue is imported as courses
/// that open the LMS, assignments of them are pushed once with only the mapped fields, completions pulled back complete
/// them with certificates and show in reports, an LMS that does not answer leaves its courses "unavailable" and nothing
/// else broken, and the package, permission and tenant boundaries hold. The LMS is a fake host; names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningLmsTests(TenantWorld world)
{
    private const string Api = "/api/v1/learning";
    private const string Packs = "/api/v1/integrations/pack-connections";

    private FakeRestApi Rest => world.Factory.Rest;

    /// <summary>A small LMS: two catalogue pages, the enrolments it received, the completions it reports, and a switch that makes it fail.</summary>
    private sealed class FakeLms(string host)
    {
        public ConcurrentQueue<JsonElement> Enrolments { get; } = new();
        public List<object> Completions { get; } = [];
        public string FirstTitle = "Data protection";
        public bool Down;
        public string? LastSince;

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            if (Down)
            {
                return FakeRestApi.Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            var path = s.Uri.AbsolutePath;
            if (s.Method == "POST" && path == "/api/enrolments")
            {
                Enrolments.Enqueue(JsonDocument.Parse(s.Body!).RootElement.Clone());
                return FakeRestApi.Json(HttpStatusCode.Created, """{"ok":true}""");
            }
            if (path == "/api/courses" && s.Uri.Query.Contains("page=2", StringComparison.Ordinal))
            {
                return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    items = new object[]
                    {
                        new { id = "C-2", name = "Fire safety", url = $"https://{host}/launch/C-2" },
                        // Not opened from TeamBeats: the address is not https on an allowed host.
                        new { id = "C-3", name = "Unsafe", url = "javascript:alert(1)" },
                    },
                }));
            }
            if (path == "/api/courses")
            {
                return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    items = new[] { new { id = "C-1", name = FirstTitle, description = "Handling personal data.", url = $"https://{host}/launch/C-1", lang = "en" } },
                    next = "/api/courses?page=2",
                }));
            }
            if (path == "/api/completions")
            {
                LastSince = System.Web.HttpUtility.ParseQueryString(s.Uri.Query)["since"];
                lock (Completions)
                {
                    return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = Completions.ToArray() }));
                }
            }
            return FakeRestApi.Json(HttpStatusCode.OK, "{}");
        }
    }

    private static object Settings(TestTenant t, string host, string completionsPath = "/completions?since={since}") => new
    {
        baseUrl = $"https://{host}/api",
        auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "lms-token") },
        coursesPath = "/courses",
        coursesItemsPath = "$.items",
        courseFields = new Dictionary<string, string> { ["external_id"] = "$.id", ["title"] = "$.name", ["summary"] = "$.description", ["launch_url"] = "$.url", ["language"] = "$.lang" },
        assignPath = "/enrolments",
        assignFields = new Dictionary<string, string> { ["reference"] = "ref", ["course_id"] = "course.id", ["learner_email"] = "user.email", ["due_on"] = "due" },
        completionsPath,
        completionsItemsPath = "$.data",
        completionFields = new Dictionary<string, string> { ["course_id"] = "$.course", ["learner_email"] = "$.email", ["completed_at"] = "$.finished", ["score"] = "$.score" },
        nextPath = "$.next",
        idempotencyHeader = "Idempotency-Key",
    };

    private sealed record Org(TestTenant T, FakeLms Lms, string Host);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<Org> SetupAsync()
    {
        var t = await world.CreateTenantAsync("ll-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "laila", "omar" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var host = $"lms-{Guid.NewGuid():N}"[..20] + ".rest.test";
        var lms = new FakeLms(host);
        Rest.On(host, lms.Respond);
        var admin = t["admin"].Client;
        await OkAsync(admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host }));
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "lms-token"), "fictional-lms-token");

        // The completions path must say where the time goes.
        var bad = await admin.PostAsJsonAsync(Packs, new { kind = LmsPack.KindName, displayName = "LMS", settings = Settings(t, host, "/completions") });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("validation.lms.path", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var created = await OkAsync(admin.PostAsJsonAsync(Packs, new { kind = LmsPack.KindName, displayName = "LMS", settings = Settings(t, host) }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("ok", (await OkAsync(admin.PostAsync($"{Packs}/{id}:test", null))).GetProperty("status").GetString());
        await OkAsync(admin.PostAsync($"{Packs}/{id}:enable", null));
        return new Org(t, lms, host);
    }

    private Task<JsonElement> SyncAsync(TestTenant t) => OkAsync(t["admin"].Client.PostAsync($"{Api}/manage/lms:sync", null));

    /// <summary>Runs the durable connector jobs that are due, as the job worker does.</summary>
    private Task RunJobsAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 50);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private async Task<Guid> CourseAsync(TestTenant t, string externalId) =>
        await world.Database.TenantScalarAsync<Guid>(t.Id, "select id from learning.course where tenant_id = @t and external_id = @e", ("t", t.Id), ("e", externalId));

    [Fact]
    public async Task The_catalogue_is_imported_assignments_are_pushed_once_and_completions_complete_them()
    {
        var (t, lms, host) = await SetupAsync();
        var status = await SyncAsync(t);
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal(2, status.GetProperty("courses").GetInt32());
        var course = await CourseAsync(t, "C-1");

        // Laila finds it in the catalogue as an LMS course that opens the LMS.
        var catalogue = await OkAsync(t["laila"].Client.GetAsync($"{Api}/catalogue"));
        var item = catalogue.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == course);
        Assert.True(item.GetProperty("external").GetBoolean());
        Assert.Equal("Data protection", item.GetProperty("titles").GetProperty("en").GetString());
        var view = await OkAsync(t["laila"].Client.GetAsync($"{Api}/courses/{course}"));
        Assert.True(view.GetProperty("external").GetProperty("available").GetBoolean());
        Assert.Equal($"https://{host}/launch/C-1", view.GetProperty("external").GetProperty("launchUrl").GetString());
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from learning.course where tenant_id = @t and external_id = 'C-3'", ("t", t.Id)));

        // The LMS publishes its own courses; who sees it is still decided here.
        var admin = t["admin"].Client;
        var detail = await OkAsync(admin.GetAsync($"{Api}/manage/courses/{course}"));
        Assert.Equal("C-1", detail.GetProperty("externalId").GetString());
        var publish = await admin.PostAsJsonAsync($"{Api}/manage/courses/{course}:publish", new { revision = detail.GetProperty("revision").GetInt32() });
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
        Assert.Contains("conflict.learning.external_course", await publish.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        detail = await OkAsync(admin.PutAsJsonAsync($"{Api}/manage/courses/{course}", new
        {
            revision = detail.GetProperty("revision").GetInt32(), category = "Compliance", openEnrolment = true,
            definition = new { titles = new Dictionary<string, string> { ["en"] = "Renamed here" }, lessons = Array.Empty<object>() },
        }));
        Assert.Equal("Data protection", detail.GetProperty("draft").GetProperty("titles").GetProperty("en").GetString());
        Assert.False(detail.GetProperty("draftChanged").GetBoolean());

        // A rule gives it to Laila and Omar; each assignment goes to the LMS once, with only the mapped fields.
        await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}/rules", new { people = new[] { t["laila"].Id, t["omar"].Id }, dueDays = 30 }), HttpStatusCode.Created);
        Assert.Equal(0, (await SyncAsync(t)).GetProperty("waiting").GetInt32());
        await RunJobsAsync(t);
        await SyncAsync(t);
        await RunJobsAsync(t);
        Assert.Equal(2, lms.Enrolments.Count);
        var sent = lms.Enrolments.Select(e => e.GetProperty("user").GetProperty("email").GetString()).Order().ToArray();
        Assert.Equal(new[] { t["laila"].Email, t["omar"].Email }.Order().ToArray(), sent);
        Assert.All(lms.Enrolments, e =>
        {
            Assert.Equal("C-1", e.GetProperty("course").GetProperty("id").GetString());
            Assert.Equal(["course", "due", "ref", "user"], e.EnumerateObject().Select(p => p.Name).Order().ToArray());
        });

        // Laila finished it in the LMS; a completion for an unknown person or course changes nothing.
        var finished = DateTimeOffset.UtcNow.AddHours(-2);
        lock (lms.Completions)
        {
            lms.Completions.Add(new { course = "C-1", email = t["laila"].Email, finished = finished.ToString("O"), score = 88 });
            lms.Completions.Add(new { course = "C-1", email = "nobody@unknown.test", finished = finished.ToString("O"), score = 10 });
            lms.Completions.Add(new { course = "C-404", email = t["omar"].Email, finished = finished.ToString("O") });
        }
        await SyncAsync(t);
        Assert.NotNull(lms.LastSince);
        var mine = await OkAsync(t["laila"].Client.GetAsync($"{Api}/me"));
        var done = mine.GetProperty("completed").EnumerateArray().Single(c => c.GetProperty("courseId").GetGuid() == course);
        Assert.True(done.GetProperty("external").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("certificateId").ValueKind);
        Assert.Contains((await OkAsync(t["omar"].Client.GetAsync($"{Api}/me"))).GetProperty("assigned").EnumerateArray(), c => c.GetProperty("courseId").GetGuid() == course);

        // Reports count it, with the LMS score; reading the overlap again records nothing twice.
        var report = await OkAsync(admin.GetAsync($"{Api}/reports/courses/{course}"));
        Assert.Equal(1, report.GetProperty("total").GetProperty("completed").GetInt32());
        await SyncAsync(t);
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from learning.certificate where tenant_id = @t and course_id = @c", ("t", t.Id), ("c", course)));
        Assert.Equal(88, await world.Database.TenantScalarAsync<int>(t.Id, "select max(score) from learning.attempt a join learning.assignment s on s.id = a.assignment_id where s.tenant_id = @t and s.learner_id = @u",
            ("t", t.Id), ("u", t["laila"].Id)));

        // A new title in the LMS is published as a new version.
        lms.FirstTitle = "Data protection 2027";
        await SyncAsync(t);
        view = await OkAsync(t["omar"].Client.GetAsync($"{Api}/courses/{course}"));
        Assert.Equal(("Data protection 2027", 2), (view.GetProperty("titles").GetProperty("en").GetString(), view.GetProperty("version").GetInt32()));
    }

    [Fact]
    public async Task An_unavailable_LMS_marks_its_courses_unavailable_and_nothing_else_breaks()
    {
        var (t, lms, _) = await SetupAsync();
        await SyncAsync(t);
        var course = await CourseAsync(t, "C-1");
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/manage/courses/{course}/rules", new { people = new[] { t["laila"].Id }, dueDays = 30 }), HttpStatusCode.Created);

        lms.Down = true;
        var status = await SyncAsync(t);
        Assert.False(status.GetProperty("available").GetBoolean());
        Assert.Equal(1, status.GetProperty("waiting").GetInt32());
        var view = await OkAsync(t["laila"].Client.GetAsync($"{Api}/courses/{course}"));
        Assert.False(view.GetProperty("external").GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("external").GetProperty("launchUrl").ValueKind);
        Assert.Contains((await OkAsync(t["laila"].Client.GetAsync($"{Api}/catalogue"))).EnumerateArray(), c => c.GetProperty("id").GetGuid() == course);
        Assert.Contains((await OkAsync(t["laila"].Client.GetAsync($"{Api}/me"))).GetProperty("assigned").EnumerateArray(), c => c.GetProperty("courseId").GetGuid() == course);

        // When it answers again, the waiting assignment goes out.
        lms.Down = false;
        status = await SyncAsync(t);
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal(0, status.GetProperty("waiting").GetInt32());
        await RunJobsAsync(t);
        Assert.Single(lms.Enrolments);
    }

    [Fact]
    public async Task Only_learning_administrators_with_the_package_sync_and_other_tenants_see_nothing()
    {
        var (t, _, _) = await SetupAsync();
        await SyncAsync(t);
        var course = await CourseAsync(t, "C-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.GetAsync($"{Api}/manage/lms")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.PostAsync($"{Api}/manage/lms:sync", null)).StatusCode);

        // Workplace has no LMS connector.
        var basic = await world.CreateTenantAsync("llb-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync($"{Api}/manage/lms")).StatusCode);

        // Another tenant has no LMS connection: its sync imports nothing and it cannot open the first tenant's course.
        var other = await world.CreateTenantAsync("llo-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var status = await OkAsync(other["admin"].Client.PostAsync($"{Api}/manage/lms:sync", null));
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(0, status.GetProperty("courses").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/courses/{course}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/manage/courses/{course}")).StatusCode);
    }
}
