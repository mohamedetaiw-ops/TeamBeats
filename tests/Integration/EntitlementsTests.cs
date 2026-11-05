using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 005 entitlement core.</summary>
[Collection(WorldCollection.Name)]
public sealed class EntitlementsTests(TenantWorld world)
{
    [Fact]
    public async Task Delegated_admin_role_is_not_in_package_for_workplace_tenant()
    {
        var response = await world.B["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "user", subjectId = world.B["orgadmin"].Id, roleKey = BuiltInRoles.OrganizationAdministrator, scopeType = "org_subtree", scopeId = world.B.Unit("Gulf") });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entitlement.not_in_package", problem.GetProperty("code").GetString());
        Assert.Equal(Capabilities.OrgDelegatedAdmin, problem.GetProperty("capability").GetString());
    }

    [Fact]
    public async Task Tenant_sees_its_own_plan_and_employee_is_denied()
    {
        var plan = await world.A["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/entitlements");
        Assert.Equal("workplace_plus", plan.GetProperty("packageKey").GetString());
        Assert.True(plan.GetProperty("capabilities").GetProperty(Capabilities.AiAssistant).GetProperty("enabled").GetBoolean());

        var planB = await world.B["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/entitlements");
        Assert.Equal("workplace", planB.GetProperty("packageKey").GetString());

        var denied = await world.A["employee_gulf"].Client.GetAsync("/api/v1/tenant/entitlements");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Package_version_without_ai_assistant_is_rejected()
    {
        var response = await world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace/versions", new
        {
            capabilities = new Dictionary<string, object> { [Capabilities.OrgHierarchy] = new { enabled = true } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Database_rejects_publishing_a_version_without_ai_even_if_api_is_bypassed()
    {
        var id = Guid.NewGuid();
        await world.Database.OwnerExecAsync("insert into entitlements.package_version (id, package_key, version, status) values (@id, 'workplace', 900, 'draft')", ("id", id));
        await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
            world.Database.OwnerExecAsync("update entitlements.package_version set status = 'published', published_at = now() where id = @id", ("id", id)));
    }

    [Fact]
    public async Task New_version_publish_leaves_existing_tenants_on_their_version_until_moved()
    {
        var tenant = await world.CreateTenantAsync("ent-move", "workplace");
        var before = await tenant["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/entitlements");
        var oldVersion = before.GetProperty("version").GetInt32();

        var draft = await (await world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace/versions", new
        {
            capabilities = new Dictionary<string, object>
            {
                [Capabilities.AiAssistant] = new { enabled = true },
                [Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 9999 },
                [Capabilities.OrgHierarchy] = new { enabled = true },
            },
        })).ReadAsync<JsonElement>();
        var newVersion = draft.GetProperty("version").GetInt32();
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace/versions/{newVersion}:publish", null)).EnsureSuccessStatusCode();
        Assert.True(await world.Database.OwnerScalarAsync<long>("select count(*) from audit.platform_audit_event where action = @a", ("a", AuditActions.PackagePublished)) > 0);

        var still = await tenant["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/entitlements");
        Assert.Equal(oldVersion, still.GetProperty("version").GetInt32());

        (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription", new { packageKey = "workplace", version = newVersion }))
            .EnsureSuccessStatusCode();
        var moved = await tenant["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/entitlements");
        Assert.Equal(newVersion, moved.GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(tenant.Id, AuditActions.SubscriptionChanged);
        Assert.Equal(1, await world.Database.PlatformAuditCountAsync(AuditActions.SubscriptionChanged, tenant.Id));
    }

    [Fact]
    public async Task Tenant_admin_cannot_call_platform_package_endpoints()
    {
        var response = await world.A["admin"].Client.GetAsync("/api/v1/platform/packages");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
