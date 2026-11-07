using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 T012 and T013 (US5 scenarios 1, 2 and 4): download volume, privilege grants out of hours, blocked networks,
/// alert handling with audit, plan limits, permissions and tenant isolation.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecurityAlertTests(TenantWorld world)
{
    private async Task<TestTenant> FreshAsync(string package)
    {
        var t = await world.CreateTenantAsync("alr-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "officer", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.AssignAsync(t, "officer", "security_officer", "tenant", null);
        // Store the policy row so tests can set thresholds.
        (await t["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 30 })).EnsureSuccessStatusCode();
        return t;
    }

    private Task<int> SettingsAsync(TestTenant t, object settings) =>
        world.Database.OwnerExecInTenantAsync(t.Id, "update security.policy set alert_settings = @s::jsonb", ("s", JsonSerializer.Serialize(settings)));

    private async Task PublishAsync(TestTenant t, params DocumentDownloaded[] events)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            foreach (var e in events)
            {
                await sp.GetRequiredService<IOutbox>().EnqueueAsync(e, CancellationToken.None);
            }
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        await DispatchAsync();
    }

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private static async Task<JsonElement[]> AlertsAsync(HttpClient client, string? status = null)
    {
        var response = await client.GetAsync("/api/v1/security/alerts" + (status is null ? string.Empty : "?status=" + status));
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
    }

    [Fact]
    public async Task Download_volume_above_the_threshold_raises_one_alert_per_user_and_hour_with_the_volume_and_libraries()
    {
        var t = await FreshAsync("workplace");
        var other = await FreshAsync("workplace");
        await SettingsAsync(t, new { downloadFiles = 3 });
        var user = t["employee"].Id;
        var libraryA = Guid.NewGuid();
        var libraryB = Guid.NewGuid();

        await PublishAsync(t, new(Guid.NewGuid(), libraryA, user, 1000), new(Guid.NewGuid(), libraryA, user, 1000), new(Guid.NewGuid(), libraryB, user, 1000));
        Assert.Empty(await AlertsAsync(t["officer"].Client));

        await PublishAsync(t, new DocumentDownloaded(Guid.NewGuid(), libraryB, user, 1000));
        var alert = Assert.Single(await AlertsAsync(t["officer"].Client));
        Assert.Equal("download_volume", alert.GetProperty("kind").GetString());
        Assert.Equal(user, alert.GetProperty("subjectUserId").GetGuid());
        Assert.Equal(4, alert.GetProperty("facts").GetProperty("files").GetInt32());
        Assert.Equal(4000, alert.GetProperty("facts").GetProperty("bytes").GetInt64());
        Assert.Equal(2, alert.GetProperty("facts").GetProperty("libraries").GetArrayLength());

        // Further downloads in the same hour update the alert instead of raising more.
        await PublishAsync(t, new DocumentDownloaded(Guid.NewGuid(), libraryB, user, 1000));
        alert = Assert.Single(await AlertsAsync(t["officer"].Client));
        Assert.Equal(5, alert.GetProperty("facts").GetProperty("files").GetInt32());
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.SecurityAlertRaised));
        Assert.Equal(1, await world.Database.OutboxCountAsync("SecurityAlertRaised", t.Id));

        // Another user under the threshold and another tenant see nothing.
        await PublishAsync(t, new DocumentDownloaded(Guid.NewGuid(), libraryA, t["officer"].Id, 10));
        Assert.Single(await AlertsAsync(t["officer"].Client));
        Assert.Empty(await AlertsAsync(other["officer"].Client));

        // The byte threshold works on its own.
        await SettingsAsync(other, new { downloadBytes = 5000 });
        await PublishAsync(other, new DocumentDownloaded(Guid.NewGuid(), libraryA, other["employee"].Id, 6000));
        Assert.Equal("download_volume", Assert.Single(await AlertsAsync(other["officer"].Client)).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Granting_an_administrative_role_out_of_hours_raises_an_alert_on_Workplace_Plus_only()
    {
        var t = await FreshAsync("workplace_plus");
        var basic = await FreshAsync("workplace");
        // An empty working day: every moment is out of hours.
        await SettingsAsync(t, new { workStart = "00:00", workEnd = "00:00" });
        await SettingsAsync(basic, new { workStart = "00:00", workEnd = "00:00" });

        // Only alerts about the employee count here: the tenant's own first administrator may raise one too, depending on the hour.
        async Task<JsonElement[]> EmployeeAlertsAsync() =>
            (await AlertsAsync(t["officer"].Client)).Where(a => a.GetProperty("subjectUserId").GetGuid() == t["employee"].Id).ToArray();

        await world.AssignAsync(t, "employee", "content_publisher", "tenant", null);
        await DispatchAsync();
        Assert.Empty(await EmployeeAlertsAsync());

        var assignment = await world.AssignAsync(t, "employee", "organization_administrator", "tenant", null);
        await DispatchAsync();
        var alert = Assert.Single(await EmployeeAlertsAsync());
        Assert.Equal("privilege_grant_out_of_hours", alert.GetProperty("kind").GetString());
        Assert.Equal(t["employee"].Id, alert.GetProperty("subjectUserId").GetGuid());
        Assert.Equal(assignment, alert.GetProperty("facts").GetProperty("assignmentId").GetGuid());
        Assert.Contains("org.manage", alert.GetProperty("facts").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        await world.AssignAsync(basic, "employee", "security_officer", "tenant", null);
        await DispatchAsync();
        Assert.Empty(await AlertsAsync(basic["officer"].Client));
    }

    [Fact]
    public async Task Requests_from_blocked_networks_raise_an_alert_per_address_and_hour()
    {
        var t = await FreshAsync("workplace");
        (await t["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { ipBlock = new[] { "198.51.100.0/24" } })).EnsureSuccessStatusCode();
        var blocked = world.Factory.TenantClient(t.Key, t["employee"].Token);
        blocked.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.9");
        Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync("/api/v1/me")).StatusCode);

        var alert = Assert.Single(await AlertsAsync(t["officer"].Client));
        Assert.Equal("network_blocked", alert.GetProperty("kind").GetString());
        Assert.Equal("198.51.100.9", alert.GetProperty("facts").GetProperty("address").GetString());
    }

    [Fact]
    public async Task Security_officers_handle_alerts_with_an_audited_outcome_and_others_cannot()
    {
        var t = await FreshAsync("workplace");
        var other = await FreshAsync("workplace");
        await SettingsAsync(t, new { downloadFiles = 0 });
        await PublishAsync(t, new DocumentDownloaded(Guid.NewGuid(), Guid.NewGuid(), t["employee"].Id, 1));
        var id = Assert.Single(await AlertsAsync(t["officer"].Client)).GetProperty("id").GetGuid();
        var path = $"/api/v1/security/alerts/{id}/handle";

        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync("/api/v1/security/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.PostAsJsonAsync(path, new { outcome = "handled" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["officer"].Client.PostAsJsonAsync(path, new { outcome = "handled" })).StatusCode);
        var bad = await t["officer"].Client.PostAsJsonAsync(path, new { outcome = "ignored", note = new string('x', 1001) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badBody = await bad.Content.ReadAsStringAsync();
        Assert.Contains("validation.security.alert_outcome", badBody, StringComparison.Ordinal);
        Assert.Contains("validation.security.alert_note", badBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["officer"].Client.GetAsync("/api/v1/security/alerts?status=all")).StatusCode);

        var handled = await t["officer"].Client.PostAsJsonAsync(path, new { outcome = "handled", note = "Planned migration by the records team." });
        var view = await handled.ReadAsync<JsonElement>();
        Assert.Equal("handled", view.GetProperty("status").GetString());
        Assert.Equal(t["officer"].Id, view.GetProperty("handledBy").GetGuid());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityAlertHandled, id.ToString());
        Assert.Empty(await AlertsAsync(t["officer"].Client, "open"));
        Assert.Single(await AlertsAsync(t["officer"].Client, "handled"));

        Assert.Equal(HttpStatusCode.Conflict, (await t["officer"].Client.PostAsJsonAsync(path, new { outcome = "dismissed" })).StatusCode);
        // Tenant administrators can read alerts; only responders handle them.
        Assert.Single(await AlertsAsync(t["admin"].Client));
    }

    private static async Task<JsonElement[]> NotificationsAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/me/notifications")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToArray();

    private static async Task<JsonElement?> SecuritySectionAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/admin/navigation");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var sections = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray();
        return sections.Where(x => x.GetProperty("key").GetString() == "security").Select(x => (JsonElement?)x).FirstOrDefault();
    }

    [Fact]
    public async Task A_new_alert_notifies_alert_handlers_and_shows_as_a_badge_on_the_security_section()
    {
        var t = await FreshAsync("workplace");
        var other = await FreshAsync("workplace");
        await SettingsAsync(t, new { downloadFiles = 1 });
        var user = t["employee"].Id;
        Assert.False((await SecuritySectionAsync(t["admin"].Client))!.Value.TryGetProperty("badge", out var none) && none.ValueKind == JsonValueKind.Number);

        await PublishAsync(t, new DocumentDownloaded(Guid.NewGuid(), Guid.NewGuid(), user, 10), new DocumentDownloaded(Guid.NewGuid(), Guid.NewGuid(), user, 10));
        await DispatchAsync();
        var alert = Assert.Single(await AlertsAsync(t["officer"].Client));

        foreach (var who in new[] { "officer", "admin" })
        {
            var note = Assert.Single(await NotificationsAsync(t[who].Client), n => n.GetProperty("link").GetString() == "/admin/security/alerts");
            Assert.Equal("Unusual download volume", note.GetProperty("title").GetString());
            Assert.DoesNotContain(user.ToString(), note.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(await NotificationsAsync(t["employee"].Client), n => n.GetProperty("link").GetString() == "/admin/security/alerts");
        Assert.DoesNotContain(await NotificationsAsync(other["officer"].Client), n => n.GetProperty("link").GetString() == "/admin/security/alerts");

        Assert.Equal(1, (await SecuritySectionAsync(t["officer"].Client))!.Value.GetProperty("badge").GetInt32());
        Assert.Null(await SecuritySectionAsync(t["employee"].Client));
        var otherSection = (await SecuritySectionAsync(other["admin"].Client))!.Value;
        Assert.False(otherSection.TryGetProperty("badge", out var b) && b.ValueKind == JsonValueKind.Number);

        var handled = await t["officer"].Client.PostAsJsonAsync($"/api/v1/security/alerts/{alert.GetProperty("id").GetGuid()}/handle", new { outcome = "dismissed" });
        handled.EnsureSuccessStatusCode();
        var after = (await SecuritySectionAsync(t["officer"].Client))!.Value;
        Assert.False(after.TryGetProperty("badge", out var c) && c.ValueKind == JsonValueKind.Number);
    }
}
