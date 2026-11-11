using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Hr;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.People;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 019 T004 (US1, FR-002 to FR-004, FR-014 to FR-016, FR-018): an HR system connected through the HR REST pack
/// or the SFTP pack, matched by work email then employee number, with conflicts never matched, links made by hand kept,
/// HR fields read-only on profiles, and a run over the change threshold held for an HR administrator. The HR system is
/// a fake behind the connector HTTP client; records and names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed partial class HrSyncTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/pack-connections";
    private const string Hr = "/api/v1/hr/integration";
    private static readonly string HostKey = "FakeHostKeyForTests" + new string('1', 24);

    /// <summary>A fake HR system: employees as its API returns them, served in pages of the requested size.</summary>
    private sealed class FakeHr
    {
        public List<Dictionary<string, object?>> Employees { get; set; } = [];

        /// <summary>When true the HR system answers 503, as when it is down.</summary>
        public bool Down { get; set; }

        /// <summary>Payslips by record ID, as payroll reports them.</summary>
        public Dictionary<string, List<Dictionary<string, object?>>> Payslips { get; } = [];

        public byte[]? Pdf { get; set; }

        /// <summary>Leave balances by record ID, as HR reports them (spec 019 US7).</summary>
        public Dictionary<string, List<Dictionary<string, object?>>> Leave { get; } = [];

        /// <summary>Leave sent to HR, with the idempotency key it came with.</summary>
        public List<(JsonElement Body, string? Key)> LeaveSent { get; } = [];

        /// <summary>When set, HR answers a leave submission with this status instead of taking it.</summary>
        public HttpStatusCode? LeaveAnswer { get; set; }

        /// <summary>Every path asked for, so a test can see whose records were read.</summary>
        public List<string> Asked { get; } = [];

        public HttpResponseMessage Answer(FakeRestApi.Sent sent)
        {
            if (sent.Headers.GetValueOrDefault("Authorization") != "Bearer fictional-hr-token")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            if (Down)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            lock (Asked)
            {
                Asked.Add(sent.Uri.AbsolutePath);
            }
            if (sent.Method == "POST" && sent.Uri.AbsolutePath == "/v1/leave-requests")
            {
                if (LeaveAnswer is { } refused)
                {
                    return FakeRestApi.Json(refused, """{"error":"not allowed"}""");
                }
                lock (LeaveSent)
                {
                    LeaveSent.Add((JsonDocument.Parse(sent.Body!).RootElement.Clone(), sent.Headers.GetValueOrDefault("Idempotency-Key")));
                    return FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { @ref = $"HR-LV-{LeaveSent.Count}" }));
                }
            }
            if (sent.Uri.AbsolutePath.StartsWith("/v1/employees/", StringComparison.Ordinal))
            {
                var parts = sent.Uri.AbsolutePath["/v1/employees/".Length..].Split('/').Select(Uri.UnescapeDataString).ToArray();
                return parts switch
                {
                    [var id] => Employees.FirstOrDefault(e => (string?)e["id"] == id) is { } one
                        ? FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = one }))
                        : new HttpResponseMessage(HttpStatusCode.NotFound),
                    [var id, "leave"] => FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = Leave.GetValueOrDefault(id) ?? [] })),
                    [var id, "payslips"] => FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = Payslips.GetValueOrDefault(id) ?? [] })),
                    [var id, "payslips", var period, "pdf"] when Pdf is not null && Payslips.GetValueOrDefault(id)?.Any(p => (string?)p["month"] == period) == true =>
                        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pdf) { Headers = { ContentType = new("application/pdf") } } },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }
            var query = sent.Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
            var size = query.GetValueOrDefault("size", 200);
            var page = query.GetValueOrDefault("page", 1);
            var items = Employees.Skip((page - 1) * size).Take(size).ToList();
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new { items } }));
        }
    }

    private static Dictionary<string, object?> Person(string id, string? email, string? number = null, string status = "Active", string title = "Accountant",
        string dept = "FIN", string? manager = null, string grade = "G7") => new()
    {
        ["id"] = id, ["email"] = email, ["number"] = number, ["name"] = $"Person {id}", ["nameAr"] = $"شخص {id}", ["state"] = status, ["hired"] = "2024-03-01",
        ["job"] = new Dictionary<string, object?> { ["title"] = title, ["grade"] = grade }, ["costCentre"] = "CC-100", ["site"] = "Riyadh HQ", ["dept"] = dept,
        ["manager"] = manager, ["type"] = "Permanent",
    };

    private static readonly Dictionary<string, string> RestFields = new()
    {
        ["record_id"] = "$.id", ["work_email"] = "$.email", ["employee_number"] = "$.number", ["name_en"] = "$.name", ["name_ar"] = "$.nameAr", ["status"] = "$.state",
        ["hire_date"] = "$.hired", ["job_title"] = "$.job.title", ["grade"] = "$.job.grade", ["cost_centre"] = "$.costCentre", ["location"] = "$.site",
        ["org_unit_code"] = "$.dept", ["manager_employee_number"] = "$.manager", ["employment_type"] = "$.type", ["termination_date"] = "$.left",
        ["assignment_date"] = "$.since",
    };

    private static object StatusValues => new Dictionary<string, string[]> { ["active"] = ["Active"], ["terminated"] = ["Terminated", "Left"] };

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus", params string[] people)
    {
        var t = await world.CreateTenantAsync("hr-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var p in people)
        {
            await world.SeedUserAsync(t, p, MembershipTypes.Employee);
        }
        return t;
    }

    private async Task<(Guid Id, FakeHr Hr)> RestConnectionAsync(TestTenant t, bool payslips = false, bool leave = false)
    {
        var host = $"hr-{Guid.NewGuid():N}"[..20] + ".hr.test";
        var fake = new FakeHr();
        world.Factory.Rest.On(host, fake.Answer);
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "hr-token"), "fictional-hr-token");
        var created = await admin.PostAsJsonAsync(Packs, new
        {
            kind = HrRestPack.KindName,
            displayName = "People system",
            settings = new
            {
                baseUrl = $"https://{host}/v1",
                auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "hr-token") },
                employeesPath = "/employees",
                recordsPath = "$.data.items",
                employeePath = "/employees/{record_id}",
                employeeRecordPath = "$.data",
                payslipsPath = payslips ? "/employees/{record_id}/payslips" : null,
                payslipRecordsPath = payslips ? "$.items" : null,
                payslipFields = payslips
                    ? new Dictionary<string, string> { ["period"] = "$.month", ["gross"] = "$.gross", ["deductions"] = "$.deducted", ["net"] = "$.net", ["currency"] = "$.cur" }
                    : null,
                payslipPdfPath = payslips ? "/employees/{record_id}/payslips/{period}/pdf" : null,
                leaveBalancesPath = leave ? "/employees/{record_id}/leave" : null,
                leaveBalanceRecordsPath = leave ? "$.items" : null,
                leaveBalanceFields = leave ? new Dictionary<string, string> { ["type"] = "$.code", ["balance"] = "$.left" } : null,
                leaveSubmitPath = leave ? "/leave-requests" : null,
                leaveReferencePath = leave ? "$.ref" : null,
                leaveIdempotencyHeader = leave ? "Idempotency-Key" : null,
                leaveTypeCodes = leave ? new Dictionary<string, string> { ["annual"] = "AL" } : null,
                paging = "page",
                pageSizeParam = "size",
                pageSize = 2,
                fields = RestFields,
                statusValues = StatusValues,
                timeZone = "Asia/Riyadh",
            },
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{id}:enable", null)).StatusCode);
        return (id, fake);
    }

    private async Task<JsonElement> SyncAsync(TestTenant t)
    {
        var started = await t["admin"].Client.PostAsync($"{Hr}/runs", null);
        Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var ran = await sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 50);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return ran;
        });
        await DispatchAsync();
        return await OverviewAsync(t);
    }

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private static async Task<JsonElement> OverviewAsync(TestTenant t) => await (await t["admin"].Client.GetAsync(Hr)).ReadAsync<JsonElement>();

    private static async Task<JsonElement[]> RecordsAsync(TestTenant t, string state) =>
        (await (await t["admin"].Client.GetAsync($"{Hr}/records?state={state}")).ReadAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToArray();

    private static async Task<Dictionary<string, JsonElement>> OwnFieldsAsync(TestUser user) =>
        (await (await user.Client.GetAsync("/api/v1/me/profile")).ReadAsync<JsonElement>()).GetProperty("fields").EnumerateArray()
        .ToDictionary(f => f.GetProperty("key").GetString()!);

    private async Task NumberAsync(TestTenant t, string user, string number)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ProfileStore>().RefreshAsync(t[user].Id, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "with u as (update people.profile set directory_values = jsonb_build_object('employeeNumber', @n::text) where user_id = @u returning 1) select count(*) from u",
            ("n", number), ("u", t[user].Id)));
    }

    [Fact]
    public async Task The_first_sync_matches_by_email_then_number_lists_conflicts_and_unmatched_and_keeps_hand_made_links()
    {
        var other = await TenantAsync(people: "nadia");
        var t = await TenantAsync(people: ["sara", "omar", "layla"]);
        await NumberAsync(t, "omar", "E-200");
        var (_, hr) = await RestConnectionAsync(t);
        hr.Employees =
        [
            Person("R1", t["sara"].Email.ToUpperInvariant(), "E-100"),
            Person("R2", "omar.previous@elsewhere.example", "E-200", title: "Buyer"),
            Person("R3", "shared@hr.example", "E-300"),
            Person("R4", "shared@hr.example", "E-301"),
            // Same email as a user of another tenant: never a match here.
            Person("R5", other["nadia"].Email, "E-400"),
            Person("R6", "x@hr.example", "E-500", status: "On sabbatical"),
            Person("R7", "loop1@hr.example", "E-700", manager: "E-701"),
            Person("R8", "loop2@hr.example", "E-701", manager: "E-700"),
            Person("R9", "nobody@hr.example", "E-900", manager: "E-100"),
        ];

        var overview = await SyncAsync(t);
        var counts = overview.GetProperty("counts");
        Assert.Equal((2, 2, 2), (counts.GetProperty("matched").GetInt32(), counts.GetProperty("unmatched").GetInt32(), counts.GetProperty("conflicts").GetInt32()));
        var run = overview.GetProperty("lastRun");
        Assert.Equal("applied", run.GetProperty("status").GetString());
        Assert.Equal((6, 3), (run.GetProperty("total").GetInt32(), run.GetProperty("refused").GetInt32()));
        var problems = run.GetProperty("problems").EnumerateArray().Select(p => (p.GetProperty("recordId").GetString(), p.GetProperty("reason").GetString())).ToHashSet();
        Assert.Equal([("R6", "bad_status"), ("R7", "manager_loop"), ("R8", "manager_loop")], problems.OrderBy(p => p.Item1).ToArray());

        var matched = await RecordsAsync(t, "matched");
        Assert.Equal([("R1", "work_email", t["sara"].Id), ("R2", "employee_number", t["omar"].Id)],
            matched.Select(m => (m.GetProperty("recordId").GetString(), m.GetProperty("matchRule").GetString(), m.GetProperty("userId").GetGuid())).ToArray());
        Assert.Equal(new[] { "R3", "R4" }, (await RecordsAsync(t, "conflict")).Select(r => r.GetProperty("recordId").GetString()!).ToArray());
        Assert.Equal(new[] { "R5", "R9" }, (await RecordsAsync(t, "unmatched")).Select(r => r.GetProperty("recordId").GetString()!).ToArray());
        // FR-018: each applied record carries the run that applied it.
        Assert.All(matched, m => Assert.Equal(run.GetProperty("id").GetGuid(), m.GetProperty("lastRunId").GetGuid()));

        // US1 scenario 4: mapped fields come from HR, labelled so, and the person cannot change them.
        var sara = await OwnFieldsAsync(t["sara"]);
        Assert.Equal(("Accountant", "hr", false), (sara["jobTitle"].GetProperty("value").GetString(), sara["jobTitle"].GetProperty("source").GetString(),
            sara["jobTitle"].GetProperty("editable").GetBoolean()));
        Assert.Equal(("E-100", "G7", "CC-100", "Riyadh HQ"), (sara["employeeNumber"].GetProperty("value").GetString(), sara["grade"].GetProperty("value").GetString(),
            sara["costCentre"].GetProperty("value").GetString(), sara["office"].GetProperty("value").GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PeopleFieldSourceChanged, "jobTitle");
        var edit = await t["sara"].Client.PutAsJsonAsync("/api/v1/me/profile", new { values = new Dictionary<string, string> { ["jobTitle"] = "Chief" } });
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
        Assert.Contains("validation.people.read_only", await edit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("Buyer", (await OwnFieldsAsync(t["omar"]))["jobTitle"].GetProperty("value").GetString());

        // Scenario 3: a hand-made link is kept by later syncs and audited; another tenant's person cannot be linked.
        var linked = await t["admin"].Client.PostAsJsonAsync($"{Hr}/records/R9:link", new { userId = t["layla"].Id });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrMatchLinked, "R9");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await t["admin"].Client.PostAsJsonAsync($"{Hr}/records/R5:link", new { userId = other["nadia"].Id })).StatusCode);
        var taken = await t["admin"].Client.PostAsJsonAsync($"{Hr}/records/R5:link", new { userId = t["sara"].Id });
        Assert.Contains("validation.hr.user_linked", await taken.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An unlinked record stays unlinked, even though its email still matches.
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Hr}/records/R1:unlink", null)).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrMatchUnlinked, "R1");
        overview = await SyncAsync(t);
        matched = await RecordsAsync(t, "matched");
        Assert.Equal([("R2", "employee_number"), ("R9", "manual")],
            matched.Select(m => (m.GetProperty("recordId").GetString(), m.GetProperty("matchRule").GetString())).ToArray());
        Assert.Equal(t["layla"].Id, matched[1].GetProperty("userId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await OwnFieldsAsync(t["sara"]))["grade"].GetProperty("value").ValueKind);
        Assert.Equal("Accountant", (await OwnFieldsAsync(t["layla"]))["jobTitle"].GetProperty("value").GetString());

        // The other tenant has nothing of it.
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{Hr}/records/R2:link", new { userId = other["nadia"].Id })).StatusCode);
        Assert.Equal(0, (await OverviewAsync(other)).GetProperty("counts").GetProperty("matched").GetInt32());
    }

    [Fact]
    public async Task A_run_over_the_change_threshold_is_held_until_an_hr_administrator_confirms_or_discards_it()
    {
        var names = Enumerable.Range(0, 6).Select(i => $"p{i}").ToArray();
        var t = await TenantAsync(people: names);
        var (_, hr) = await RestConnectionAsync(t);
        hr.Employees = names.Select((n, i) => Person($"R{i}", t[n].Email, $"E-{i}")).ToList();
        Assert.Equal(6, (await SyncAsync(t)).GetProperty("counts").GetProperty("matched").GetInt32());

        // One leaver of six is 17%, over the default 10%: nothing is applied.
        hr.Employees[0] = Person("R0", t["p0"].Email, "E-0", status: "Left");
        var held = await SyncAsync(t);
        var run = held.GetProperty("heldRun");
        Assert.Equal(("held", 1, 0, 6), (run.GetProperty("status").GetString(), run.GetProperty("terminations").GetInt32(), run.GetProperty("moves").GetInt32(),
            run.GetProperty("activeBefore").GetInt32()));
        Assert.Equal(1L, await world.Database.OutboxCountAsync(nameof(HrSyncHeld), t.Id));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrSyncHeld, run.GetProperty("id").GetString());
        Assert.Equal("active", await world.Database.TenantScalarAsync<string>(t.Id, "select data->>'status' from integrations.hr_employee where record_id = 'R0'"));

        // A newer read replaces the held run; discarding it applies nothing.
        hr.Employees[1] = Person("R1", t["p1"].Email, "E-1", dept: "OPS");
        var again = (await SyncAsync(t)).GetProperty("heldRun");
        Assert.Equal((1, 1), (again.GetProperty("terminations").GetInt32(), again.GetProperty("moves").GetInt32()));
        Assert.Equal("superseded", await world.Database.TenantScalarAsync<string>(t.Id, "select status from integrations.hr_run where id = @id", ("id", run.GetProperty("id").GetGuid())));
        var discarded = await t["admin"].Client.PostAsync($"{Hr}/runs/{again.GetProperty("id").GetGuid()}:discard", null);
        Assert.Equal("discarded", (await discarded.ReadAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("FIN", await world.Database.TenantScalarAsync<string>(t.Id, "select data->>'org_unit_code' from integrations.hr_employee where record_id = 'R1'"));
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsync($"{Hr}/runs/{again.GetProperty("id").GetGuid()}:confirm", null)).StatusCode);

        // Confirmed, the run applies.
        var third = (await SyncAsync(t)).GetProperty("heldRun").GetProperty("id").GetGuid();
        var confirmed = await t["admin"].Client.PostAsync($"{Hr}/runs/{third}:confirm", null);
        Assert.Equal("confirmed", (await confirmed.ReadAsync<JsonElement>()).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrSyncConfirmed, third.ToString());
        Assert.Equal("terminated", await world.Database.TenantScalarAsync<string>(t.Id, "select data->>'status' from integrations.hr_employee where record_id = 'R0'"));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from integrations.hr_incoming"));

        // The threshold is the tenant's: at 50% the same kind of run applies at once.
        var overview = await t["admin"].Client.GetAsync(Hr);
        var tag = overview.Headers.ETag!.Tag;
        var stale = new HttpRequestMessage(HttpMethod.Put, $"{Hr}/settings") { Content = JsonContent.Create(new { changeThresholdPercent = 50 }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"v999\"");
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.SendAsync(stale)).StatusCode);
        var change = new HttpRequestMessage(HttpMethod.Put, $"{Hr}/settings") { Content = JsonContent.Create(new { changeThresholdPercent = 50, matchRule = new[] { "employee_number" } }) };
        change.Headers.TryAddWithoutValidation("If-Match", tag);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.SendAsync(change)).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrSettingsChanged);
        hr.Employees[2] = Person("R2", t["p2"].Email, "E-2", status: "Terminated");
        var applied = await SyncAsync(t);
        Assert.Equal(JsonValueKind.Null, applied.GetProperty("heldRun").ValueKind);
        Assert.Equal("applied", applied.GetProperty("lastRun").GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_sftp_pack_reads_the_newest_export_with_mapped_columns()
    {
        var t = await TenantAsync(people: ["sara", "omar"]);
        var host = $"sftp-{Guid.NewGuid():N}"[..20] + ".hr.test";
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "hr-sftp"), "fictional-sftp-password");
        var server = world.Factory.Sftp.On(host, new FakeSftp.Server { HostKeySha256 = HostKey, Username = "teambeats", Secret = "fictional-sftp-password" });
        server.Folders["/hr"] = new();
        var now = DateTimeOffset.UtcNow;
        server.Put("/hr", "employees-old.csv", $"Id;Mail;Status;Joined;Title\nS1;{t["sara"].Email};A;01/02/2020;Old title\n", now.AddDays(-1));
        server.Put("/hr", "employees-new.csv",
            $"Id;Mail;Status;Joined;Title\nS1;{t["sara"].Email};A;01/02/2020;Engineer\nS2;\"{t["omar"].Email}\";T;15/06/2019;Driver\nS3;nobody@hr.example;A;31/02/2020;Clerk\n",
            now);
        var created = await admin.PostAsJsonAsync(Packs, new
        {
            kind = HrSftpPack.KindName,
            displayName = "HR export",
            settings = new
            {
                sftp = new { host, username = "teambeats", secretRef = SecretReferences.For(t.Id, "hr-sftp"), hostKeySha256 = HostKey, directory = "/hr", filePattern = "employees-*.csv" },
                delimiter = ";",
                fields = new Dictionary<string, string> { ["record_id"] = "Id", ["work_email"] = "Mail", ["status"] = "Status", ["hire_date"] = "Joined", ["job_title"] = "Title" },
                statusValues = new Dictionary<string, string[]> { ["active"] = ["A"], ["terminated"] = ["T"] },
                dateFormat = "dd/MM/yyyy",
            },
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var test = await (await admin.PostAsync($"{Packs}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Contains("integrations.test.hr_file_found", test.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{id}:enable", null)).StatusCode);

        var overview = await SyncAsync(t);
        Assert.Equal(2, overview.GetProperty("counts").GetProperty("matched").GetInt32());
        var problem = Assert.Single(overview.GetProperty("lastRun").GetProperty("problems").EnumerateArray());
        Assert.Equal(("S3", "bad_date"), (problem.GetProperty("recordId").GetString(), problem.GetProperty("reason").GetString()));
        var sara = await OwnFieldsAsync(t["sara"]);
        Assert.Equal(("Engineer", "2020-02-01"), (sara["jobTitle"].GetProperty("value").GetString(), sara["hireDate"].GetProperty("value").GetString()));
        Assert.Equal("terminated", await world.Database.TenantScalarAsync<string>(t.Id, "select data->>'status' from integrations.hr_employee where record_id = 'S2'"));
        // The server is only read.
        Assert.Equal(2, server.Folders["/hr"].Count);
    }

    [Fact]
    public async Task Settings_permissions_the_package_and_the_tenant_boundary_hold()
    {
        var t = await TenantAsync(people: "sara");
        var admin = t["admin"].Client;
        var host = $"hr-{Guid.NewGuid():N}"[..20] + ".hr.test";
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        var bad = await admin.PostAsJsonAsync(Packs, new
        {
            kind = HrRestPack.KindName,
            displayName = "Bad",
            settings = new
            {
                baseUrl = $"https://{host}",
                auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "hr-token") },
                employeesPath = "employees",
                paging = "next",
                fields = new Dictionary<string, string> { ["work_email"] = "$.email", ["salary"] = "$.pay", ["job_title"] = "$.email" },
                statusValues = new Dictionary<string, string[]> { ["active"] = ["Active"] },
                syncAt = "25:00",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadAsStringAsync();
        foreach (var key in new[] { "validation.hr.employees_path", "validation.hr.record_path", "validation.hr.field_source", "validation.hr.fields_required",
                     "validation.hr.field_twice", "validation.hr.status_values", "validation.hr.sync_at" })
        {
            Assert.Contains(key, body, StringComparison.Ordinal);
        }

        await RestConnectionAsync(t);
        var second = await admin.PostAsJsonAsync(Packs, new { kind = HrSftpPack.KindName, displayName = "Second", settings = new { } });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("conflict.hr.one_connection", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // HR integration management needs hr.integration_manage: an employee has none of it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync(Hr)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PostAsync($"{Hr}/runs", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PostAsJsonAsync($"{Hr}/records/R1:link", new { userId = t["sara"].Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"{Hr}/records?state=everything")).StatusCode);

        // Workplace has no HR integration.
        var workplace = await TenantAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await workplace["admin"].Client.GetAsync(Hr)).StatusCode);
        var refused = await workplace["admin"].Client.PostAsJsonAsync(Packs, new { kind = HrRestPack.KindName, displayName = "HR", settings = new { } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Empty(await world.Factory.InScopeAsync(workplace.Id, sp => sp.GetRequiredService<IHrRecords>().MatchedAsync()));
    }

    [Fact]
    public void Records_with_a_manager_loop_or_a_duplicate_id_are_refused_and_the_rest_kept()
    {
        static HrEmployee E(string id, string number, string? manager) =>
            new(id, number, null, null, null, HrEmployeeStatuses.Active, null, null, null, null, null, null, null, null, manager);
        var read = HrMapping.Check([E("A", "1", "2"), E("B", "2", "3"), E("C", "3", "1"), E("D", "4", "1"), E("D", "5", null), E("F", "6", "4")], []);
        Assert.Equal(["F"], read.Employees.Select(e => e.RecordId).ToArray());
        Assert.Equal([("A", "manager_loop"), ("B", "manager_loop"), ("C", "manager_loop"), ("D", "duplicate_id")],
            read.Problems.Select(p => (p.RecordId, p.Reason)).OrderBy(p => p.RecordId).ToArray());
    }
}
