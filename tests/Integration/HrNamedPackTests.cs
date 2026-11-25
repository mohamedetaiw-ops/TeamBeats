using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Hr;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 019 R3 US8 (FR-019, FR-020): Oracle HCM and SAP SuccessFactors connected by address and sign-in alone. Each pack
/// reads its system's own answer shape (Oracle workers with offset paging, SuccessFactors OData users with next links and
/// /Date()/ dates), includes leavers, keeps one overridden field, reads one record live, and needs hr.named_packs. Both HR
/// systems are fakes behind the connector HTTP client; people and numbers are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class HrNamedPackTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/pack-connections";
    private const string Hr = "/api/v1/hr/integration";

    /// <summary>Oracle HCM workers as the HCM REST API returns them, in pages by offset and limit.</summary>
    private sealed class FakeOracle
    {
        public List<object> Workers { get; } = [];

        public List<string> Asked { get; } = [];

        public HttpResponseMessage Answer(FakeRestApi.Sent sent)
        {
            if (sent.Headers.GetValueOrDefault("Authorization") != "Bearer fictional-hcm-token")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            lock (Asked)
            {
                Asked.Add(sent.Uri.PathAndQuery);
            }
            if (sent.Uri.AbsolutePath != HrOracleHcmPack.Resource)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var query = System.Web.HttpUtility.ParseQueryString(sent.Uri.Query);
            if (query["expand"] != "names,emails,workRelationships.assignments.managers")
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
            if (query["q"] is { } q)
            {
                var id = q["PersonId=".Length..];
                return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = Workers.Where(w => JsonSerializer.SerializeToElement(w).GetProperty("PersonId").GetRawText() == id) }));
            }
            var offset = int.Parse(query["offset"]!, System.Globalization.CultureInfo.InvariantCulture);
            var limit = int.Parse(query["limit"]!, System.Globalization.CultureInfo.InvariantCulture);
            var items = Workers.Skip(offset).Take(limit).ToList();
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items, count = items.Count, hasMore = offset + limit < Workers.Count, offset, limit }));
        }
    }

    private static object Worker(long personId, string number, string email, string status, string title, string department, string? manager,
        string? terminated = null) => new
    {
        PersonId = personId,
        PersonNumber = number,
        names = new[] { new { DisplayName = $"Worker {number}" } },
        emails = new[] { new { EmailType = "W1", EmailAddress = email } },
        workRelationships = new[]
        {
            new
            {
                StartDate = "2023-02-01",
                TerminationDate = terminated,
                WorkerType = "E",
                assignments = new[]
                {
                    new
                    {
                        AssignmentStatusType = status,
                        AssignmentName = title,
                        GradeCode = "G5",
                        LocationCode = "DXB",
                        DepartmentName = department,
                        EffectiveStartDate = "2024-01-01",
                        managers = manager is null ? Array.Empty<object>() : [new { ManagerPersonNumber = manager, ManagerType = "LINE_MANAGER" }],
                    },
                },
            },
        },
    };

    /// <summary>SuccessFactors users as the OData v2 API returns them, two to a page with a next link.</summary>
    private sealed class FakeSuccessFactors(string host)
    {
        public List<object> Users { get; } = [];

        public List<string> Asked { get; } = [];

        public HttpResponseMessage Answer(FakeRestApi.Sent sent)
        {
            if (sent.Headers.GetValueOrDefault("Authorization") != "Bearer fictional-sf-token")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            lock (Asked)
            {
                Asked.Add(Uri.UnescapeDataString(sent.Uri.PathAndQuery));
            }
            var query = System.Web.HttpUtility.ParseQueryString(sent.Uri.Query);
            if (query["$format"] != "json" || query["$expand"] != "manager")
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
            var path = Uri.UnescapeDataString(sent.Uri.AbsolutePath);
            if (path.StartsWith("/odata/v2/User('", StringComparison.Ordinal))
            {
                var id = path["/odata/v2/User('".Length..^2];
                return Users.FirstOrDefault(u => JsonSerializer.SerializeToElement(u).GetProperty("userId").GetString() == id) is { } one
                    ? FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { d = one }))
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (path != "/odata/v2/User")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            // Without the status filter SuccessFactors returns active users only.
            var all = query["$filter"] == "status in 't','f','T','F'";
            var users = Users.Where(u => all || JsonSerializer.SerializeToElement(u).GetProperty("status").GetString() == "t").ToList();
            var skip = int.TryParse(query["$skiptoken"], out var s) ? s : 0;
            var page = users.Skip(skip).Take(2).ToList();
            var next = skip + 2 < users.Count ? $"https://{host}/odata/v2/User?$format=json&$expand=manager{(all ? "&$filter=status%20in%20't','f','T','F'" : "")}&$skiptoken={skip + 2}" : null;
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { d = new Dictionary<string, object?> { ["results"] = page, ["__next"] = next } }));
        }
    }

    private static object SfUser(string userId, string empId, string email, string status, string title, string department, string? manager) => new
    {
        userId,
        empId,
        email,
        displayName = $"User {userId}",
        status,
        hireDate = "/Date(1709251200000)/",
        title,
        department,
        location = "Nairobi",
        division = "CC-9",
        manager = manager is null ? null : new { empId = manager },
    };

    private async Task<TestTenant> TenantAsync(string package, params string[] people)
    {
        var t = await world.CreateTenantAsync("hrn-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var p in people)
        {
            await world.SeedUserAsync(t, p, MembershipTypes.Employee);
        }
        return t;
    }

    private async Task<(string Host, HttpResponseMessage Created)> ConnectAsync(TestTenant t, string kind, string token, Func<string, Func<FakeRestApi.Sent, HttpResponseMessage>> fake,
        object? fields = null)
    {
        var host = $"hcm-{Guid.NewGuid():N}"[..20] + ".hr.test";
        world.Factory.Rest.On(host, fake(host));
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, token), token == "hcm" ? "fictional-hcm-token" : "fictional-sf-token");
        var created = await admin.PostAsJsonAsync(Packs, new
        {
            kind,
            displayName = "HR system",
            settings = new
            {
                baseUrl = $"https://{host}",
                auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, token) },
                timeZone = "Asia/Dubai",
                fields,
                // Paths are the preset's: a tenant value is ignored.
                employeesPath = "/somewhere/else",
            },
        });
        return (host, created);
    }

    private async Task<JsonElement> SyncAsync(TestTenant t, Guid connection)
    {
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Packs}/{connection}:enable", null)).StatusCode);
        var started = await t["admin"].Client.PostAsync($"{Hr}/runs", null);
        Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var ran = await sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 50);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return ran;
        });
        await world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);
        return await (await t["admin"].Client.GetAsync(Hr)).ReadAsync<JsonElement>();
    }

    private async Task<Dictionary<string, (string? Status, string? Title, string? Manager, string? Hire)>> StoredAsync(TestTenant t)
    {
        var text = await world.Database.TenantScalarAsync<string>(t.Id, "select jsonb_object_agg(record_id, data)::text from integrations.hr_employee");
        static string? V(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return JsonDocument.Parse(text!).RootElement.EnumerateObject().ToDictionary(p => p.Name,
            p => (V(p.Value, HrFields.Status), V(p.Value, HrFields.JobTitle), V(p.Value, HrFields.ManagerEmployeeNumber), V(p.Value, HrFields.HireDate)));
    }

    [Fact]
    public async Task Oracle_HCM_workers_sync_with_offset_paging_leavers_included_a_field_overridden_and_one_record_read_live()
    {
        var t = await TenantAsync("enterprise_gov", "sara", "omar");
        var oracle = new FakeOracle();
        oracle.Workers.AddRange(
        [
            Worker(300000001, "E-1", t["sara"].Email, "ACTIVE", "Accountant", "Finance", null),
            Worker(300000002, "E-2", t["omar"].Email, "SUSPENDED", "Buyer", "Procurement", "E-1"),
            Worker(300000003, "E-3", "left@hcm.example", "INACTIVE", "Clerk", "Finance", "E-1", terminated: "2025-06-30"),
        ]);
        var (_, created) = await ConnectAsync(t, HrOracleHcmPack.KindName, "hcm", _ => oracle.Answer,
            fields: new Dictionary<string, string> { ["job_title"] = "$.workRelationships[0].assignments[0].DepartmentName", ["org_unit_code"] = "" });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var view = await created.ReadAsync<JsonElement>();
        var settings = view.GetProperty("settings");
        Assert.Equal(HrOracleHcmPack.Resource + "?onlyData=true&expand=names,emails,workRelationships.assignments.managers", settings.GetProperty("employeesPath").GetString());
        Assert.Equal(("offset", 200), (settings.GetProperty("paging").GetString(), settings.GetProperty("pageSize").GetInt32()));
        Assert.False(settings.GetProperty("fields").TryGetProperty("org_unit_code", out _));

        // Three workers in pages of 200: one page, then the short page ends the read.
        var overview = await SyncAsync(t, view.GetProperty("id").GetGuid());
        var run = overview.GetProperty("lastRun");
        Assert.Equal(("applied", 3, 0), (run.GetProperty("status").GetString(), run.GetProperty("total").GetInt32(), run.GetProperty("refused").GetInt32()));
        Assert.Equal(2, overview.GetProperty("counts").GetProperty("matched").GetInt32());
        Assert.Contains(oracle.Asked, a => a.Contains("offset=0&limit=200", StringComparison.Ordinal));

        var stored = await StoredAsync(t);
        // FR-020: the leaver is read; a suspended assignment is still employment; the overridden field is the tenant's.
        Assert.Equal(("terminated", "Finance"), (stored["300000003"].Status, stored["300000003"].Title));
        Assert.Equal(("active", "Procurement", "E-1"), (stored["300000002"].Status, stored["300000002"].Title, stored["300000002"].Manager));
        Assert.Equal("2023-02-01", stored["300000001"].Hire);

        // One record read now through the preset's single worker address.
        var current = await world.Factory.AsUserAsync(t.Id, t["sara"].Id, sp => sp.GetRequiredService<IHrRecords>().CurrentAsync(t["sara"].Id, CancellationToken.None));
        Assert.Equal((HrRecordRead.Ok, "300000001"), (current.State, current.Record?.RecordId));
        Assert.Contains(oracle.Asked, a => a.Contains("q=PersonId=300000001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessFactors_users_sync_through_next_links_with_inactive_users_and_OData_dates()
    {
        var t = await TenantAsync("enterprise_gov", "layla", "karim");
        FakeSuccessFactors? sf = null;
        var (_, created) = await ConnectAsync(t, HrSuccessFactorsPack.KindName, "sf", host => (sf = new FakeSuccessFactors(host)).Answer);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        sf!.Users.AddRange(
        [
            SfUser("u1", "1001", t["layla"].Email, "t", "Engineer", "ENG", null),
            SfUser("u2", "1002", t["karim"].Email, "t", "Analyst", "ENG", "1001"),
            SfUser("u3", "1003", "gone@sf.example", "f", "Analyst", "ENG", "1001"),
        ]);

        var overview = await SyncAsync(t, (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid());
        var run = overview.GetProperty("lastRun");
        Assert.Equal(("applied", 3, 0), (run.GetProperty("status").GetString(), run.GetProperty("total").GetInt32(), run.GetProperty("refused").GetInt32()));
        // Two pages: the second through the next link SuccessFactors returned.
        Assert.Contains(sf.Asked, a => a.Contains("$skiptoken=2", StringComparison.Ordinal));
        var stored = await StoredAsync(t);
        Assert.Equal(("terminated", "1001"), (stored["u3"].Status, stored["u3"].Manager));
        // /Date(1709251200000)/ is 1 March 2024.
        Assert.Equal("2024-03-01", stored["u1"].Hire);

        var current = await world.Factory.AsUserAsync(t.Id, t["karim"].Id, sp => sp.GetRequiredService<IHrRecords>().CurrentAsync(t["karim"].Id, CancellationToken.None));
        Assert.Equal((HrRecordRead.Ok, "Analyst"), (current.State, current.Record?.Fields.GetValueOrDefault(HrFields.JobTitle)));
    }

    [Fact]
    public async Task Without_hr_named_packs_the_named_packs_are_refused_and_the_generic_pack_stays()
    {
        var t = await TenantAsync("workplace_plus");
        var (_, refused) = await ConnectAsync(t, HrOracleHcmPack.KindName, "hcm", _ => _ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var (_, sf) = await ConnectAsync(t, HrSuccessFactorsPack.KindName, "sf", _ => _ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.Forbidden, sf.StatusCode);
        var packs = await (await t["admin"].Client.GetAsync("/api/v1/integrations/packs")).ReadAsync<JsonElement>();
        var byKind = packs.EnumerateArray().ToDictionary(p => p.GetProperty("kind").GetString()!, p => p.GetProperty("inPackage").GetBoolean());
        Assert.Equal((false, false, true), (byKind[HrOracleHcmPack.KindName], byKind[HrSuccessFactorsPack.KindName], byKind[HrRestPack.KindName]));
    }
}
