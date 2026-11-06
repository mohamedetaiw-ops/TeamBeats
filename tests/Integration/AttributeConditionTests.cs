using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Authorization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 004 User Story 7: attribute conditions on role assignments and resource permissions (FR-014 to FR-016).</summary>
[Collection(WorldCollection.Name)]
public sealed class AttributeConditionTests(TenantWorld world)
{
    private static object GradeAtLeast(int grade) => new { attribute = "job_grade", @operator = "gte", value = grade };

    private async Task<JsonElement> Explain(TestTenant t, string user, string permission, Guid? unit = null, Guid? doc = null)
    {
        var url = $"/api/v1/authz/explain?userId={t[user].Id}&permission={permission}";
        if (unit is { } u)
        {
            url += $"&orgUnitId={u}";
        }
        if (doc is { } d)
        {
            url += $"&resourceType={TestDocuments.ResourceType}&resourceId={d}";
        }
        return await t["admin"].Client.GetFromJsonAsync<JsonElement>(url);
    }

    private async Task<TestUser> UserAsync(TestTenant t, string name, string membershipType = "employee", string? unit = null, int? grade = null)
    {
        var user = await world.SeedUserAsync(t, name, membershipType);
        if (unit is not null)
        {
            await world.AddMembershipAsync(t, name, unit);
        }
        if (grade is { } g)
        {
            world.Factory.UserAttributes.Set(t.Id, user.Id, ConditionAttributes.JobGrade, g.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return user;
    }

    private static Task<HttpResponseMessage> AssignAsync(TestTenant t, Guid userId, string role, string scopeType, Guid? scopeId, object? condition) =>
        t["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "user", subjectId = userId, roleKey = role, scopeType, scopeId, condition });

    private void ExpireGrantCaches() =>
        ((MemoryCache)world.Factory.Services.GetRequiredService<IMemoryCache>()).Compact(1.0);

    [Fact]
    public async Task US7_grade_condition_allows_senior_denies_junior_and_explains_both()
    {
        var t = world.A;
        var senior = await UserAsync(t, "cond_senior", unit: "Gulf", grade: 8);
        var junior = await UserAsync(t, "cond_junior", unit: "Gulf", grade: 5);
        foreach (var user in new[] { senior, junior })
        {
            var response = await AssignAsync(t, user.Id, BuiltInRoles.Approver, "org_subtree", t.Unit("Gulf"), GradeAtLeast(7));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var view = await response.ReadAsync<JsonElement>();
            Assert.Equal("gte", view.GetProperty("condition").GetProperty("operator").GetString());
            await world.Database.AssertAuditedAsync(t.Id, AuditActions.RoleAssigned, view.GetProperty("id").GetGuid().ToString());
        }

        // Scenario 1: allowed, and the explanation names the role, the scope and the condition with the value used.
        var allowed = await Explain(t, "cond_senior", Permissions.WorkflowApprove, t.Unit("GulfFinance"));
        Assert.True(allowed.GetProperty("allowed").GetBoolean());
        var reason = allowed.GetProperty("reasons").EnumerateArray().Single(r => r.GetProperty("roleKey").GetString() == BuiltInRoles.Approver);
        Assert.Equal(t.Unit("Gulf"), reason.GetProperty("scopeId").GetGuid());
        var clause = reason.GetProperty("condition").GetProperty("clauses")[0];
        Assert.Equal("job_grade", clause.GetProperty("attribute").GetString());
        Assert.Equal("8", clause.GetProperty("actual").GetString());
        Assert.Equal("7", clause.GetProperty("expected")[0].GetString());
        Assert.True(clause.GetProperty("met").GetBoolean());

        // Scenario 2: denied, and the explanation says the condition was not met.
        var denied = await Explain(t, "cond_junior", Permissions.WorkflowApprove, t.Unit("GulfFinance"));
        Assert.False(denied.GetProperty("allowed").GetBoolean());
        var unmet = denied.GetProperty("notApplied").EnumerateArray().Single();
        Assert.Equal(BuiltInRoles.Approver, unmet.GetProperty("roleKey").GetString());
        Assert.False(unmet.GetProperty("condition").GetProperty("met").GetBoolean());
        Assert.Equal("5", unmet.GetProperty("condition").GetProperty("clauses")[0].GetProperty("actual").GetString());

        // Scope still bounds the grant: the condition holding never reaches outside the subtree.
        Assert.False((await Explain(t, "cond_senior", Permissions.WorkflowApprove, t.Unit("Africa"))).GetProperty("allowed").GetBoolean());

        // Scenario 6: a grade change from the directory shows once the cached grants expire (at most 60 s, FR-008).
        Assert.True(EffectiveGrantsLoader.MaxStaleness <= TimeSpan.FromSeconds(60));
        world.Factory.UserAttributes.Set(t.Id, junior.Id, ConditionAttributes.JobGrade, "7");
        ExpireGrantCaches();
        Assert.True((await Explain(t, "cond_junior", Permissions.WorkflowApprove, t.Unit("GulfFinance"))).GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task US7_missing_attribute_fails_closed_and_conditions_never_create_access()
    {
        var t = world.A;
        var ungraded = await UserAsync(t, "cond_ungraded", unit: "Gulf");
        (await AssignAsync(t, ungraded.Id, BuiltInRoles.Approver, "org_subtree", t.Unit("Gulf"),
            new { any = new object[] { GradeAtLeast(7), new { attribute = "job_grade", @operator = "lte", value = 3 } } })).EnsureSuccessStatusCode();

        // Scenario 4: no job grade synced, so the condition is false and the grant does not apply.
        var denied = await Explain(t, "cond_ungraded", Permissions.WorkflowApprove, t.Unit("Gulf"));
        Assert.False(denied.GetProperty("allowed").GetBoolean());
        var trace = denied.GetProperty("notApplied")[0].GetProperty("condition");
        Assert.Equal("job_grade", trace.GetProperty("missingAttributes")[0].GetString());
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("clauses")[0].GetProperty("actual").ValueKind);

        // Scenario 5: no grant at all, however well the attributes would match.
        await UserAsync(t, "cond_nogrant", unit: "Gulf", grade: 12);
        var none = await Explain(t, "cond_nogrant", Permissions.WorkflowApprove, t.Unit("Gulf"));
        Assert.False(none.GetProperty("allowed").GetBoolean());
        Assert.Empty(none.GetProperty("notApplied").EnumerateArray());
    }

    [Fact]
    public async Task US7_share_with_employee_condition_hides_resource_from_contractor()
    {
        var t = world.A;
        var admin = t["admin"].Client;
        var group = await (await admin.PostAsJsonAsync("/api/v1/authz/groups", new { key = "cond-finance", name = "Finance" })).ReadAsync<JsonElement>();
        var groupId = group.GetProperty("id").GetGuid();
        var employee = await UserAsync(t, "cond_fin_employee");
        var contractor = await UserAsync(t, "cond_fin_contractor", MembershipTypes.Contractor);
        foreach (var user in new[] { employee, contractor })
        {
            (await admin.PostAsJsonAsync($"/api/v1/authz/groups/{groupId}/members", new { userId = user.Id })).EnsureSuccessStatusCode();
        }

        var library = world.Factory.Documents.Add(t.Id, t.Unit("GulfFinance"), ResourceVisibility.Restricted, "payroll library");
        (await admin.PutAsJsonAsync($"/api/v1/authz/resources/{TestDocuments.ResourceType}/{library}/acl", new[]
        {
            new { subjectType = "group", subjectId = groupId, permission = Permissions.ContentView, condition = (object)new { attribute = "membership_type", @operator = "eq", value = "employee" } },
        })).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ResourcePermissionChanged, library.ToString());

        var acl = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/authz/resources/{TestDocuments.ResourceType}/{library}/acl");
        Assert.Equal("membership_type", acl[0].GetProperty("condition").GetProperty("attribute").GetString());

        Assert.True((await Explain(t, "cond_fin_employee", Permissions.ContentView, doc: library)).GetProperty("allowed").GetBoolean());
        Assert.False((await Explain(t, "cond_fin_contractor", Permissions.ContentView, doc: library)).GetProperty("allowed").GetBoolean());

        // Scenario 3: the contractor gets not-found semantics, and batch filtering drops the library for them only.
        var descriptor = new ResourceDescriptor(TestDocuments.ResourceType, library, t.Unit("GulfFinance"), ResourceVisibility.Restricted);
        var error = await Assert.ThrowsAsync<AppException>(() => AsUser(t, contractor.Id, authz =>
            authz.DemandVisibleAsync(Permissions.ContentView, AuthzTarget.Resource(descriptor))));
        Assert.Equal(404, error.Status);
        Assert.Empty(await AsUser(t, contractor.Id, authz => authz.FilterAsync(Permissions.ContentView, [descriptor])));
        Assert.Single(await AsUser(t, employee.Id, authz => authz.FilterAsync(Permissions.ContentView, [descriptor])));
    }

    [Fact]
    public async Task Resource_classification_condition_applies_per_resource_and_never_widens_org_coverage()
    {
        var t = world.A;
        var reader = await UserAsync(t, "cond_internal_reader");
        (await AssignAsync(t, reader.Id, BuiltInRoles.ContentPublisher, "tenant", null,
            new { attribute = "classification", @operator = "in", value = new[] { "public", "internal" } })).EnsureSuccessStatusCode();

        var internalDoc = world.Factory.Documents.Add(t.Id, t.Unit("Africa"), ResourceVisibility.Restricted, "handbook", classification: "internal");
        var confidential = world.Factory.Documents.Add(t.Id, t.Unit("Africa"), ResourceVisibility.Restricted, "board minutes", classification: "confidential");
        var unclassified = world.Factory.Documents.Add(t.Id, t.Unit("Africa"), ResourceVisibility.Restricted, "draft");

        Assert.True((await Explain(t, "cond_internal_reader", Permissions.ContentPublish, doc: internalDoc)).GetProperty("allowed").GetBoolean());
        Assert.False((await Explain(t, "cond_internal_reader", Permissions.ContentPublish, doc: confidential)).GetProperty("allowed").GetBoolean());
        Assert.False((await Explain(t, "cond_internal_reader", Permissions.ContentPublish, doc: unclassified)).GetProperty("allowed").GetBoolean());

        // Without a resource the classification is absent, so the grant gives no org or tenant coverage.
        Assert.False((await Explain(t, "cond_internal_reader", Permissions.ContentPublish, t.Unit("Africa"))).GetProperty("allowed").GetBoolean());
        Assert.False(await AsUser(t, reader.Id, authz => authz.HoldsAnywhereAsync(Permissions.ContentPublish)));
        Assert.False((await AsUser(t, reader.Id, authz => authz.CoverageAsync(Permissions.ContentPublish))).Any);
    }

    [Fact]
    public async Task Conditional_assigner_cannot_assign_beyond_what_their_condition_allows()
    {
        var t = world.A;
        var junior = await UserAsync(t, "cond_junior_admin", unit: "Gulf", grade: 4);
        (await AssignAsync(t, junior.Id, BuiltInRoles.OrganizationAdministrator, "org_subtree", t.Unit("Gulf"), GradeAtLeast(6))).EnsureSuccessStatusCode();
        var target = await UserAsync(t, "cond_target", unit: "Gulf");

        var response = await t["cond_junior_admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "user", subjectId = target.Id, roleKey = BuiltInRoles.Approver, scopeType = "org_unit", scopeId = t.Unit("GulfFinance") });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"attribute":"favourite_colour","operator":"eq","value":"blue"}""", "validation.condition.unknown_attribute")]
    [InlineData("""{"attribute":"job_title","operator":"gte","value":"manager"}""", "validation.condition.operator_not_allowed")]
    [InlineData("""{"attribute":"job_grade","operator":"matches","value":7}""", "validation.condition.unknown_operator")]
    [InlineData("""{"attribute":"job_grade","operator":"gte","value":"7; drop table"}""", "validation.condition.value_type")]
    [InlineData("""{"attribute":"membership_type","operator":"eq","value":"vendor"}""", "validation.condition.value_type")]
    [InlineData("""{"attribute":"membership_type","operator":"in","value":"employee"}""", "validation.condition.value_type")]
    [InlineData("""{"attribute":"job_grade","operator":"gte","value":7,"script":"return true"}""", "validation.condition.shape")]
    [InlineData("""{"expression":"user.job_grade >= 7"}""", "validation.condition.shape")]
    [InlineData("\"job_grade >= 7\"", "validation.condition.shape")]
    [InlineData("""{"all":[]}""", "validation.condition.group_size")]
    [InlineData("""{"all":[{"any":[{"all":[{"attribute":"job_grade","operator":"gte","value":7}]}]}]}""", "validation.condition.too_deep")]
    public async Task Conditions_outside_the_schema_are_rejected(string condition, string expectedKey)
    {
        var t = world.A;
        var response = await t["admin"].Client.PostAsync("/api/v1/authz/assignments", new StringContent(
            $$"""{"subjectType":"user","subjectId":"{{t["employee_gulf"].Id}}","roleKey":"approver","scopeType":"org_unit","scopeId":"{{t.Unit("Gulf")}}","condition":{{condition}}}""",
            System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedKey, problem.GetProperty("errors").GetProperty("condition")[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task Two_level_condition_is_accepted_and_stored_canonically()
    {
        var t = world.A;
        var user = await UserAsync(t, "cond_two_level", unit: "Gulf", grade: 9);
        var response = await AssignAsync(t, user.Id, BuiltInRoles.Approver, "org_unit", t.Unit("Gulf"), new
        {
            all = new object[]
            {
                new { attribute = "membership_type", @operator = "eq", value = "Employee" },
                new { any = new object[] { GradeAtLeast(8), new { attribute = "job_title", @operator = "in", value = new[] { "Finance Manager" } } } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = (await response.ReadAsync<JsonElement>()).GetProperty("condition");
        Assert.Equal("employee", stored.GetProperty("all")[0].GetProperty("value").GetString());
        // Every attribute the condition names must be present, even one in an "any" branch that is not needed (fail closed).
        Assert.False((await Explain(t, "cond_two_level", Permissions.WorkflowApprove, t.Unit("Gulf"))).GetProperty("allowed").GetBoolean());
        world.Factory.UserAttributes.Set(t.Id, user.Id, ConditionAttributes.JobTitle, "Analyst");
        ExpireGrantCaches();
        var explained = await Explain(t, "cond_two_level", Permissions.WorkflowApprove, t.Unit("Gulf"));
        Assert.True(explained.GetProperty("allowed").GetBoolean(), explained.ToString());
    }

    [Fact]
    public async Task Condition_cannot_reference_another_tenants_unit_or_attributes()
    {
        var a = world.A;
        var response = await AssignAsync(a, a["employee_gulf"].Id, BuiltInRoles.Approver, "org_unit", a.Unit("Gulf"),
            new { attribute = "owning_org_unit", @operator = "eq", value = world.B.Unit("Gulf") });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Attributes are per tenant: a grade set for the user's twin in tenant B does nothing in tenant A.
        var twinA = await UserAsync(a, "cond_twin", unit: "Gulf");
        var twinB = await UserAsync(world.B, "cond_twin", unit: "Gulf", grade: 10);
        (await AssignAsync(a, twinA.Id, BuiltInRoles.Approver, "org_unit", a.Unit("Gulf"), GradeAtLeast(7))).EnsureSuccessStatusCode();
        Assert.False((await Explain(a, "cond_twin", Permissions.WorkflowApprove, a.Unit("Gulf"))).GetProperty("allowed").GetBoolean());
        Assert.NotEqual(twinA.Id, twinB.Id);
    }

    private async Task<T> AsUser<T>(TestTenant t, Guid userId, Func<IAuthorizationService, Task<T>> action)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        ctx.SetTenant(t.Id);
        ctx.SetActor(ActorType.User, userId);
        return await action(scope.ServiceProvider.GetRequiredService<IAuthorizationService>());
    }

    private async Task AsUser(TestTenant t, Guid userId, Func<IAuthorizationService, Task> action) =>
        await AsUser<bool>(t, userId, async authz => { await action(authz); return true; });
}
