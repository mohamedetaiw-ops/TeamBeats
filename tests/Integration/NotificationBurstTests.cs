using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 015 section 15 and T009: a burst of 100,000 notifications, as a critical announcement sends them, creates every
/// in-app record within 2 minutes. The sender is called in the batches of 500 that the critical notice handler uses,
/// each batch in a scope and transaction of its own like an outbox message. Members are written straight into the table.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class NotificationBurstTests(TenantWorld world, ITestOutputHelper output)
{
    private const int Audience = 100_000;
    private const int Batch = 500;

    [Fact]
    public async Task A_burst_of_100000_notifications_creates_every_in_app_record_within_two_minutes()
    {
        var t = await world.CreateTenantAsync("nb-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            with people as (select gen_random_uuid() as id from generate_series(1, @n)),
            accounts as (
              insert into identity.user_account (tenant_id, id, display_name, email, status, created_via)
              select @t, id, 'Person', id || '@burst.example', 'active', 'dev' from people returning id)
            insert into tenancy.tenant_membership (tenant_id, user_id, membership_type, status)
            select @t, id, 'employee', 'active' from accounts
            """, ("t", t.Id), ("n", Audience));
        var members = await world.Factory.InScopeAsync(t.Id, async sp =>
            await sp.GetRequiredService<IDbSession>().QueryAsync("select user_id from tenancy.tenant_membership where status = 'active'", null, r => r.GetGuid(0)));

        var clock = Stopwatch.StartNew();
        var created = 0;
        foreach (var chunk in members.Chunk(Batch))
        {
            created += await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                var result = await sp.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest(chunk,
                    NotificationCategories.CriticalAnnouncement, "content.critical.sent", "/content/read/00000000-0000-0000-0000-000000000001", Urgent: true));
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return result.Created;
            });
        }
        clock.Stop();
        output.WriteLine($"{created} notifications created in {clock.Elapsed.TotalSeconds:F1} s");

        Assert.True(members.Count > Audience, "The tenant admin is a member too.");
        Assert.Equal(members.Count, created);
        Assert.Equal(members.Count, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification"));
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(2), $"{created} notifications took {clock.Elapsed.TotalSeconds:F0} s");
        // Critical announcements are mandatory, so every person gets an email delivery queued, sent at the relay's pace.
        Assert.Equal(members.Count, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery where channel = 'email'"));

        // The queued email is removed here so the shared delivery worker does not spend other tests' time on it.
        await world.Database.OwnerExecAsync("delete from notifications.delivery where tenant_id = @t", ("t", t.Id));
    }
}
