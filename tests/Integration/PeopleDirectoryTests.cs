using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Identity;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 010 T004 and T006: profiles and the reporting line from the directory, people search in both languages,
/// field visibility in the API and in search, own profile edits, the manual manager, the org chart, My team, the
/// policy, newcomers and locations, with cross-tenant denial and audit.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PeopleDirectoryTests(TenantWorld world)
{
    private static string Oid() => Guid.NewGuid().ToString("D");

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    private static async Task<string[]> SearchAsync(HttpClient client, string q) =>
        (await OkAsync(await client.GetAsync($"/api/v1/people?q={Uri.EscapeDataString(q)}"))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("displayName").GetString()!).ToArray();

    private static async Task<Dictionary<string, string>> FieldsAsync(HttpClient client, Guid person) =>
        (await OkAsync(await client.GetAsync($"/api/v1/people/{person}"))).GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("key").GetString()!, f => f.GetProperty("value").GetString()!);

    private static Task<HttpResponseMessage> PutPolicyAsync(HttpClient admin, object body, string? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/people/policy") { Content = JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        return admin.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SetManagerAsync(HttpClient admin, Guid person, Guid? manager) =>
        admin.PutAsJsonAsync($"/api/v1/people/{person}/manager", new { managerId = manager });

    /// <summary>A signed-in client for a user that directory sync created.</summary>
    private async Task<TestUser> UserAsync(TestTenant tenant, string name, Guid userId)
    {
        var token = await world.Factory.InScopeAsync(tenant.Id, async sp =>
        {
            var value = await sp.GetRequiredService<SessionService>().CreateUserSessionAsync(userId, "test", CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return value;
        });
        var user = new TestUser(name, userId, $"{name}@{tenant.Key}.example", token, world.Factory.TenantClient(tenant.Key, token));
        tenant.Users[name] = user;
        return user;
    }

    private async Task<Guid> LinkedUserAsync(SyncedTenant s, string oid) =>
        await world.Factory.InScopeAsync(s.Id, sp => Sql.ScalarAsync<Guid>(sp.GetRequiredService<IDbSession>(),
            "select user_id from identity.directory_user_link where directory_id = @d", new { d = oid }));

    private Task<long> CountAsync(Guid tenant, string sql) => world.Database.CountInTenantAsync(tenant, sql);

    /// <summary>
    /// A directory tenant: Mona manages Omar and Sara; Sara manages Layla. Titles, departments, phones and offices come
    /// from the directory. Units: Holding, Gulf (Omar, Sara, Layla, Mona) and Africa (Kofi).
    /// </summary>
    private async Task<(SyncedTenant S, Dictionary<string, string> Oids)> DirectoryTenantAsync()
    {
        var s = await DirectoryTestSetup.CreateAsync(world, "ppl");
        var oids = new[] { "mona", "omar", "sara", "layla", "kofi" }.ToDictionary(n => n, _ => Oid());
        s.Data.AddUser(oids["mona"], "Mona Haddad", s.Email("mona"), jobTitle: "Finance director");
        s.Data.AddUser(oids["omar"], "Mohamed Omar", s.Email("omar"), jobTitle: "Travel claims officer");
        s.Data.AddUser(oids["sara"], "سارة أحمد", s.Email("sara"), jobTitle: "Accountant");
        s.Data.AddUser(oids["layla"], "Layla Nasser", s.Email("layla"), jobTitle: "Payroll analyst");
        s.Data.AddUser(oids["kofi"], "Kofi Mensah", s.Email("kofi"), jobTitle: "Engineer");
        s.Data.SetOrg(oids["omar"], "Acme Gulf", "Finance");
        s.Data.SetProperties(oids["omar"], new Dictionary<string, object?>
        {
            ["businessPhones"] = new[] { "+971 4 555 7321" }, ["mobilePhone"] = "+971 50 111 2222", ["officeLocation"] = "Dubai HQ",
            ["employeeId"] = "E-1001", ["employeeHireDate"] = DateTimeOffset.UtcNow.AddDays(-5).ToString("O"),
        });
        s.Data.SetProperties(oids["sara"], new Dictionary<string, object?> { ["officeLocation"] = "dubai  hq ", ["mobilePhone"] = "+971 50 333 4444" });
        s.Data.SetProperties(oids["kofi"], new Dictionary<string, object?> { ["officeLocation"] = "Accra", ["employeeHireDate"] = "2019-01-01T00:00:00Z" });
        s.Data.SetManager(oids["omar"], oids["mona"]);
        s.Data.SetManager(oids["sara"], oids["mona"]);
        s.Data.SetManager(oids["layla"], oids["sara"]);
        await DirectoryTestSetup.SyncAsync(world, s);

        var holding = await world.CreateUnitAsync(s.Tenant, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(s.Tenant, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(s.Tenant, "GulfFinance", gulf, "department");
        await world.CreateUnitAsync(s.Tenant, "Africa", holding, "subsidiary");
        foreach (var (name, oid) in oids)
        {
            await UserAsync(s.Tenant, name, await LinkedUserAsync(s, oid));
        }
        foreach (var name in new[] { "mona", "omar", "sara", "layla" })
        {
            await world.AddMembershipAsync(s.Tenant, name, "GulfFinance");
        }
        await world.AddMembershipAsync(s.Tenant, "kofi", "Africa");
        return (s, oids);
    }

    [Fact]
    public async Task Directory_sync_sets_profiles_managers_and_history_and_ignores_a_manager_who_is_not_a_member()
    {
        var (s, oids) = await DirectoryTenantAsync();
        var t = s.Tenant;
        var admin = s.Admin;

        var omar = await OkAsync(await admin.GetAsync($"/api/v1/people/{t["omar"].Id}"));
        Assert.Equal("Mona Haddad", omar.GetProperty("manager").GetProperty("displayName").GetString());
        Assert.Equal("dubai hq", omar.GetProperty("location").GetProperty("key").GetString());
        var fields = await FieldsAsync(admin, t["omar"].Id);
        Assert.Equal("Travel claims officer", fields["jobTitle"]);
        Assert.Equal("+971 4 555 7321", fields["workPhone"]);
        Assert.Equal("+971 50 111 2222", fields["mobilePhone"]);
        Assert.Equal("E-1001", fields["employeeNumber"]);
        Assert.Equal(1, await CountAsync(s.Id, $"select count(*) from people.reporting_line where user_id = '{t["omar"].Id}' and manager_user_id = '{t["mona"].Id}' and valid_to is null and source = 'directory'"));
        Assert.True(await world.Database.OutboxCountAsync(nameof(ReportingLineChanged), s.Id) >= 3);
        Assert.True(await world.Database.OutboxCountAsync(nameof(LocationAdded), s.Id) >= 2);

        // "Dubai HQ" and "dubai  hq " are one location (US5 scenario 2).
        var locations = await OkAsync(await admin.GetAsync("/api/v1/people/locations"));
        Assert.Equal(2, locations.GetArrayLength());
        Assert.Equal(2, locations.EnumerateArray().Single(l => l.GetProperty("key").GetString() == "dubai hq").GetProperty("people").GetInt32());

        // A manager who is not a member, then no manager at all: the line is cleared with its date (edge cases).
        s.Data.SetManager(oids["layla"], Oid());
        s.Data.SetManager(oids["omar"], null);
        await DirectoryTestSetup.SyncAsync(world, s);
        Assert.Equal(JsonValueKind.Null, (await OkAsync(await admin.GetAsync($"/api/v1/people/{t["layla"].Id}"))).GetProperty("manager").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await OkAsync(await admin.GetAsync($"/api/v1/people/{t["omar"].Id}"))).GetProperty("manager").ValueKind);
        Assert.Equal(2, await CountAsync(s.Id, $"select count(*) from people.reporting_line where user_id = '{t["omar"].Id}'"));
        Assert.Equal(1, await CountAsync(s.Id, $"select count(*) from people.reporting_line where user_id = '{t["omar"].Id}' and valid_to is not null"));

        // The manager is never set by hand while the directory owns it.
        var conflict = await SetManagerAsync(admin, t["omar"].Id, t["mona"].Id);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task A_reporting_cycle_keeps_the_previous_lines_and_is_reported()
    {
        var (s, oids) = await DirectoryTenantAsync();
        var t = s.Tenant;
        s.Data.SetManager(oids["mona"], oids["layla"]); // Mona -> Layla -> Sara -> Mona
        await DirectoryTestSetup.SyncAsync(world, s);

        Assert.Equal(JsonValueKind.Null, (await OkAsync(await s.Admin.GetAsync($"/api/v1/people/{t["mona"].Id}"))).GetProperty("manager").ValueKind);
        Assert.Equal("Mona Haddad", (await OkAsync(await s.Admin.GetAsync($"/api/v1/people/{t["sara"].Id}"))).GetProperty("manager").GetProperty("displayName").GetString());
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.PeopleReportingCycleDetected, s.Connection.ToString());
        // Omar is outside the cycle and keeps working normally.
        Assert.Equal(1, await CountAsync(s.Id, $"select count(*) from people.reporting_line where user_id = '{t["omar"].Id}' and valid_to is null and manager_user_id = '{t["mona"].Id}'"));
    }

    [Fact]
    public async Task Search_matches_name_title_department_extension_and_email_with_Arabic_normalization_and_transliteration()
    {
        var (s, _) = await DirectoryTenantAsync();
        var employee = s.Tenant["kofi"].Client;

        Assert.Equal(["Mohamed Omar"], await SearchAsync(employee, "travel claims"));
        Assert.Equal(["Mohamed Omar"], await SearchAsync(employee, "Mohammed"));
        Assert.Equal(["Mohamed Omar"], await SearchAsync(employee, "muhammad"));
        Assert.Equal(["Mohamed Omar"], await SearchAsync(employee, "7321"));
        Assert.Equal(["Mohamed Omar"], await SearchAsync(employee, "finance travel"));
        Assert.Equal(["Layla Nasser"], await SearchAsync(employee, $"layla@{s.Key}"));
        // Arabic with diacritics and a hamza variant finds the plain spelling.
        Assert.Equal(["سارة أحمد"], await SearchAsync(employee, "سَارة احمد"));
        // A match at the start of a word ranks before one inside a word.
        Assert.Equal("Layla Nasser", (await SearchAsync(employee, "na"))[0]);

        var page = await OkAsync(await employee.GetAsync("/api/v1/people?limit=2"));
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page.GetProperty("next").GetInt32());
        var byUnit = await OkAsync(await employee.GetAsync($"/api/v1/people?unitId={s.Tenant.Unit("Africa")}"));
        Assert.Equal(["Kofi Mensah"], byUnit.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("displayName").GetString()!).ToArray());
        var byLocation = await OkAsync(await employee.GetAsync("/api/v1/people?location=Dubai%20HQ"));
        Assert.Equal(2, byLocation.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Visibility_rules_apply_in_profiles_and_search()
    {
        var (s, _) = await DirectoryTenantAsync();
        var t = s.Tenant;
        await world.AssignAsync(t, "kofi", BuiltInRoles.HrOfficer, "org_subtree", t.Unit("Africa"));
        var omar = t["omar"].Id;

        // Mobile phone: managers and HR in scope. Mona (manager) and Omar see it; Sara (peer) and Kofi (HR elsewhere) do not.
        Assert.Contains("mobilePhone", (await FieldsAsync(t["mona"].Client, omar)).Keys);
        Assert.Contains("mobilePhone", (await FieldsAsync(t["omar"].Client, omar)).Keys);
        Assert.DoesNotContain("mobilePhone", (await FieldsAsync(t["sara"].Client, omar)).Keys);
        Assert.DoesNotContain("mobilePhone", (await FieldsAsync(t["kofi"].Client, omar)).Keys);
        Assert.DoesNotContain("employeeNumber", (await FieldsAsync(t["mona"].Client, omar)).Keys);
        Assert.Empty(await SearchAsync(t["sara"].Client, "1112222"));

        // Same subtree: Gulf colleagues see the work phone, Africa does not, and it no longer matches searches from there.
        var policy = await OkAsync(await s.Admin.GetAsync("/api/v1/people/policy"));
        var saved = await PutPolicyAsync(s.Admin, new { fields = new[] { new { key = "workPhone", source = "directory", visibility = "subtree" } } },
            policy.GetProperty("version").GetInt32() is var v ? $"\"v{v}\"" : null);
        await OkAsync(saved);
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.PeopleVisibilityChanged, "workPhone");
        Assert.Contains("workPhone", (await FieldsAsync(t["sara"].Client, omar)).Keys);
        Assert.DoesNotContain("workPhone", (await FieldsAsync(t["kofi"].Client, omar)).Keys);
        Assert.Empty(await SearchAsync(t["kofi"].Client, "7321"));

        // Only the person and administrators: the manager loses the mobile phone too.
        await OkAsync(await PutPolicyAsync(s.Admin, new { fields = new[] { new { key = "mobilePhone", source = "directory", visibility = "private" } } }));
        Assert.DoesNotContain("mobilePhone", (await FieldsAsync(t["mona"].Client, omar)).Keys);
        Assert.Contains("mobilePhone", (await FieldsAsync(s.Admin, omar)).Keys);

        // Other modules and agent tools get the same answer through the contract.
        var viaContract = await world.Factory.InScopeAsync(s.Id, sp => sp.GetRequiredService<IProfiles>().ViewAsync(t["sara"].Id, [omar]));
        Assert.Null(viaContract.Single().Field("mobilePhone"));
        Assert.Equal("Travel claims officer", viaContract.Single().Field("jobTitle"));
    }

    [Fact]
    public async Task Org_chart_and_my_team_follow_the_reporting_line()
    {
        var (s, _) = await DirectoryTenantAsync();
        var t = s.Tenant;

        var chart = await OkAsync(await t["kofi"].Client.GetAsync($"/api/v1/people/{t["layla"].Id}/org-chart"));
        Assert.Equal(["سارة أحمد", "Mona Haddad"], chart.GetProperty("chain").EnumerateArray().Select(c => c.GetProperty("displayName").GetString()!).ToArray());
        Assert.Equal(0, chart.GetProperty("peerCount").GetInt32());
        var sara = await OkAsync(await t["kofi"].Client.GetAsync($"/api/v1/people/{t["sara"].Id}/org-chart"));
        Assert.Equal(["Mohamed Omar"], sara.GetProperty("peers").EnumerateArray().Select(c => c.GetProperty("displayName").GetString()!).ToArray());
        Assert.Equal(1, sara.GetProperty("reportCount").GetInt32());

        var direct = await OkAsync(await t["mona"].Client.GetAsync("/api/v1/me/team"));
        Assert.Equal(2, direct.GetProperty("total").GetInt32());
        var all = await OkAsync(await t["mona"].Client.GetAsync("/api/v1/me/team?indirect=true"));
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        var layla = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == t["layla"].Id);
        Assert.Equal(2, layla.GetProperty("level").GetInt32());
        // Managers see manager-visible fields of their reports.
        var omar = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == t["omar"].Id);
        Assert.Contains(omar.GetProperty("fields").EnumerateArray(), f => f.GetProperty("key").GetString() == "mobilePhone");
        Assert.Equal(0, (await OkAsync(await t["kofi"].Client.GetAsync("/api/v1/me/team"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Disabled_members_are_not_found_in_search_and_their_profile_is_no_longer_available()
    {
        var (s, oids) = await DirectoryTenantAsync();
        s.Data.UpdateUser(oids["layla"], enabled: false);
        await DirectoryTestSetup.SyncAsync(world, s);

        Assert.Empty(await SearchAsync(s.Admin, "layla"));
        var gone = await s.Admin.GetAsync($"/api/v1/people/{s.Tenant["layla"].Id}");
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        var body = await gone.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Payroll", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task People_of_another_tenant_are_never_found()
    {
        var (s, _) = await DirectoryTenantAsync();
        var other = await world.CreateTenantAsync("ppx-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var outsider = other["admin"].Client;

        Assert.Empty(await SearchAsync(outsider, s.Tenant["omar"].Email));
        Assert.Empty(await SearchAsync(outsider, "Mohamed"));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/people/{s.Tenant["omar"].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/people/{s.Tenant["omar"].Id}/org-chart")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync($"/api/v1/people/{s.Tenant["omar"].Id}/manager", new { managerId = (Guid?)null })).StatusCode);
    }

    [Fact]
    public async Task Employees_edit_their_own_editable_fields_and_directory_fields_stay_read_only()
    {
        var (s, _) = await DirectoryTenantAsync();
        var t = s.Tenant;
        await OkAsync(await PutPolicyAsync(s.Admin, new { fields = new[] { new { key = "mobilePhone", source = "person", visibility = "managers_hr" } } }));
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.PeopleFieldSourceChanged, "mobilePhone");

        var sara = t["sara"].Client;
        var own = await OkAsync(await sara.GetAsync("/api/v1/me/profile"));
        Assert.True(own.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "mobilePhone").GetProperty("editable").GetBoolean());
        Assert.False(own.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "jobTitle").GetProperty("editable").GetBoolean());

        await OkAsync(await sara.PutAsJsonAsync("/api/v1/me/profile", new { values = new Dictionary<string, string> { ["mobilePhone"] = "+971 55 999 0000", ["timeZone"] = "Asia/Dubai" } }));
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.PeopleProfileUpdated, t["sara"].Id.ToString());
        Assert.True(await world.Database.OutboxCountAsync(nameof(ProfileUpdated), s.Id) >= 1);
        Assert.Equal("+971 55 999 0000", (await FieldsAsync(t["mona"].Client, t["sara"].Id))["mobilePhone"]);
        Assert.DoesNotContain("mobilePhone", (await FieldsAsync(t["omar"].Client, t["sara"].Id)).Keys);

        Assert.Contains("validation.people.read_only", await ErrorKeysAsync(await sara.PutAsJsonAsync("/api/v1/me/profile", new { values = new Dictionary<string, string> { ["jobTitle"] = "Chief" } })));
        Assert.Contains("validation.people.time_zone", await ErrorKeysAsync(await sara.PutAsJsonAsync("/api/v1/me/profile", new { values = new Dictionary<string, string> { ["timeZone"] = "Mars/Base" } })));
        Assert.Contains("validation.people.field_unknown", await ErrorKeysAsync(await sara.PutAsJsonAsync("/api/v1/me/profile", new { values = new Dictionary<string, string> { ["manager"] = t["omar"].Id.ToString() } })));
    }

    [Fact]
    public async Task Without_directory_sync_an_administrator_sets_managers_by_hand_with_history_and_audit()
    {
        var t = await world.CreateTenantAsync("ppm-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var lead = await world.SeedUserAsync(t, "lead", MembershipTypes.Employee);
        var member = await world.SeedUserAsync(t, "member", MembershipTypes.Employee);
        var admin = t["admin"].Client;

        await OkAsync(await SetManagerAsync(admin, member.Id, lead.Id));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PeopleManagerSetManually, member.Id.ToString());
        Assert.Equal(1, (await OkAsync(await lead.Client.GetAsync("/api/v1/me/team"))).GetProperty("total").GetInt32());
        var line = await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IReportingLine>().ChainAsync(member.Id, 5));
        Assert.Equal([lead.Id], line);

        Assert.Contains("validation.people.manager_cycle", await ErrorKeysAsync(await SetManagerAsync(admin, lead.Id, member.Id)));
        Assert.Contains("validation.people.manager_cycle", await ErrorKeysAsync(await SetManagerAsync(admin, lead.Id, lead.Id)));
        Assert.Contains("validation.people.manager_unknown", await ErrorKeysAsync(await SetManagerAsync(admin, lead.Id, Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.Forbidden, (await SetManagerAsync(member.Client, lead.Id, null)).StatusCode);

        await OkAsync(await SetManagerAsync(admin, member.Id, null));
        Assert.Equal(2, await CountAsync(t.Id, $"select count(*) from people.reporting_line where user_id = '{member.Id}'"));
    }

    [Fact]
    public async Task The_policy_is_for_administrators_custom_fields_need_the_capability_and_stale_versions_conflict()
    {
        var basic = await world.CreateTenantAsync("ppp-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var employee = await world.SeedUserAsync(basic, "employee", MembershipTypes.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.GetAsync("/api/v1/people/policy")).StatusCode);
        var custom = new { fields = new[] { new { key = "custom.badge_name", source = "person", visibility = "everyone", names = new { en = "Badge name", ar = "اسم البطاقة" } } } };
        var denied = await PutPolicyAsync(basic["admin"].Client, custom);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("entitlement.not_in_package", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var plus = await world.CreateTenantAsync("ppq-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var admin = plus["admin"].Client;
        var view = await OkAsync(await PutPolicyAsync(admin, custom));
        Assert.Contains(view.GetProperty("fields").EnumerateArray(), f => f.GetProperty("key").GetString() == "custom.badge_name" && f.GetProperty("custom").GetBoolean());
        var member = await world.SeedUserAsync(plus, "member", MembershipTypes.Employee);
        await OkAsync(await member.Client.PutAsJsonAsync("/api/v1/me/profile", new { values = new Dictionary<string, string> { ["custom.badge_name"] = "Sam" } }));
        Assert.Equal("Sam", (await FieldsAsync(admin, member.Id))["custom.badge_name"]);

        var stale = await PutPolicyAsync(admin, new { newcomerDays = 14 }, "\"v1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("validation.people.newcomer_days", await ErrorKeysAsync(await PutPolicyAsync(admin, new { newcomerDays = 3 })));
        Assert.Contains("validation.people.source", await ErrorKeysAsync(await PutPolicyAsync(admin, new { fields = new[] { new { key = "timeZone", source = "directory", visibility = "everyone" } } })));
        Assert.Contains("validation.people.field_unknown", await ErrorKeysAsync(await PutPolicyAsync(admin, new { fields = new[] { new { key = "salary", source = "admin", visibility = "private" } } })));
        await OkAsync(await PutPolicyAsync(admin, new { newcomerDays = 14, allowNewcomerOptOut = true }));
        await world.Database.AssertAuditedAsync(plus.Id, AuditActions.PeopleSettingsChanged);
    }

    [Fact]
    public async Task Newcomers_list_recent_hires_newest_first_and_respect_the_opt_out()
    {
        var (s, _) = await DirectoryTenantAsync();
        var t = s.Tenant;
        var newcomers = await OkAsync(await t["kofi"].Client.GetAsync("/api/v1/people/newcomers"));
        var names = newcomers.EnumerateArray().Select(n => n.GetProperty("displayName").GetString()).ToList();
        Assert.Contains("Mohamed Omar", names);
        Assert.DoesNotContain("Kofi Mensah", names); // hired in 2019

        // Opting out has no effect until the tenant allows it.
        await OkAsync(await t["omar"].Client.PutAsJsonAsync("/api/v1/me/profile", new { newcomerOptOut = true }));
        Assert.Contains("Mohamed Omar", (await OkAsync(await t["kofi"].Client.GetAsync("/api/v1/people/newcomers"))).EnumerateArray().Select(n => n.GetProperty("displayName").GetString()));
        await OkAsync(await PutPolicyAsync(s.Admin, new { allowNewcomerOptOut = true }));
        await OkAsync(await t["omar"].Client.PutAsJsonAsync("/api/v1/me/profile", new { newcomerOptOut = true }));
        Assert.DoesNotContain("Mohamed Omar", (await OkAsync(await t["kofi"].Client.GetAsync("/api/v1/people/newcomers"))).EnumerateArray().Select(n => n.GetProperty("displayName").GetString()));
        Assert.Equal(["Mohamed Omar"], await SearchAsync(t["kofi"].Client, "travel")); // still findable
    }

    [Fact]
    public async Task Renaming_a_location_keeps_its_key()
    {
        var (s, _) = await DirectoryTenantAsync();
        var renamed = await OkAsync(await s.Admin.PutAsJsonAsync("/api/v1/people/locations/dubai%20hq", new { names = new { en = "Dubai head office", ar = "المقر الرئيسي في دبي" } }));
        Assert.Equal("dubai hq", renamed.GetProperty("key").GetString());
        Assert.Equal("Dubai head office", renamed.GetProperty("names").GetProperty("en").GetString());
        var omar = await OkAsync(await s.Tenant["kofi"].Client.GetAsync($"/api/v1/people/{s.Tenant["omar"].Id}"));
        Assert.Equal("المقر الرئيسي في دبي", omar.GetProperty("location").GetProperty("names").GetProperty("ar").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Tenant["kofi"].Client.PutAsJsonAsync("/api/v1/people/locations/dubai%20hq", new { names = new { en = "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.PutAsJsonAsync("/api/v1/people/locations/nowhere", new { names = new { en = "x" } })).StatusCode);
    }

    [Fact]
    public async Task HR_officers_in_scope_correct_administrator_managed_fields_and_localized_names()
    {
        var (s, _) = await DirectoryTenantAsync();
        var t = s.Tenant;
        await world.AssignAsync(t, "kofi", BuiltInRoles.HrOfficer, "org_subtree", t.Unit("Africa"));
        await OkAsync(await PutPolicyAsync(s.Admin, new { fields = new[] { new { key = "office", source = "admin", visibility = "everyone" } } }));

        await OkAsync(await s.Admin.PutAsJsonAsync($"/api/v1/people/{t["omar"].Id}/fields", new { values = new { office = "Tower 2, floor 9" }, names = new { ar = "محمد عمر" } }));
        Assert.Equal("Tower 2, floor 9", (await FieldsAsync(t["sara"].Client, t["omar"].Id))["office"]);
        Assert.Equal(["Mohamed Omar"], await SearchAsync(t["sara"].Client, "محمد"));

        // Kofi is HR for Africa only.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.PutAsJsonAsync($"/api/v1/people/{t["omar"].Id}/fields", new { values = new { office = "x" } })).StatusCode);
        await OkAsync(await t["kofi"].Client.PutAsJsonAsync($"/api/v1/people/{t["kofi"].Id}/fields", new { values = new { office = "Accra campus" } }));
        Assert.Contains("validation.people.read_only", await ErrorKeysAsync(await s.Admin.PutAsJsonAsync($"/api/v1/people/{t["omar"].Id}/fields", new { values = new { jobTitle = "x" } })));
    }
}
