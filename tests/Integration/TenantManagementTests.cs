using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 001 tenant management (tasks T017 to T032).</summary>
[Collection(WorldCollection.Name)]
public sealed class TenantManagementTests(TenantWorld world)
{
    private static object CreateBody(string key, string package = "workplace", string[]? admins = null) => new
    {
        key,
        displayName = "Acme Holding",
        packageKey = package,
        defaultLocale = "ar",
        enabledLocales = new[] { "ar", "en" },
        timeZone = "Asia/Riyadh",
        admins = admins ?? [$"it.lead@{key}.example"],
    };

    [Fact]
    public async Task US1_1_create_tenant_seeds_roles_assigns_package_appoints_admin_and_audits()
    {
        var response = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", CreateBody("acme"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = tenant.GetProperty("id").GetGuid();
        Assert.Equal("active", tenant.GetProperty("status").GetString());

        Assert.Equal(20, await world.Database.CountInTenantAsync(id, "select count(*) from authz.role where is_builtin"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(id,
            "select count(*) from entitlements.subscription s join entitlements.package_version v on v.id = s.package_version_id where v.package_key = 'workplace' and s.status = 'active'"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(id,
            "select count(*) from tenancy.tenant_admin_appointment where invited_email = 'it.lead@acme.example' and status = 'pending'"));
        await world.Database.AssertAuditedAsync(id, AuditActions.TenantCreated, id.ToString());
        await world.Database.AssertAuditedAsync(id, AuditActions.TenantAdminAppointed);
        Assert.Equal(1, await world.Database.PlatformAuditCountAsync(AuditActions.TenantCreated, id));
        Assert.Equal(1, await world.Database.OutboxCountAsync("TenantCreated", id));

        // Independent test: the appointed admin signs in (first sign-in activates the appointment) and sees only this tenant.
        var tt = new TestTenant("acme", id);
        var admin = await world.SeedUserAsync(tt, "it.lead", "employee");
        var settings = await admin.Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/settings");
        Assert.Equal("ar", settings.GetProperty("defaultLocale").GetString());
        var me = await admin.Client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(id, me.GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal("rtl", me.GetProperty("dir").GetString());
    }

    [Fact]
    public async Task US1_2_duplicate_key_is_case_insensitive()
    {
        (await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", CreateBody("dupe-co"))).EnsureSuccessStatusCode();
        var second = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", CreateBody("DUPE-CO"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conflict.tenant.key_taken", problem.GetProperty("detailKey").GetString());
    }

    [Fact]
    public async Task Concurrent_create_with_same_key_exactly_one_succeeds()
    {
        var tasks = Enumerable.Range(0, 5).Select(_ => world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", CreateBody("racer"))).ToList();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task US1_3_unknown_package_rolls_back_everything()
    {
        var response = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", CreateBody("ghost-co", package: "no_such_package"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var list = await world.Platform.GetFromJsonAsync<JsonElement>("/api/v1/platform/tenants?q=ghost-co");
        Assert.Equal(0, list.GetArrayLength());
        Assert.Equal(0, await world.Database.OwnerScalarAsync<long>("select count(*) from tenancy.tenant where key = 'ghost-co'"));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac me")]
    [InlineData("acme_co")]
    [InlineData("platform")]
    [InlineData("admin")]
    [InlineData("api")]
    public async Task Invalid_and_reserved_keys_are_rejected(string key)
    {
        var response = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", CreateBody(key));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("key", out _));
    }

    [Fact]
    public async Task Idempotency_key_replays_the_first_response()
    {
        var body = CreateBody("idem-co");
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/platform/tenants") { Content = JsonContent.Create(body) };
        request1.Headers.Add("Idempotency-Key", "create-idem-co-1");
        var first = await world.Platform.SendAsync(request1);
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/platform/tenants") { Content = JsonContent.Create(body) };
        request2.Headers.Add("Idempotency-Key", "create-idem-co-1");
        var second = await world.Platform.SendAsync(request2);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(),
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task US1_4_tenant_admin_cannot_call_platform_endpoints()
    {
        // On a tenant host, platform routes do not exist for anyone.
        var onTenantHost = await world.A["admin"].Client.PostAsJsonAsync("/api/v1/platform/tenants", CreateBody("sneaky"));
        Assert.Equal(HttpStatusCode.NotFound, onTenantHost.StatusCode);

        // On the platform host, a tenant session is not a platform session.
        var onPlatformHost = await world.Factory.PlatformClient(world.A["admin"].Token).PostAsJsonAsync("/api/v1/platform/tenants", CreateBody("sneaky"));
        Assert.Equal(HttpStatusCode.Unauthorized, onPlatformHost.StatusCode);
        Assert.Equal(0, await world.Database.OwnerScalarAsync<long>("select count(*) from tenancy.tenant where key = 'sneaky'"));
    }

    [Fact]
    public async Task US2_1_enable_ar_and_en_with_ar_default()
    {
        var t = await world.CreateTenantAsync("locales-co", "workplace", "en", ["en"]);
        var admin = t["admin"].Client;
        var current = await admin.GetFromJsonAsync<JsonElement>("/api/v1/tenant/settings");
        var response = await admin.PutAsJsonAsync("/api/v1/tenant/settings", new
        {
            defaultLocale = "ar",
            enabledLocales = new[] { "ar", "en" },
            timeZone = "Asia/Dubai",
            primaryColor = "#0A4D8C",
            rowVersion = current.GetProperty("rowVersion").GetInt32(),
        });
        var updated = await response.ReadAsync<JsonElement>();
        Assert.Equal("ar", updated.GetProperty("defaultLocale").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TenantSettingsChanged);

        var employee = await world.SeedUserAsync(t, "emp", "employee");
        var me = await employee.Client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal("ar", me.GetProperty("locale").GetString());
        Assert.Equal("rtl", me.GetProperty("dir").GetString());

        // The employee can switch to English.
        (await employee.Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "en" })).EnsureSuccessStatusCode();
        me = await employee.Client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal("en", me.GetProperty("locale").GetString());
        Assert.Equal("ltr", me.GetProperty("dir").GetString());
    }

    [Fact]
    public async Task US2_2_cannot_disable_the_default_locale()
    {
        var admin = world.A["admin"].Client;
        var current = await admin.GetFromJsonAsync<JsonElement>("/api/v1/tenant/settings");
        var response = await admin.PutAsJsonAsync("/api/v1/tenant/settings", new
        {
            defaultLocale = current.GetProperty("defaultLocale").GetString(),
            enabledLocales = new[] { "fr" },
            timeZone = "Asia/Dubai",
            rowVersion = current.GetProperty("rowVersion").GetInt32(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation.locale.default_must_be_enabled",
            problem.GetProperty("errors").GetProperty("defaultLocale")[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task Settings_update_with_stale_row_version_conflicts()
    {
        var t = await world.CreateTenantAsync("stale-co", "workplace");
        var admin = t["admin"].Client;
        var current = await admin.GetFromJsonAsync<JsonElement>("/api/v1/tenant/settings");
        var body = new { defaultLocale = "en", enabledLocales = new[] { "ar", "en" }, timeZone = "UTC", rowVersion = current.GetProperty("rowVersion").GetInt32() };
        (await admin.PutAsJsonAsync("/api/v1/tenant/settings", body)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/tenant/settings", body)).StatusCode);
    }

    [Fact]
    public async Task Employees_cannot_read_or_change_tenant_settings()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await world.A["employee_gulf"].Client.GetAsync("/api/v1/tenant/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await world.A["employee_gulf"].Client.PutAsJsonAsync("/api/v1/tenant/settings",
            new { defaultLocale = "en", enabledLocales = new[] { "en" }, timeZone = "UTC", rowVersion = 1 })).StatusCode);
    }

    public static IEnumerable<object[]> Logos()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16 };
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\" fill=\"#0A4D8C\"/></svg>");
        yield return ["image/png", png, HttpStatusCode.NoContent];
        yield return ["image/jpeg", jpeg, HttpStatusCode.NoContent];
        yield return ["image/svg+xml", svg, HttpStatusCode.NoContent];
        yield return ["image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"), HttpStatusCode.BadRequest];
        yield return ["image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect onload=\"alert(1)\"/></svg>"), HttpStatusCode.BadRequest];
        yield return ["image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><image xlink:href=\"https://evil.example/x.png\"/></svg>"), HttpStatusCode.BadRequest];
        yield return ["image/svg+xml", Encoding.UTF8.GetBytes("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg>&x;</svg>"), HttpStatusCode.BadRequest];
        yield return ["image/gif", Encoding.UTF8.GetBytes("GIF89a"), HttpStatusCode.BadRequest];
        yield return ["image/png", Encoding.UTF8.GetBytes("not really a png"), HttpStatusCode.BadRequest];
        var big = new byte[(int)(1.1 * 1024 * 1024)];
        png.CopyTo(big, 0);
        yield return ["image/png", big, HttpStatusCode.BadRequest];
    }

    [Theory]
    [MemberData(nameof(Logos))]
    public async Task US2_4_logo_rules(string contentType, byte[] data, HttpStatusCode expected)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var response = await world.A["admin"].Client.PutAsync("/api/v1/tenant/branding/logo", content);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.NoContent)
        {
            var stored = Directory.GetFiles(Path.Combine(world.Factory.StorageRoot, "tenants", world.A.Id.ToString("D"), "branding"));
            Assert.NotEmpty(stored);
        }
    }

    [Fact]
    public async Task US3_suspend_blocks_access_revokes_sessions_and_reactivation_restores()
    {
        var t = await world.CreateTenantAsync("suspend-co", "workplace");
        var employee = await world.SeedUserAsync(t, "emp", "employee");
        Assert.Equal(HttpStatusCode.OK, (await employee.Client.GetAsync("/api/v1/me")).StatusCode);

        var suspend = await world.Platform.PostAsJsonAsync($"/api/v1/platform/tenants/{t.Id}:suspend", new { reason = "non-payment" });
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);

        var blocked = await employee.Client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("tenant.suspended", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from identity.user_session where revoked_at is null"));
        Assert.True(await world.Database.CountInTenantAsync(t.Id, "select count(*) from identity.user_account") > 0, "data is retained");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TenantStatusChanged, t.Id.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SessionRevoked);
        Assert.Equal(1, await world.Database.OutboxCountAsync("TenantSuspended", t.Id));

        (await world.Platform.PostAsJsonAsync($"/api/v1/platform/tenants/{t.Id}:reactivate", new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await employee.Client.GetAsync("/api/v1/me")).StatusCode);
        var again = await world.SeedUserAsync(t, "emp2", "employee");
        Assert.Equal(HttpStatusCode.OK, (await again.Client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Close_marks_tenant_for_deletion_after_90_days_and_blocks_access()
    {
        var t = await world.CreateTenantAsync("close-co", "workplace");
        var closed = await (await world.Platform.PostAsJsonAsync($"/api/v1/platform/tenants/{t.Id}:close", new { reason = "contract ended" }))
            .ReadAsync<JsonElement>();
        var due = closed.GetProperty("deletionDueAt").GetDateTimeOffset();
        Assert.InRange((due - DateTimeOffset.UtcNow).TotalDays, 89.9, 90.1);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["admin"].Client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await world.Platform.PostAsJsonAsync($"/api/v1/platform/tenants/{t.Id}:reactivate", new { })).StatusCode);
    }

    [Fact]
    public async Task Tenant_admin_appoints_another_admin()
    {
        var t = await world.CreateTenantAsync("appoint-co", "workplace");
        var existing = await world.SeedUserAsync(t, "second", "employee");
        var response = await t["admin"].Client.PostAsJsonAsync("/api/v1/tenant/admins", new { email = existing.Email });
        var appointment = await response.ReadAsync<JsonElement>();
        Assert.Equal("active", appointment.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await existing.Client.GetAsync("/api/v1/tenant/settings")).StatusCode);

        var pending = await (await t["admin"].Client.PostAsJsonAsync("/api/v1/tenant/admins", new { email = "future@appoint-co.example" })).ReadAsync<JsonElement>();
        Assert.Equal("pending", pending.GetProperty("status").GetString());
    }
}
