using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 006 User Story 5 (R1): the Connections area with the Microsoft 365 connection.</summary>
[Collection(WorldCollection.Name)]
public sealed class ConnectionsTests(TenantWorld world)
{
    private static readonly string[] DirectoryRoles = ["User.Read.All", "Group.Read.All", "AdministrativeUnit.Read.All"];

    private FakeMicrosoftGraph Graph => world.Factory.Graph;

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus") =>
        await world.CreateTenantAsync("conn-" + Guid.NewGuid().ToString("N")[..8], package);

    private static async Task<JsonElement> CreateAsync(TestTenant t, Guid directory, params string[] features)
    {
        var response = await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith(name + "=", StringComparison.Ordinal)).Select(v => v[(name.Length + 1)..]).FirstOrDefault()
            : null;

    /// <summary>Admin consent round trip: start on the tenant host, Microsoft redirects to the platform host, which routes back.</summary>
    private async Task<string> ConsentAsync(TestTenant t, Guid connection, Guid directory, string feature, bool granted = true, string? binding = null,
        Guid? answeringDirectory = null)
    {
        var start = await t["admin"].Client.GetAsync($"/api/v1/integrations/connections/{connection}/features/{feature}/consent");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var url = start.Headers.Location!;
        Assert.StartsWith($"https://login.microsoftonline.com/{directory:D}/v2.0/adminconsent?", url.ToString(), StringComparison.Ordinal);
        var query = FakeIdentityProvider.ParseQuery(url.Query);
        Assert.Equal($"https://platform.{ApiFactory.BaseDomain}/api/v1/integrations/consent/callback", query["redirect_uri"]);

        var back = $"?admin_consent={(granted ? "True" : "False")}&tenant={answeringDirectory ?? directory:D}&state={Uri.EscapeDataString(query["state"])}"
            + (granted ? string.Empty : "&error=access_denied");
        var relay = await world.Factory.PlatformClient().GetAsync("/api/v1/integrations/consent/callback" + back);
        Assert.Equal(HttpStatusCode.Redirect, relay.StatusCode);
        var tenantCallback = relay.Headers.Location!;
        Assert.Equal($"{t.Key}.{ApiFactory.BaseDomain}", tenantCallback.Host);

        var client = world.Factory.TenantClient(t.Key, t["admin"].Token);
        client.DefaultRequestHeaders.Add("Cookie", $"tb_consent={binding ?? Cookie(start, "tb_consent")}");
        var done = await client.GetAsync(tenantCallback.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        return done.Headers.Location!.ToString();
    }

    private static JsonElement Feature(JsonElement connection, string key) =>
        connection.GetProperty("features").EnumerateArray().First(f => f.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Catalogue_shows_each_feature_with_permissions_and_type_before_consent()
    {
        var t = await TenantAsync();
        var catalogue = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/integrations/catalogue/microsoft365");
        var directory = catalogue.EnumerateArray().First(f => f.GetProperty("key").GetString() == "directory_sync");
        Assert.All(directory.GetProperty("permissions").EnumerateArray(), p => Assert.Equal("application", p.GetProperty("type").GetString()));
        var mail = catalogue.EnumerateArray().First(f => f.GetProperty("key").GetString() == "mail");
        Assert.All(mail.GetProperty("permissions").EnumerateArray(), p => Assert.Equal("delegated", p.GetProperty("type").GetString()));
        Assert.Equal(["User.Read", "Mail.ReadBasic"], mail.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task Create_consent_and_test_makes_directory_sync_healthy()
    {
        var t = await TenantAsync();
        var directory = Guid.NewGuid();
        var created = await CreateAsync(t, directory, "directory_sync");
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("needs_consent", created.GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ConnectionCreated, id.ToString());

        Graph.Consent(directory, DirectoryRoles);
        var landed = await ConsentAsync(t, id, directory, "directory_sync");
        Assert.Equal($"/admin/connections?connection={id}&consent=granted", landed);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ConnectionConsentGranted, id.ToString());

        var test = await (await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("healthy", test.GetProperty("status").GetString());
        var result = test.GetProperty("features")[0];
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(3400, result.GetProperty("users").GetInt64());
        Assert.Equal(120, result.GetProperty("groups").GetInt64());

        var view = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/connections/{id}");
        Assert.Equal("healthy", view.GetProperty("status").GetString());
        Assert.Equal("granted", Feature(view, "directory_sync").GetProperty("consentStatus").GetString());
    }

    [Fact]
    public async Task Test_warns_about_permissions_broader_than_needed()
    {
        var t = await TenantAsync();
        var directory = Guid.NewGuid();
        var id = (await CreateAsync(t, directory, "directory_sync")).GetProperty("id").GetGuid();
        Graph.Consent(directory, [.. DirectoryRoles, "User.ReadWrite.All"]);
        await ConsentAsync(t, id, directory, "directory_sync");
        var test = await (await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}:test", null)).ReadAsync<JsonElement>();
        var result = test.GetProperty("features")[0];
        Assert.Equal("integrations.test.ok_broader", result.GetProperty("resultKey").GetString());
        Assert.Equal(["User.ReadWrite.All"], result.GetProperty("broader").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Mail_fails_the_test_when_the_application_holds_application_mail_permissions()
    {
        var t = await TenantAsync();
        var directory = Guid.NewGuid();
        var id = (await CreateAsync(t, directory, "mail")).GetProperty("id").GetGuid();
        var start = await t["admin"].Client.GetAsync($"/api/v1/integrations/connections/{id}/features/mail/consent");
        var scope = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query)["scope"];
        Assert.Equal("https://graph.microsoft.com/User.Read https://graph.microsoft.com/Mail.ReadBasic", scope);

        Graph.Consent(directory, ["Mail.Read"]);
        await ConsentAsync(t, id, directory, "mail");
        var test = await (await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("degraded", test.GetProperty("status").GetString());
        Assert.Equal("integrations.test.application_mail_permissions", test.GetProperty("features")[0].GetProperty("resultKey").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ConnectionStatusChanged, id.ToString());
    }

    [Fact]
    public async Task Revoked_consent_is_detected_and_shown_as_needs_consent()
    {
        var t = await TenantAsync();
        var directory = Guid.NewGuid();
        var id = (await CreateAsync(t, directory, "directory_sync")).GetProperty("id").GetGuid();
        Graph.Consent(directory, DirectoryRoles);
        await ConsentAsync(t, id, directory, "directory_sync");
        await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}:test", null);

        Graph.Revoke(directory);
        var test = await (await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("needs_consent", test.GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ConnectionConsentRevokedDetected, id.ToString());
    }

    [Fact]
    public async Task Unreachable_microsoft_marks_the_connection_degraded()
    {
        var t = await TenantAsync();
        var directory = Guid.NewGuid();
        var id = (await CreateAsync(t, directory, "directory_sync")).GetProperty("id").GetGuid();
        Graph.Consent(directory, DirectoryRoles);
        await ConsentAsync(t, id, directory, "directory_sync");
        Graph.Unavailable = true;
        try
        {
            var test = await (await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}:test", null)).ReadAsync<JsonElement>();
            Assert.Equal("degraded", test.GetProperty("status").GetString());
            Assert.Equal("integrations.test.unreachable", test.GetProperty("features")[0].GetProperty("resultKey").GetString());
        }
        finally
        {
            Graph.Unavailable = false;
        }
    }

    [Fact]
    public async Task Consent_from_another_directory_or_without_the_browser_binding_is_not_recorded()
    {
        var t = await TenantAsync();
        var directory = Guid.NewGuid();
        var id = (await CreateAsync(t, directory, "directory_sync")).GetProperty("id").GetGuid();
        Assert.EndsWith("consent=failed", await ConsentAsync(t, id, directory, "directory_sync", answeringDirectory: Guid.NewGuid()), StringComparison.Ordinal);
        Assert.EndsWith("consent=failed", await ConsentAsync(t, id, directory, "directory_sync", binding: "forged"), StringComparison.Ordinal);
        Assert.EndsWith("consent=failed", await ConsentAsync(t, id, directory, "directory_sync", granted: false), StringComparison.Ordinal);
        var view = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/connections/{id}");
        Assert.Equal("pending", Feature(view, "directory_sync").GetProperty("consentStatus").GetString());
    }

    [Fact]
    public async Task Feature_not_in_package_cannot_be_turned_on()
    {
        var t = await TenantAsync("workplace");
        var response = await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = Guid.NewGuid(), displayName = "Contoso", features = new[] { "mail" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("validation.integrations.feature_not_in_package", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var catalogue = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/integrations/catalogue/microsoft365");
        Assert.False(catalogue.EnumerateArray().First(f => f.GetProperty("key").GetString() == "mail").GetProperty("inPackage").GetBoolean());
    }

    [Fact]
    public async Task One_connection_per_microsoft_365_tenant()
    {
        var t = await TenantAsync();
        var directory = Guid.NewGuid();
        await CreateAsync(t, directory, "directory_sync");
        var again = await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Again", features = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_feature_or_connection_is_audited_and_reflected()
    {
        var t = await TenantAsync();
        var directory = Guid.NewGuid();
        var id = (await CreateAsync(t, directory, "directory_sync")).GetProperty("id").GetGuid();
        (await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}/features/calendar:enable", null)).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ConnectionFeatureEnabled, id.ToString());
        (await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}/features/calendar:disable", null)).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ConnectionFeatureDisabled, id.ToString());

        (await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}:disable", null)).EnsureSuccessStatusCode();
        var view = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/connections/{id}");
        Assert.Equal("disabled", view.GetProperty("status").GetString());
        (await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{id}:enable", null)).EnsureSuccessStatusCode();
        view = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/connections/{id}");
        Assert.Equal("needs_consent", view.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Delete_shows_dependents_and_needs_confirmation()
    {
        var t = await TenantAsync();
        var id = (await CreateAsync(t, Guid.NewGuid(), "directory_sync")).GetProperty("id").GetGuid();
        var preview = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/connections/{id}/dependents");
        Assert.Equal(["directory_sync"], preview.GetProperty("enabledFeatures").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.DeleteAsync($"/api/v1/integrations/connections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.DeleteAsync($"/api/v1/integrations/connections/{id}?confirm=true")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ConnectionDeleted, id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.GetAsync($"/api/v1/integrations/connections/{id}")).StatusCode);
    }

    [Fact]
    public async Task Employees_and_organization_admins_are_denied()
    {
        foreach (var user in new[] { "employee_gulf", "orgadmin" })
        {
            var client = world.A[user].Client;
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/integrations/connections")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/integrations/connections", new { kind = "microsoft365" })).StatusCode);
        }
    }

    [Fact]
    public async Task Integration_administrator_sees_only_connections_in_admin_navigation()
    {
        var t = await TenantAsync();
        var integrator = await world.SeedUserAsync(t, "integrator", MembershipTypes.Employee);
        await world.AssignAsync(t, "integrator", "integration_administrator", "tenant", null);
        var nav = await integrator.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/navigation");
        Assert.Equal(["connections"], nav.EnumerateArray().Select(s => s.GetProperty("key").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await integrator.Client.GetAsync("/api/v1/integrations/connections")).StatusCode);
    }

    [Fact]
    public async Task Connections_of_another_tenant_are_invisible()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var directory = Guid.NewGuid();
        var inB = (await CreateAsync(b, directory, "directory_sync")).GetProperty("id").GetGuid();
        Assert.Empty(await a["admin"].Client.GetFromJsonAsync<JsonElement[]>("/api/v1/integrations/connections") ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await a["admin"].Client.GetAsync($"/api/v1/integrations/connections/{inB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a["admin"].Client.PostAsync($"/api/v1/integrations/connections/{inB}:test", null)).StatusCode);
        // The same Microsoft 365 tenant may be connected by another TeamBeats tenant (two separate records, ADR-0005).
        await CreateAsync(a, directory, "directory_sync");

        // A consent started in tenant B cannot be completed on tenant A's host.
        var start = await b["admin"].Client.GetAsync($"/api/v1/integrations/connections/{inB}/features/directory_sync/consent");
        var state = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query)["state"];
        var client = world.Factory.TenantClient(a.Key, a["admin"].Token);
        client.DefaultRequestHeaders.Add("Cookie", $"tb_consent={Cookie(start, "tb_consent")}");
        var done = await client.GetAsync($"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(state)}");
        Assert.Equal("/admin/connections?consent=failed", done.Headers.Location!.ToString());
    }
}
