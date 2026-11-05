using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Audit;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 006 audit: append-only, chain sealing and verification, scoped search, export, retention.</summary>
[Collection(WorldCollection.Name)]
public sealed class AuditTests(TenantWorld world)
{
    private Task<int> SealAsync(Guid tenantId) => world.Factory.InScopeAsync<int>(tenantId, async sp =>
    {
        var sealedRows = await sp.GetRequiredService<AuditMaintenance>().SealAsync(10_000, CancellationToken.None);
        await sp.GetRequiredService<IDbSession>().CommitAsync();
        return sealedRows;
    });

    [Theory]
    [InlineData("update audit.audit_event set action = 'x'")]
    [InlineData("delete from audit.audit_event")]
    public async Task Application_role_cannot_change_or_delete_audit_rows(string sql)
    {
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => world.Database.AppExecInTenantAsync(world.A.Id, sql));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Sealed_chain_verifies_and_tampering_is_detected_and_audited()
    {
        var tenant = await world.CreateTenantAsync("audit-chain", "workplace");
        await world.CreateUnitAsync(tenant, "Root", null, "organization");
        Assert.True(await SealAsync(tenant.Id) > 0);

        var ok = await tenant["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/audit/integrity");
        Assert.True(ok.GetProperty("intact").GetBoolean());
        Assert.Equal(0, ok.GetProperty("unsealed").GetInt64());

        await world.Database.OwnerExecInTenantAsync(tenant.Id,
            "update audit.audit_event set target_id = 'tampered' where action = @a", ("a", AuditActions.OrgUnitCreated));
        var broken = await tenant["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/audit/integrity");
        Assert.False(broken.GetProperty("intact").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, broken.GetProperty("firstBreak").ValueKind);
        await world.Database.AssertAuditedAsync(tenant.Id, AuditActions.AuditIntegrityFailed);
    }

    [Fact]
    public async Task Search_is_tenant_isolated_and_scoped_for_security_officer()
    {
        var a = await world.A["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/audit/events?limit=200");
        var items = a.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.DoesNotContain(items, e => e.GetProperty("targetId").ValueKind == JsonValueKind.String
                                          && e.GetProperty("targetId").GetString() == world.B.Id.ToString());

        var security = await world.A["security"].Client.GetAsync("/api/v1/audit/events?limit=5");
        Assert.Equal(HttpStatusCode.OK, security.StatusCode);
        var employee = await world.A["employee_gulf"].Client.GetAsync("/api/v1/audit/events");
        Assert.Equal(HttpStatusCode.Forbidden, employee.StatusCode);
    }

    [Fact]
    public async Task Search_pages_with_a_cursor()
    {
        var first = await world.A["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/audit/events?limit=2");
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var second = await world.A["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/audit/events?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        var firstIds = first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        Assert.DoesNotContain(second.GetProperty("items").EnumerateArray(), i => firstIds.Contains(i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Export_runs_through_the_outbox_and_is_downloadable_only_in_its_tenant()
    {
        var admin = world.A["admin"].Client;
        var accepted = await admin.PostAsJsonAsync("/api/v1/audit/exports", new { format = "csv" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.AuditExported, id.ToString());

        await world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

        var job = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/audit/exports/{id}");
        Assert.Equal("completed", job.GetProperty("status").GetString());
        var file = await admin.GetAsync($"/api/v1/audit/exports/{id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        var csv = await file.Content.ReadAsStringAsync();
        Assert.StartsWith("id,occurred_at,", csv);
        Assert.DoesNotContain(world.B.Id.ToString(), csv);

        var cross = await world.B["admin"].Client.GetAsync($"/api/v1/audit/exports/{id}/file");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
    }

    [Fact]
    public async Task Retention_below_minimum_is_rejected_and_change_is_audited()
    {
        var tenant = await world.CreateTenantAsync("audit-ret", "workplace");
        var low = await tenant["admin"].Client.PutAsJsonAsync("/api/v1/audit/retention", new { days = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, low.StatusCode);
        (await tenant["admin"].Client.PutAsJsonAsync("/api/v1/audit/retention", new { days = 730 })).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(tenant.Id, AuditActions.AuditRetentionChanged);
    }

    [Fact]
    public async Task Purge_removes_only_sealed_rows_past_retention_and_records_itself()
    {
        var tenant = await world.CreateTenantAsync("audit-purge", "workplace");
        await world.Database.OwnerExecInTenantAsync(tenant.Id,
            """
            insert into audit.audit_event (tenant_id, id, occurred_at, actor_type, action, target_type, outcome)
            values (@t, gen_random_uuid(), now() - interval '400 days', 'system', 'test.ancient', 'test', 'success')
            """, ("t", tenant.Id));
        await SealAsync(tenant.Id);

        var removed = await world.Factory.InScopeAsync<long>(tenant.Id, async sp =>
        {
            var n = await sp.GetRequiredService<AuditMaintenance>().PurgeAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });
        Assert.Equal(1, removed);
        Assert.Equal(0, await world.Database.AuditCountAsync(tenant.Id, "test.ancient"));
        await world.Database.AssertAuditedAsync(tenant.Id, AuditActions.AuditPurged);
        Assert.True(await world.Database.AuditCountAsync(tenant.Id, AuditActions.TenantCreated) > 0);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("-2,3", "\"'-2,3\"")]
    public void Csv_escapes_commas_and_formula_injection(string input, string expected)
    {
        Assert.Equal(expected, AuditExportHandler.Csv(input));
    }
}
