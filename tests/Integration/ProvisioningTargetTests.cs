using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 003 FR-008: the provisioning extension point shaped after SCIM 2.0 (find, create, update, deactivate a user,
/// set a group's members), which directory sync runs on and a later SCIM endpoint would call. Never links by email,
/// keeps the last administrator, revokes sessions on deactivation, and sees only the tenant in context.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ProvisioningTargetTests(TenantWorld world)
{
    private static ProvisioningSource Source(string issuer) => new(Guid.NewGuid(), issuer, "directory");

    private Task<T> InTenantAsync<T>(TestTenant t, Func<IProvisioningTarget, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await action(sp.GetRequiredService<IProvisioningTarget>());
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<string?> StatusAsync(TestTenant t, Guid user) =>
        world.Database.TenantScalarAsync<string>(t.Id, "select status from tenancy.tenant_membership where user_id = @u", ("u", user));

    [Fact]
    public async Task Creates_finds_updates_and_links_users_by_immutable_id_only()
    {
        var t = await world.CreateTenantAsync("pv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var issuer = $"urn:test:provisioning:{Guid.NewGuid():N}";

        var (outcome, id) = await InTenantAsync(t, p => p.CreateOrLinkUserAsync(Source(issuer), new ProvisionedUser("ext-1", "Dina", $"dina@{t.Key}.example"), CancellationToken.None));
        Assert.Equal(ProvisionOutcome.Created, outcome);
        Assert.Equal(id, await InTenantAsync(t, p => p.FindUserAsync(issuer, "ext-1", CancellationToken.None)));
        Assert.Equal("active", await StatusAsync(t, id!.Value));
        Assert.Equal("directory", await world.Database.TenantScalarAsync<string>(t.Id, "select created_via from identity.user_account where id = @u", ("u", id!.Value)));

        // The same ID again links to the same user; another ID with a used email is skipped, never linked by email.
        Assert.Equal((ProvisionOutcome.Linked, id), await InTenantAsync(t, p => p.CreateOrLinkUserAsync(Source(issuer), new ProvisionedUser("ext-1", "Dina", null), CancellationToken.None)));
        Assert.Equal(ProvisionOutcome.Skipped,
            (await InTenantAsync(t, p => p.CreateOrLinkUserAsync(Source(issuer), new ProvisionedUser("ext-2", "Other", $"dina@{t.Key}.example"), CancellationToken.None))).Outcome);
        Assert.Equal(ProvisionOutcome.Skipped, (await InTenantAsync(t, p => p.CreateOrLinkUserAsync(Source(issuer), new ProvisionedUser("ext-3", "No mail", null), CancellationToken.None))).Outcome);

        var renamed = await InTenantAsync(t, p => p.UpdateUserAsync(Source(issuer), id.Value, new ProvisionedUser("ext-1", "Dina Saleh", $"dina.saleh@{t.Key}.example"), CancellationToken.None));
        Assert.Equal(["display_name", "email"], renamed.Changed);
        Assert.False(renamed.EmailKept);
        // An email another account holds is not taken over; the old one stays.
        var clash = await InTenantAsync(t, p => p.UpdateUserAsync(Source(issuer), id.Value, new ProvisionedUser("ext-1", "Dina Saleh", t["admin"].Email), CancellationToken.None));
        Assert.Empty(clash.Changed);
        Assert.True(clash.EmailKept);
        Assert.Equal($"dina.saleh@{t.Key}.example", await world.Database.TenantScalarAsync<string>(t.Id, "select email from identity.user_account where id = @u", ("u", id!.Value)));
    }

    [Fact]
    public async Task Deactivation_revokes_sessions_and_keeps_the_last_administrator()
    {
        var t = await world.CreateTenantAsync("pv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var employee = await world.SeedUserAsync(t, "erin", MembershipTypes.Employee);
        var source = Source("urn:test");

        Assert.True(await InTenantAsync(t, p => p.DeactivateUserAsync(source, employee.Id, CancellationToken.None)));
        // The audit entry names the source connection (spec 003 section 13).
        Assert.Equal(source.ConnectionId.ToString(), await world.Database.TenantScalarAsync<string>(t.Id,
            "select changes->>'connection' from audit.audit_event where action = 'identity.disabled_by_directory' and target_id = @u", ("u", employee.Id.ToString())));
        Assert.Equal("disabled", await StatusAsync(t, employee.Id));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from identity.user_session where user_id = @u and revoked_at is null", ("u", employee.Id)));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await employee.Client.GetAsync("/api/v1/me")).StatusCode);

        await InTenantAsync(t, async p => { await p.ReactivateUserAsync(Source("urn:test"), employee.Id, CancellationToken.None); return true; });
        Assert.Equal("active", await StatusAsync(t, employee.Id));

        Assert.False(await InTenantAsync(t, p => p.DeactivateUserAsync(Source("urn:test"), t["admin"].Id, CancellationToken.None)));
        Assert.Equal("active", await StatusAsync(t, t["admin"].Id));
    }

    [Fact]
    public async Task Group_members_are_set_on_a_source_owned_group()
    {
        var t = await world.CreateTenantAsync("pv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var erin = await world.SeedUserAsync(t, "erin", MembershipTypes.Employee);
        var key = "entra:" + Guid.NewGuid().ToString("D");

        var group = await InTenantAsync(t, p => p.SetGroupMembersAsync(key, "Finance", [erin.Id, t["admin"].Id], CancellationToken.None));
        Assert.Equal(group, await InTenantAsync(t, p => p.SetGroupMembersAsync(key, "Finance", [erin.Id], CancellationToken.None)));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from authz.group_member where group_id = @g", ("g", (object)group)));
    }

    [Fact]
    public async Task Another_tenant_cannot_find_or_change_a_provisioned_user()
    {
        var a = await world.CreateTenantAsync("pv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var b = await world.CreateTenantAsync("pv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var issuer = $"urn:test:provisioning:{Guid.NewGuid():N}";
        var (_, id) = await InTenantAsync(a, p => p.CreateOrLinkUserAsync(Source(issuer), new ProvisionedUser("ext-1", "Dina", $"dina@{a.Key}.example"), CancellationToken.None));

        Assert.Null(await InTenantAsync(b, p => p.FindUserAsync(issuer, "ext-1", CancellationToken.None)));
        // The same immutable ID in another tenant is a separate person (per-tenant identities).
        var (outcome, other) = await InTenantAsync(b, p => p.CreateOrLinkUserAsync(Source(issuer), new ProvisionedUser("ext-1", "Dina", $"dina@{a.Key}.example"), CancellationToken.None));
        Assert.Equal(ProvisionOutcome.Created, outcome);
        Assert.NotEqual(id, other);
        await Assert.ThrowsAsync<AppException>(() => InTenantAsync(b, p => p.UpdateUserAsync(Source(issuer), id!.Value, new ProvisionedUser("ext-1", "Changed", null), CancellationToken.None)));
        await Assert.ThrowsAsync<AppException>(() => InTenantAsync(b, p => p.DeactivateUserAsync(Source(issuer), id!.Value, CancellationToken.None)));
        Assert.Equal("active", await StatusAsync(a, id!.Value));
        Assert.Equal("Dina", await world.Database.TenantScalarAsync<string>(a.Id, "select display_name from identity.user_account where id = @u", ("u", id!.Value)));
    }
}
