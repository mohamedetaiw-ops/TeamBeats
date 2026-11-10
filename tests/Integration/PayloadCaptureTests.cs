using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 FR-007 and T001: payload capture for debugging. Off by default; an administrator turns it on for one
/// connection for at most 24 hours with a statement that the data is Public or Internal; each run in the window keeps
/// its input and output for 24 hours, readable only by operators of the tenant; run history itself never shows content.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PayloadCaptureTests(TenantWorld world)
{
    private static readonly JsonElement Input = JsonSerializer.SerializeToElement(new { summary = "Laptop for Mona", category = "hardware" });

    private async Task<TestTenant> TenantAsync()
    {
        var t = await world.CreateTenantAsync("pc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        return t;
    }

    private async Task<Guid> ConnectionAsync(TestTenant t, string kind = "test_ticketing")
    {
        var id = Guid.NewGuid();
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into integrations.connection (tenant_id, id, kind, display_name, status, enabled, rate_limit_per_minute)
            values (@t, @id, @kind, 'Service desk', 'healthy', true, 600)
            """, ("t", t.Id), ("id", id), ("kind", kind));
        return id;
    }

    private Task<ConnectorCallResult> InvokeAsync(TestTenant t, Guid connection, string key) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await sp.GetRequiredService<IConnectorRuntime>().InvokeAsync(new ConnectorCall(connection, "create_incident", Input, key, "corr-pc"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private static string Base(Guid c) => $"/api/v1/integrations/connections/{c}";

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, Guid c, int? hours = 2, bool? publicOrInternal = true) =>
        client.PutAsJsonAsync($"{Base(c)}/capture", new { hours, publicOrInternal });

    private static async Task<JsonElement> RunAsync(HttpClient client, Guid c, Guid run)
    {
        var page = await (await client.GetAsync($"{Base(c)}/runs")).ReadAsync<JsonElement>();
        return page.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == run);
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    [Fact]
    public async Task Capture_is_off_by_default_and_a_window_keeps_input_and_output_for_operators_only()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        var admin = t["admin"].Client;

        var before = await InvokeAsync(t, c, "pc-before");
        Assert.False((await RunAsync(admin, c, before.RunId!.Value)).GetProperty("captured").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Base(c)}/runs/{before.RunId}/capture")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await (await admin.GetAsync($"{Base(c)}/capture")).ReadAsync<JsonElement>()).GetProperty("until").ValueKind);

        var started = await StartAsync(admin, c);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var until = (await started.ReadAsync<JsonElement>()).GetProperty("until").GetDateTimeOffset();
        Assert.InRange(until - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(110), TimeSpan.FromMinutes(121));
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ConnectionChanged, c.ToString()));

        var during = await InvokeAsync(t, c, "pc-during");
        var row = await RunAsync(admin, c, during.RunId!.Value);
        Assert.True(row.GetProperty("captured").GetBoolean());
        Assert.DoesNotContain("Laptop for Mona", row.GetRawText());
        var capture = await (await admin.GetAsync($"{Base(c)}/runs/{during.RunId}/capture")).ReadAsync<JsonElement>();
        Assert.Equal("Laptop for Mona", capture.GetProperty("input").GetProperty("summary").GetString());
        Assert.Equal(during.ExternalReference, capture.GetProperty("output").GetProperty("number").GetString());

        // An employee is no operator; another tenant does not see the connection.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Base(c)}/runs/{during.RunId}/capture")).StatusCode);
        var other = await TenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Base(c)}/runs/{during.RunId}/capture")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await StartAsync(other["admin"].Client, c)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Base(c)}/capture")).StatusCode);
        Assert.Equal(2, await world.Database.AuditCountAsync(t.Id, AuditActions.ConnectionChanged, c.ToString()));
        var after = await InvokeAsync(t, c, "pc-after");
        Assert.False((await RunAsync(admin, c, after.RunId!.Value)).GetProperty("captured").GetBoolean());
    }

    [Fact]
    public async Task A_capture_is_deleted_after_24_hours()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(t["admin"].Client, c, hours: 24)).StatusCode);
        var run = await InvokeAsync(t, c, "pc-expiry");
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from integrations.run_capture where run_id = @id", ("id", run.RunId!.Value)));

        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ConnectorJobs>().RunDueAsync(DateTimeOffset.UtcNow.AddHours(25), 50, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from integrations.run_capture where run_id = @id", ("id", run.RunId!.Value)));
    }

    [Fact]
    public async Task Capture_needs_a_window_of_at_most_24_hours_the_data_statement_and_a_connection_that_may_capture()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        var admin = t["admin"].Client;
        Assert.Contains("validation.connector.capture_hours", await ErrorKeysAsync(await StartAsync(admin, c, hours: 25)));
        Assert.Contains("validation.connector.capture_hours", await ErrorKeysAsync(await StartAsync(admin, c, hours: 0)));
        Assert.Contains("validation.connector.capture_classification", await ErrorKeysAsync(await StartAsync(admin, c, publicOrInternal: false)));
        var directory = await ConnectionAsync(t, ConnectionKinds.OnPremAd);
        Assert.Contains("validation.connector.capture_kind", await ErrorKeysAsync(await StartAsync(admin, directory)));
        Assert.Equal(HttpStatusCode.Forbidden, (await StartAsync(t["sara"].Client, c)).StatusCode);
        Assert.Equal(0, await world.Database.AuditCountAsync(t.Id, AuditActions.ConnectionChanged, c.ToString()));
    }
}
