using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 T011 (US7, FR-016): SCORM 1.2 and xAPI packages are scanned and checked on import, unpacked to tenant
/// storage and played from a sandboxed content address that needs no session. Completion and score reported by the
/// package are recorded like native ones; a learner cannot mark a package lesson done themselves. The launch token works
/// only in its tenant and until it expires. The package needs the SCORM capability. Content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningPackageTests(TenantWorld world)
{
    private const string Api = "/api/v1/learning";
    private const string Activity = "https://example.test/activities/fire-safety";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var (path, content) in files)
            {
                // Stored, not compressed, so the test scanner sees the bytes as ClamAV would after unpacking.
                using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.NoCompression).Open());
                writer.Write(content);
            }
        }
        return memory.ToArray();
    }

    private static string ScormManifest(string version = "1.2") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <manifest identifier="safety" version="1" xmlns="http://www.imsproject.org/xsd/imscp_rootv1p1p2" xmlns:adlcp="http://www.adlnet.org/xsd/adlcp_rootv1p2">
          <metadata><schema>ADL SCORM</schema><schemaversion>{version}</schemaversion></metadata>
          <organizations default="org"><organization identifier="org"><title>Safety basics</title>
            <item identifier="i1" identifierref="r1"><title>Lesson</title></item></organization></organizations>
          <resources><resource identifier="r1" type="webcontent" adlcp:scormtype="sco" href="content/index.html"><file href="content/index.html"/></resource></resources>
        </manifest>
        """;

    private static byte[] Scorm() => Zip(("imsmanifest.xml", ScormManifest()), ("content/index.html", "<html><head><title>Safety</title></head><body>Safety basics</body></html>"),
        ("content/data.json", """{"slides":3}"""));

    private static byte[] Xapi() => Zip(
        ("tincan.xml", $"""<tincan xmlns="http://projecttincan.com/tincan.xsd"><activities><activity id="{Activity}" type="http://adlnet.gov/expapi/activities/course"><name>Fire safety</name><launch lang="en-us">index.html</launch></activity></activities></tincan>"""),
        ("index.html", "<html><head></head><body>Fire safety</body></html>"));

    private static HttpContent ZipBody(byte[] zip)
    {
        var body = new ByteArrayContent(zip);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return body;
    }

    private static string[] Keys(JsonElement problem) =>
        problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString()!)).ToArray()
            : [problem.GetProperty("code").GetString()!];

    private sealed record Setup(TestTenant T, Guid Course, Guid Package);

    /// <summary>A course with one package lesson, published with open enrolment, and Laila enrolled.</summary>
    private async Task<Setup> CourseAsync(byte[] zip, string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("lp-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "laila", MembershipTypes.Employee);
        var admin = t["admin"].Client;
        var draft = await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            openEnrolment = true, definition = new { titles = new Dictionary<string, string> { ["en"] = "Safety" }, lessons = Array.Empty<object>() },
        }), HttpStatusCode.Created);
        var course = draft.GetProperty("id").GetGuid();
        var imported = await OkAsync(admin.PostAsync($"{Api}/manage/courses/{course}/packages", ZipBody(zip)), HttpStatusCode.Created);
        var id = imported.GetProperty("id").GetGuid();
        var updated = await OkAsync(admin.PutAsJsonAsync($"{Api}/manage/courses/{course}", new
        {
            revision = draft.GetProperty("revision").GetInt32(), openEnrolment = true,
            definition = new
            {
                titles = new Dictionary<string, string> { ["en"] = "Safety" },
                lessons = new object[] { new { key = "sco", kind = "package", titles = new Dictionary<string, string> { ["en"] = "Safety basics" }, sources = new Dictionary<string, Guid> { ["en"] = id } } },
            },
        }));
        await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}:publish", new { revision = updated.GetProperty("revision").GetInt32() }));
        await OkAsync(t["laila"].Client.PostAsync($"{Api}/courses/{course}:enrol", null));
        return new Setup(t, course, id);
    }

    private async Task<string> LaunchAsync(Setup s) =>
        (await OkAsync(s.T["laila"].Client.PostAsJsonAsync($"{Api}/courses/{s.Course}/lessons/sco:launch", new { locale = "en" }))).GetProperty("url").GetString()!;

    private static string Root(string url) => string.Join('/', url.Split('?')[0].Split('/').Take(6));

    [Fact]
    public async Task A_SCORM_package_plays_without_a_session_and_its_pass_completes_the_course()
    {
        var s = await CourseAsync(Scorm());
        var t = s.T;
        var listed = await OkAsync(t["admin"].Client.GetAsync($"{Api}/manage/courses/{s.Course}/packages"));
        Assert.Equal(("scorm12", "Safety basics", "content/index.html", 3), (listed[0].GetProperty("kind").GetString(), listed[0].GetProperty("title").GetString(),
            listed[0].GetProperty("launchPath").GetString(), listed[0].GetProperty("files").GetInt32()));

        // The learner cannot mark it done; only the package's report does.
        var faked = await t["laila"].Client.PostAsync($"{Api}/courses/{s.Course}/lessons/sco:complete", null);
        Assert.Equal(HttpStatusCode.Conflict, faked.StatusCode);
        Assert.Contains("conflict.learning.package_needs_launch", await faked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var url = await LaunchAsync(s);
        var root = Root(url);
        Assert.StartsWith("/api/v1/learning/content/", url, StringComparison.Ordinal);
        Assert.EndsWith("/content/index.html", url, StringComparison.Ordinal);

        // No session: the content opens from the tenant host alone, sandboxed, with the bridge first in the head.
        var anonymous = world.Factory.TenantClient(t.Key);
        var page = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        var policy = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("sandbox allow-scripts", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'self'", policy, StringComparison.Ordinal);
        Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(page.Headers.Contains("Set-Cookie"));
        Assert.Contains($"<head><script src=\"{root}/__tb/scorm.js\"></script><title>", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var data = await anonymous.GetAsync($"{root}/content/data.json");
        Assert.Equal("*", data.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("LMSInitialize", await (await anonymous.GetAsync($"{root}/__tb/scorm.js")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{root}/content%2F..%2F..%2Fimsmanifest.xml")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{root}/missing.html")).StatusCode);

        // An unfinished launch saves where the learner was, and the next launch resumes there.
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsync($"{root}/__tb/runtime",
            new StringContent("""{"values":{"cmi.core.lesson_status":"incomplete","cmi.suspend_data":"slide=2","cmi.core.student_id":"forged"}}""", Encoding.UTF8, "text/plain"))).StatusCode);
        Assert.Equal("open", (await OkAsync(t["laila"].Client.GetAsync($"{Api}/courses/{s.Course}"))).GetProperty("me").GetProperty("state").GetString());
        var second = Root(await LaunchAsync(s));
        var bridge = await (await anonymous.GetAsync($"{second}/__tb/scorm.js")).Content.ReadAsStringAsync();
        Assert.Contains("slide=2", bridge, StringComparison.Ordinal);
        Assert.Contains(t["laila"].Id.ToString(), bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", bridge, StringComparison.Ordinal);

        // A pass with a score is recorded like a native completion, once.
        foreach (var _ in new[] { 1, 2 })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsync($"{second}/__tb/runtime",
                new StringContent("""{"values":{"cmi.core.lesson_status":"passed","cmi.core.score.raw":"8","cmi.core.score.max":"10"},"finish":true}""", Encoding.UTF8, "text/plain"))).StatusCode);
        }
        var me = (await OkAsync(t["laila"].Client.GetAsync($"{Api}/courses/{s.Course}"))).GetProperty("me");
        Assert.Equal("completed", me.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, me.GetProperty("certificateId").ValueKind);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from learning.attempt where lesson_key = 'sco' and score = 80"));

        // Another tenant's host does not know the token, and an expired launch opens nothing.
        var other = await world.CreateTenantAsync("lpo-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(other.Key).GetAsync(url)).StatusCode);
        await world.Database.OwnerExecInTenantAsync(t.Id, "update learning.package_launch set expires_at = now() - interval '1 minute'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{root}/__tb/xapi/statements")).StatusCode);
    }

    [Fact]
    public async Task An_xAPI_package_reports_through_its_statement_endpoint()
    {
        var s = await CourseAsync(Xapi());
        var t = s.T;
        var url = await LaunchAsync(s);
        var query = System.Web.HttpUtility.ParseQueryString(url.Split('?', 2)[1]);
        var endpoint = query["endpoint"]!;
        Assert.Equal(Activity, query["activity_id"]);
        Assert.DoesNotContain(t["laila"].Email, query["actor"], StringComparison.OrdinalIgnoreCase);
        var anonymous = world.Factory.TenantClient(t.Key);
        anonymous.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(query["auth"]!);
        anonymous.DefaultRequestHeaders.Add("X-Experience-API-Version", "1.0.3");

        var preflight = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Options, $"{endpoint}statements"));
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Contains("Authorization", preflight.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.Ordinal);

        static object Statement(string verb, string activity, double scaled) => new
        {
            actor = new { objectType = "Agent", account = new { homePage = "https://teambeats.app", name = "x" } },
            verb = new { id = $"http://adlnet.gov/expapi/verbs/{verb}" },
            @object = new { id = activity },
            result = new { score = new { scaled } },
        };

        // A failed try and a statement about another activity leave the course open.
        await OkAsync(anonymous.PostAsJsonAsync($"{endpoint}statements", new[] { Statement("failed", Activity, 0.4), Statement("completed", Activity + "/part-1", 1) }));
        Assert.Equal("open", (await OkAsync(t["laila"].Client.GetAsync($"{Api}/courses/{s.Course}"))).GetProperty("me").GetProperty("state").GetString());
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from learning.attempt where lesson_key = 'sco' and score = 40 and not passed"));

        // State documents are kept for the launch.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{endpoint}activities/state?stateId=bookmark")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PutAsync($"{endpoint}activities/state?stateId=bookmark", new StringContent("""{"page":4}""", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Contains("\"page\":4", await (await anonymous.GetAsync($"{endpoint}activities/state?stateId=bookmark")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await OkAsync(anonymous.PostAsJsonAsync($"{endpoint}statements", Statement("passed", Activity, 0.9)));
        var me = (await OkAsync(t["laila"].Client.GetAsync($"{Api}/courses/{s.Course}"))).GetProperty("me");
        Assert.Equal("completed", me.GetProperty("state").GetString());
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from learning.attempt where lesson_key = 'sco' and score = 90 and passed"));
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsync($"{endpoint}statements", new StringContent("not json", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Unsafe_or_unknown_packages_are_refused_and_the_capability_and_role_are_needed()
    {
        var t = await world.CreateTenantAsync("lpr-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        await world.SeedUserAsync(t, "laila", MembershipTypes.Employee);
        var admin = t["admin"].Client;
        var course = (await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            definition = new { titles = new Dictionary<string, string> { ["en"] = "Safety" }, lessons = Array.Empty<object>() },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        async Task<string[]> RefusedAsync(byte[] zip)
        {
            var response = await admin.PostAsync($"{Api}/manage/courses/{course}/packages", ZipBody(zip));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return Keys(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);
        }

        Assert.Contains("validation.learning.package_infected", await RefusedAsync(Zip(("imsmanifest.xml", ScormManifest()), ("content/index.html", TestSignatureScanner.Eicar))));
        Assert.Contains("validation.learning.package_zip", await RefusedAsync(Zip(("imsmanifest.xml", ScormManifest()), ("content/index.html", "x"), ("../escape.html", "x"))));
        Assert.Contains("validation.learning.package_manifest", await RefusedAsync(Zip(("index.html", "x"))));
        Assert.Contains("validation.learning.package_manifest", await RefusedAsync(Zip(("imsmanifest.xml", ScormManifest()))));
        Assert.Contains("validation.learning.package_manifest", await RefusedAsync(Zip(("imsmanifest.xml", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><manifest>&e;</manifest>"))));
        Assert.Contains("validation.learning.package_version", await RefusedAsync(Zip(("imsmanifest.xml", ScormManifest("2004 3rd Edition")), ("content/index.html", "x"))));
        Assert.Contains("validation.learning.package_zip", await RefusedAsync(Encoding.UTF8.GetBytes("not a zip")));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from learning.package"));

        // A learner cannot import; Workplace Plus has no SCORM import; another tenant cannot reach the course.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.PostAsync($"{Api}/manage/courses/{course}/packages", ZipBody(Scorm()))).StatusCode);
        var plus = await world.CreateTenantAsync("lpp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var plusCourse = (await OkAsync(plus["admin"].Client.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            definition = new { titles = new Dictionary<string, string> { ["en"] = "Safety" }, lessons = Array.Empty<object>() },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await plus["admin"].Client.PostAsync($"{Api}/manage/courses/{plusCourse}/packages", ZipBody(Scorm()))).StatusCode);
        Assert.False((await OkAsync(plus["admin"].Client.GetAsync($"{Api}/rights"))).GetProperty("packages").GetBoolean());
        Assert.True((await OkAsync(admin.GetAsync($"{Api}/rights"))).GetProperty("packages").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await plus["admin"].Client.GetAsync($"{Api}/manage/courses/{course}/packages")).StatusCode);
    }
}
