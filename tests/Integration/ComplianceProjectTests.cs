using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 R2 slice 5 (T028, T029; US7, FR-021 to FR-024): compliance projects. A project starts from a framework of
/// the control library and copies its controls; owners move their controls on and attach audit exports as evidence with
/// the export's hash; the readiness view counts by status, owner and org unit; a project shared to a guest space is
/// read-only to its guests; scoped compliance officers see only their subtree; the feature is in Enterprise only.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ComplianceProjectTests(TenantWorld world)
{
    private const string Api = "/api/v1/compliance";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var problem = await OkAsync(call, expected);
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : problem.GetProperty("detailKey").GetString()!;
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> call) => (await call).StatusCode;

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("cp-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "officer", "omar", "laila", "sara" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "officer", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return t;
    }

    private static async Task<(Guid Id, JsonElement[] Controls)> ProjectAsync(TestTenant t, string user, string framework, Guid? unit = null, string name = "ISO 27001 surveillance audit")
    {
        var created = await OkAsync(t[user].Client.PostAsJsonAsync($"{Api}/projects", new { name, frameworkKey = framework, orgUnitId = unit }), HttpStatusCode.Created);
        return (created.GetProperty("project").GetProperty("id").GetGuid(), created.GetProperty("controls").EnumerateArray().ToArray());
    }

    private static Guid Control(JsonElement[] controls, string code) => controls.Single(c => c.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

    [Fact]
    public async Task A_project_starts_from_a_framework_of_the_library_and_follows_permissions_and_plan()
    {
        var t = await FreshAsync();

        // The library holds the three platform sets, with titles in English and Arabic.
        var library = await OkAsync(t["officer"].Client.GetAsync($"{Api}/library"));
        var sets = library.GetProperty("frameworks").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!, f => f.GetProperty("controlCount").GetInt32());
        Assert.Equal((93, 38, 29), (sets["iso27001"], sets["soc2"], sets["nca_ecc"]));
        Assert.True(library.GetProperty("canManage").GetBoolean());
        var iso = await OkAsync(t["officer"].Client.GetAsync($"{Api}/library/iso27001"));
        var access = iso.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "5.15");
        Assert.Equal(("Access control", "التحكم في الوصول"), (access.GetProperty("titles").GetProperty("en").GetString(), access.GetProperty("titles").GetProperty("ar").GetString()));

        // Scenario 1: every control of the set, each with owner, status and due date to fill in.
        var (project, controls) = await ProjectAsync(t, "officer", "iso27001");
        Assert.Equal(93, controls.Length);
        Assert.All(controls, c => Assert.Equal("not_started", c.GetProperty("status").GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComplianceProjectCreated, project.ToString());
        Assert.Equal("validation.compliance.framework_unknown",
            await ProblemKeyAsync(t["officer"].Client.PostAsJsonAsync($"{Api}/projects", new { name = "X", frameworkKey = "pci" }), HttpStatusCode.BadRequest));

        // A tenant framework, and a project from it.
        var own = await OkAsync(t["officer"].Client.PostAsJsonAsync($"{Api}/library", new
        {
            key = "vendor-review", version = "2027", names = new { en = "Vendor review", ar = "مراجعة الموردين" },
            controls = new object[] { new { code = "VR-1", titles = new { en = "Contract signed" } }, new { code = "VR-2", titles = new { en = "Data processing terms" } } },
        }), HttpStatusCode.Created);
        Assert.Equal("tenant.vendor-review", own.GetProperty("key").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComplianceFrameworkChanged, own.GetProperty("id").GetGuid().ToString());
        Assert.Equal("validation.compliance.control_code_taken", await ProblemKeyAsync(t["officer"].Client.PostAsJsonAsync($"{Api}/library", new
        {
            key = "dupes", version = "1", names = new { en = "Dupes" },
            controls = new object[] { new { code = "A", titles = new { en = "One" } }, new { code = "a", titles = new { en = "Two" } } },
        }), HttpStatusCode.BadRequest));
        Assert.Equal(2, (await ProjectAsync(t, "officer", "tenant.vendor-review", name: "Vendor review 2027")).Controls.Length);

        // People without a compliance role neither create nor find projects.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/projects", new { name = "Mine", frameworkKey = "soc2" })));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["laila"].Client.GetAsync($"{Api}/projects/{project}")));
        Assert.Empty((await OkAsync(t["laila"].Client.GetAsync($"{Api}/projects"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["laila"].Client.GetAsync($"{Api}/library")));

        // Another tenant never reaches it; a smaller plan has no compliance projects at all.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other["officer"].Client.GetAsync($"{Api}/projects/{project}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other["officer"].Client.GetAsync($"{Api}/controls/{controls[0].GetProperty("id").GetGuid()}")));
        var plus = await FreshAsync("workplace_plus");
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(plus["officer"].Client.PostAsJsonAsync($"{Api}/projects", new { name = "X", frameworkKey = "soc2" })));
        Assert.False((await OkAsync(plus["officer"].Client.GetAsync($"{Api}/rights"))).GetProperty("enabled").GetBoolean());
        var rights = await OkAsync(t["officer"].Client.GetAsync($"{Api}/rights"));
        Assert.Equal((true, true, true), (rights.GetProperty("enabled").GetBoolean(), rights.GetProperty("canView").GetBoolean(), rights.GetProperty("canManage").GetBoolean()));
    }

    [Fact]
    public async Task Owners_move_their_controls_on_and_only_compliance_officers_review_them()
    {
        var t = await FreshAsync();
        var (project, controls) = await ProjectAsync(t, "officer", "soc2");
        var cc61 = Control(controls, "CC6.1");
        var officer = t["officer"].Client;
        var omar = t["omar"].Client;

        // The officer gives the control an owner, a due date and a document from a library the officer reads.
        var space = await OkAsync(officer.PostAsJsonAsync("/api/v1/spaces", new { name = "SOC 2 evidence", description = "Evidence.", visibility = "private", template = "project" }),
            HttpStatusCode.Created);
        var library = space.GetProperty("libraryId").GetGuid();
        var doc = (await OkAsync(officer.PostAsync($"/api/v1/documents/libraries/{library}/files?name=access-review.txt&publish=true",
            new ByteArrayContent(Encoding.UTF8.GetBytes("Quarterly access review."))), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var due = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var set = await OkAsync(officer.PutAsJsonAsync($"{Api}/controls/{cc61}", new { ownerId = t["omar"].Id, dueOn = due, documentIds = new[] { doc } }));
        Assert.Equal(t["omar"].Id, set.GetProperty("control").GetProperty("owner").GetProperty("userId").GetGuid());
        Assert.True(Assert.Single(set.GetProperty("documents").EnumerateArray()).GetProperty("available").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComplianceControlChanged, cc61.ToString());
        Assert.Equal("validation.compliance.policy_unknown",
            await ProblemKeyAsync(officer.PutAsJsonAsync($"{Api}/controls/{cc61}", new { policyIds = new[] { Guid.NewGuid() } }), HttpStatusCode.BadRequest));
        await world.SeedUserAsync(t, "karim", MembershipTypes.Guest);
        Assert.Equal("validation.compliance.owner_invalid",
            await ProblemKeyAsync(officer.PutAsJsonAsync($"{Api}/controls/{cc61}", new { ownerId = t["karim"].Id }), HttpStatusCode.BadRequest));

        // Omar now sees the project as an owner. The document is not one he may open, so it shows without its name.
        var mine = await OkAsync(omar.GetAsync($"{Api}/controls/{cc61}"));
        var hidden = Assert.Single(mine.GetProperty("documents").EnumerateArray());
        Assert.False(hidden.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("names").ValueKind);
        Assert.Contains((await OkAsync(omar.GetAsync($"{Api}/projects"))).EnumerateArray(), p => p.GetProperty("id").GetGuid() == project);

        // He moves it to "evidence ready" and adds a task, but cannot review it, reassign it or touch other controls.
        var version = mine.GetProperty("control").GetProperty("version").GetInt32();
        await OkAsync(omar.PutAsJsonAsync($"{Api}/controls/{cc61}", new { status = "in_progress", note = "Review scheduled.", version }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComplianceControlStatusChanged, cc61.ToString());
        Assert.Equal("conflict.compliance.changed",
            await ProblemKeyAsync(omar.PutAsJsonAsync($"{Api}/controls/{cc61}", new { status = "evidence_ready", version }), HttpStatusCode.Conflict));
        await OkAsync(omar.PutAsJsonAsync($"{Api}/controls/{cc61}", new { status = "evidence_ready" }));
        Assert.Equal("authz.denied.compliance_review", await ProblemKeyAsync(omar.PutAsJsonAsync($"{Api}/controls/{cc61}", new { status = "reviewed" }), HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(omar.PutAsJsonAsync($"{Api}/controls/{cc61}", new { ownerId = t["laila"].Id })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(omar.PutAsJsonAsync($"{Api}/controls/{Control(controls, "CC6.2")}", new { status = "in_progress" })));
        var withTask = await OkAsync(omar.PostAsJsonAsync($"{Api}/controls/{cc61}/tasks", new { title = "Export admin role changes for the last 12 months" }));
        Assert.Equal(t["omar"].Id, Assert.Single(withTask.GetProperty("tasks").EnumerateArray()).GetProperty("owner").GetProperty("userId").GetGuid());

        // The officer reviews it. Archiving the project freezes it.
        var reviewed = await OkAsync(officer.PutAsJsonAsync($"{Api}/controls/{cc61}", new { status = "reviewed" }));
        Assert.Equal("reviewed", reviewed.GetProperty("control").GetProperty("status").GetString());
        await OkAsync(officer.PutAsJsonAsync($"{Api}/projects/{project}", new { status = "archived" }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComplianceProjectChanged, project.ToString());
        Assert.Equal("conflict.compliance.archived",
            await ProblemKeyAsync(officer.PutAsJsonAsync($"{Api}/controls/{cc61}", new { status = "in_progress" }), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task An_audit_export_attached_as_evidence_keeps_its_reference_and_hash_and_the_readiness_view_counts_it()
    {
        var t = await FreshAsync();
        var (project, controls) = await ProjectAsync(t, "officer", "iso27001");
        var privileged = Control(controls, "8.2");
        var officer = t["officer"].Client;
        var admin = t["admin"].Client;

        await world.CreateUnitAsync(t, "GULF", null, "division");
        await world.AddMembershipAsync(t, "omar", "GULF");
        await OkAsync(officer.PutAsJsonAsync($"{Api}/controls/{privileged}", new { ownerId = t["omar"].Id, status = "in_progress" }));
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var detail = await OkAsync(officer.PostAsJsonAsync($"{Api}/controls/{privileged}/tasks",
            new { title = "Export admin role changes for the last 12 months", ownerId = t["omar"].Id, dueOn = yesterday }));
        var task = detail.GetProperty("tasks")[0].GetProperty("id").GetGuid();

        // Readiness: by status, the overdue task, by owner and by the owner's org unit.
        var readiness = await OkAsync(officer.GetAsync($"{Api}/projects/{project}/readiness"));
        Assert.Equal((93, 92, 1), (readiness.GetProperty("total").GetInt32(), readiness.GetProperty("byStatus").GetProperty("not_started").GetInt32(),
            readiness.GetProperty("byStatus").GetProperty("in_progress").GetInt32()));
        Assert.Equal(task, Assert.Single(readiness.GetProperty("overdueTasks").EnumerateArray()).GetProperty("taskId").GetGuid());
        var byOmar = readiness.GetProperty("byOwner").EnumerateArray().Single(o => o.GetProperty("owner").ValueKind != JsonValueKind.Null);
        Assert.Equal((1, 1), (byOmar.GetProperty("byStatus").GetProperty("in_progress").GetInt32(), byOmar.GetProperty("overdueTasks").GetInt32()));
        Assert.Contains(readiness.GetProperty("byOrgUnit").EnumerateArray(), u => u.ValueKind == JsonValueKind.Object
            && u.GetProperty("orgUnitId").ValueKind != JsonValueKind.Null && u.GetProperty("orgUnitId").GetGuid() == t.Unit("GULF"));

        // An audit export, made by someone who may export the audit trail.
        var accepted = await OkAsync(admin.PostAsJsonAsync("/api/v1/audit/exports", new { format = "csv" }), HttpStatusCode.Accepted);
        var export = accepted.GetProperty("id").GetGuid();
        await world.DispatchTenantAsync(t.Id);
        var original = await (await admin.GetAsync($"/api/v1/audit/exports/{export}/file")).Content.ReadAsByteArrayAsync();
        var sha = Convert.ToHexStringLower(SHA256.HashData(original));

        // Attaching needs audit.export: Omar owns the task but cannot read audit exports.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/tasks/{task}/evidence", new { exportId = export })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["omar"].Client.GetAsync($"{Api}/exports")));
        Assert.Contains((await OkAsync(admin.GetAsync($"{Api}/exports"))).EnumerateArray(), e => e.GetProperty("id").GetGuid() == export);
        Assert.Equal("validation.compliance.export_unknown",
            await ProblemKeyAsync(admin.PostAsJsonAsync($"{Api}/tasks/{task}/evidence", new { exportId = Guid.NewGuid() }), HttpStatusCode.BadRequest));
        var attached = await OkAsync(admin.PostAsJsonAsync($"{Api}/tasks/{task}/evidence", new { exportId = export }));
        var done = attached.GetProperty("tasks")[0];
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("doneAt").ValueKind);
        var evidence = Assert.Single(done.GetProperty("evidence").EnumerateArray());
        Assert.Equal((export, sha, "audit_export"), (evidence.GetProperty("sourceId").GetGuid(), evidence.GetProperty("sha256").GetString(), evidence.GetProperty("kind").GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComplianceEvidenceAttached, evidence.GetProperty("id").GetGuid().ToString());

        // The kept copy is the same file, and it outlives the export.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update audit.export_job set expires_at = now() - interval '1 day' where id = @p", ("p", export));
        var copy = await t["omar"].Client.GetAsync($"{Api}/evidence/{evidence.GetProperty("id").GetGuid()}/file");
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.Equal(sha, copy.Headers.GetValues("X-Content-SHA256").Single());
        Assert.Equal(original, await copy.Content.ReadAsByteArrayAsync());
        Assert.Empty((await OkAsync(officer.GetAsync($"{Api}/projects/{project}/readiness"))).GetProperty("overdueTasks").EnumerateArray());

        // A task with evidence stays.
        Assert.Equal("conflict.compliance.task_has_evidence", await ProblemKeyAsync(officer.DeleteAsync($"{Api}/tasks/{task}"), HttpStatusCode.Conflict));
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other["admin"].Client.GetAsync($"{Api}/evidence/{evidence.GetProperty("id").GetGuid()}/file")));
    }

    [Fact]
    public async Task A_scoped_compliance_officer_sees_and_runs_only_their_subtree()
    {
        var t = await FreshAsync();
        var gulf = await world.CreateUnitAsync(t, "GULF", null, "division");
        var dubai = await world.CreateUnitAsync(t, "DXB", gulf, "department");
        var egypt = await world.CreateUnitAsync(t, "EGY", null, "division");
        await world.AssignAsync(t, "sara", BuiltInRoles.ComplianceOfficer, "org_subtree", gulf);

        var (inDubai, _) = await ProjectAsync(t, "sara", "nca_ecc", dubai, "Dubai ECC review");
        var (inEgypt, _) = await ProjectAsync(t, "officer", "nca_ecc", egypt, "Egypt ECC review");
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["sara"].Client.PostAsJsonAsync($"{Api}/projects", new { name = "X", frameworkKey = "soc2", orgUnitId = egypt })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["sara"].Client.PostAsJsonAsync($"{Api}/projects", new { name = "X", frameworkKey = "soc2" })));

        var listed = (await OkAsync(t["sara"].Client.GetAsync($"{Api}/projects"))).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(inDubai, listed);
        Assert.DoesNotContain(inEgypt, listed);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["sara"].Client.GetAsync($"{Api}/projects/{inEgypt}")));
        // Moving her project out of her subtree is refused.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["sara"].Client.PutAsJsonAsync($"{Api}/projects/{inDubai}", new { orgUnitId = egypt })));
        // The tenant-wide officer sees both.
        var all = (await OkAsync(t["officer"].Client.GetAsync($"{Api}/projects"))).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(inDubai, all);
        Assert.Contains(inEgypt, all);
    }

    [Fact]
    public async Task A_project_shared_to_an_auditor_guest_space_is_read_only_to_its_guests()
    {
        var t = await FreshAsync();
        var officer = t["officer"].Client;
        var (project, controls) = await ProjectAsync(t, "officer", "iso27001");
        var control = Control(controls, "5.1");
        await OkAsync(officer.PostAsJsonAsync($"{Api}/controls/{control}/tasks", new { title = "Approved policy set" }));

        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/spaces/manage/settings", new { creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, version = 0 }));
        var space = (await OkAsync(officer.PostAsJsonAsync("/api/v1/spaces", new { name = "External audit 2027", description = "Audit.", visibility = "private", template = "project" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var lailaSpace = (await OkAsync(t["laila"].Client.PostAsJsonAsync("/api/v1/spaces", new { name = "Laila's space", description = "Hers.", visibility = "private", template = "project" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await world.SeedUserAsync(t, "auditor", MembershipTypes.Guest);
        await OkAsync(officer.PostAsJsonAsync($"/api/v1/spaces/{space}/guests", new { email = t["auditor"].Email }));
        var auditor = t["auditor"].Client;
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(auditor.GetAsync($"{Api}/projects/{project}")));

        // Only a space the officer owns can receive the project.
        Assert.Equal("validation.compliance.space_not_owned",
            await ProblemKeyAsync(officer.PutAsJsonAsync($"{Api}/projects/{project}/share", new { spaceId = lailaSpace }), HttpStatusCode.BadRequest));
        var shared = await OkAsync(officer.PutAsJsonAsync($"{Api}/projects/{project}/share", new { spaceId = space }));
        Assert.Equal("External audit 2027", shared.GetProperty("project").GetProperty("sharedSpace").GetProperty("name").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComplianceProjectShared, project.ToString());

        // The auditor reads controls, status, tasks and readiness, from the list for the space too.
        var seen = await OkAsync(auditor.GetAsync($"{Api}/projects/{project}"));
        Assert.True(seen.GetProperty("project").GetProperty("rights").GetProperty("readOnly").GetBoolean());
        Assert.Equal(93, seen.GetProperty("controls").GetArrayLength());
        Assert.Contains((await OkAsync(auditor.GetAsync($"{Api}/projects?spaceId={space}"))).EnumerateArray(), p => p.GetProperty("id").GetGuid() == project);
        var detail = await OkAsync(auditor.GetAsync($"{Api}/controls/{control}"));
        Assert.False(detail.GetProperty("rights").GetProperty("canChangeStatus").GetBoolean());
        Assert.Single(detail.GetProperty("tasks").EnumerateArray());
        await OkAsync(auditor.GetAsync($"{Api}/projects/{project}/readiness"));

        // ...and changes nothing.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(auditor.PutAsJsonAsync($"{Api}/controls/{control}", new { status = "reviewed" })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(auditor.PostAsJsonAsync($"{Api}/controls/{control}/tasks", new { title = "Mine" })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(auditor.PutAsJsonAsync($"{Api}/projects/{project}", new { name = "Renamed" })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(auditor.GetAsync($"{Api}/library")));

        // Other projects of the tenant stay hidden, and unsharing ends the auditor's access at once.
        var (other, _) = await ProjectAsync(t, "officer", "soc2", name: "Internal only");
        Assert.DoesNotContain((await OkAsync(auditor.GetAsync($"{Api}/projects"))).EnumerateArray(), p => p.GetProperty("id").GetGuid() == other);
        await OkAsync(officer.PutAsJsonAsync($"{Api}/projects/{project}/share", new { spaceId = (Guid?)null }));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(auditor.GetAsync($"{Api}/projects/{project}")));
    }
}
