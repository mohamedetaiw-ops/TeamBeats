using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Security;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 R2 slice 2 (T023, FR-012, US3 scenario 5): access reviews. Sponsors confirm or remove their guests; managers
/// confirm or remove their reports' administrator roles; security administrators review where there is no manager,
/// never their own access. What nobody confirms within 14 days is removed, except the last tenant administrator.
/// Reviews and items of one tenant are invisible to another. Names and addresses are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecurityAccessReviewTests(TenantWorld world)
{
    private const string Reviews = "/api/v1/security/access-reviews";
    private const string Mine = "/api/v1/access-reviews";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("detailKey").GetString()!;
    }

    /// <summary>
    /// Omar owns a space with Karim as a guest he invited, and manages Lina, a security officer. Sami is a second tenant
    /// administrator. Laila is an employee with nothing to review.
    /// </summary>
    private async Task<(TestTenant T, Guid Space)> SetupAsync()
    {
        var t = await world.CreateTenantAsync("ar-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "omar", "lina", "sami", "laila" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.SeedUserAsync(t, "karim", MembershipTypes.Guest);
        await world.AssignAsync(t, "lina", "security_officer", "tenant", null);
        await world.AssignAsync(t, "sami", "tenant_administrator", "tenant", null);
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["lina"].Id}/manager", new { managerId = t["omar"].Id }));
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/spaces/manage/settings", new { creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, version = 0 }));
        var space = (await OkAsync(t["omar"].Client.PostAsJsonAsync("/api/v1/spaces", new { name = "Vendor rollout", description = "Rollout.", visibility = "private", template = "project" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"/api/v1/spaces/{space}/guests", new { email = t["karim"].Email }));
        return (t, space);
    }

    private static async Task<JsonElement[]> ItemsAsync(HttpClient client) => (await OkAsync(client.GetAsync(Mine))).GetProperty("items").EnumerateArray().ToArray();

    private Task<int> RunAsync(TestTenant t, DateTimeOffset at) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var closed = await sp.GetRequiredService<AccessReviews>().RunAsync(at, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return closed;
        });

    [Fact]
    public async Task Sponsors_and_managers_confirm_or_remove_the_access_they_are_responsible_for()
    {
        var (t, space) = await SetupAsync();
        var (other, _) = await SetupAsync();

        // Only security administrators open a review; one at a time.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.PostAsync(Reviews, null)).StatusCode);
        var review = await OkAsync(t["admin"].Client.PostAsync(Reviews, null), HttpStatusCode.Created);
        Assert.Equal("conflict.security.review_open", await ProblemKeyAsync(t["admin"].Client.PostAsync(Reviews, null), HttpStatusCode.Conflict));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityAccessReviewStarted);

        // Omar reviews the guest he invited and his report's administrator role; he was told.
        var omars = await ItemsAsync(t["omar"].Client);
        var guest = Assert.Single(omars, i => i.GetProperty("kind").GetString() == "guest");
        Assert.Equal((t["karim"].Id, space), (guest.GetProperty("subjectId").GetGuid(), guest.GetProperty("spaceId").GetGuid()));
        var role = Assert.Single(omars, i => i.GetProperty("kind").GetString() == "admin_role");
        Assert.Equal((t["lina"].Id, "security_officer"), (role.GetProperty("subjectId").GetGuid(), role.GetProperty("roleKey").GetString()));
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from notifications.notification where user_id = '{t["omar"].Id}' and template_key = '{AccessReviews.RequestedTemplate}'"));

        // The two tenant administrators review each other, never themselves; Laila has nothing to review.
        var admins = await ItemsAsync(t["admin"].Client);
        Assert.Contains(admins, i => i.GetProperty("subjectId").GetGuid() == t["sami"].Id);
        Assert.DoesNotContain(admins, i => i.GetProperty("subjectId").GetGuid() == t["admin"].Id);
        Assert.Contains(await ItemsAsync(t["sami"].Client), i => i.GetProperty("subjectId").GetGuid() == t["admin"].Id);
        Assert.Empty(await ItemsAsync(t["laila"].Client));

        // Someone else's item, or another tenant's, is not found.
        var guestItem = guest.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["laila"].Client.PostAsync($"{Mine}/items/{guestItem}:remove", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["omar"].Client.PostAsync($"{Mine}/items/{guestItem}:remove", null)).StatusCode);

        // Removing the guest takes them out of the space at once; Karim was in no other space, so he is disabled.
        await OkAsync(t["omar"].Client.PostAsync($"{Mine}/items/{guestItem}:remove", null));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from spaces.space_member where user_id = '{t["karim"].Id}'"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from tenancy.tenant_membership where user_id = '{t["karim"].Id}' and status = 'disabled'"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.GuestReviewed, t["karim"].Id.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SpacesGuestRemoved);
        Assert.Equal("conflict.security.review_decided", await ProblemKeyAsync(t["omar"].Client.PostAsync($"{Mine}/items/{guestItem}:confirm", null), HttpStatusCode.Conflict));

        // Confirming keeps Lina's role.
        await OkAsync(t["omar"].Client.PostAsync($"{Mine}/items/{role.GetProperty("id").GetGuid()}:confirm", null));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityAccessReviewDecided, t["lina"].Id.ToString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from authz.role_assignment a join authz.role r on r.id = a.role_id where a.subject_id = '{t["lina"].Id}' and r.key = 'security_officer' and a.revoked_at is null"));

        // Sami removes the first administrator; the admin cannot then remove Sami, the last one.
        var adminItem = (await ItemsAsync(t["sami"].Client)).Single(i => i.GetProperty("subjectId").GetGuid() == t["admin"].Id).GetProperty("id").GetGuid();
        var samiItem = admins.Single(i => i.GetProperty("subjectId").GetGuid() == t["sami"].Id).GetProperty("id").GetGuid();
        await OkAsync(t["sami"].Client.PostAsync($"{Mine}/items/{adminItem}:remove", null));
        Assert.Equal("conflict.security.review_last_admin", await ProblemKeyAsync(t["admin"].Client.PostAsync($"{Mine}/items/{samiItem}:remove", null), HttpStatusCode.Conflict));

        // Security administrators see the review with its counts; another tenant sees none of it.
        var page = await OkAsync(t["sami"].Client.GetAsync(Reviews));
        var listed = Assert.Single(page.GetProperty("reviews").EnumerateArray());
        Assert.Equal(review.GetProperty("id").GetGuid(), listed.GetProperty("id").GetGuid());
        Assert.Equal((2, 1), (listed.GetProperty("removed").GetInt32(), listed.GetProperty("confirmed").GetInt32()));
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync(Reviews))).GetProperty("reviews").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.GetAsync(Reviews)).StatusCode);
    }

    [Fact]
    public async Task What_nobody_confirms_in_14_days_is_removed_and_the_next_review_opens_after_90_days()
    {
        var (t, _) = await SetupAsync();
        var start = DateTimeOffset.UtcNow;

        // The first run only schedules the first review, 90 days out.
        Assert.Equal(0, await RunAsync(t, start));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from security.access_review"));
        await OkAsync(t["admin"].Client.PostAsync(Reviews, null), HttpStatusCode.Created);

        // Before the 14 days nothing changes; after them every undecided item is removed except the last administrator.
        Assert.Equal(0, await RunAsync(t, start.AddDays(13)));
        Assert.Equal(1, await RunAsync(t, start.AddDays(15)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from spaces.space_member where user_id = '{t["karim"].Id}'"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from authz.role_assignment a join authz.role r on r.id = a.role_id where a.subject_id = '{t["lina"].Id}' and r.key = 'security_officer' and a.revoked_at is null"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from authz.role_assignment a join authz.role r on r.id = a.role_id where r.key = 'tenant_administrator' and a.scope_type = 'tenant' and a.revoked_at is null"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from security.access_review_item where decision = 'kept'"));
        Assert.Equal(3, await world.Database.CountInTenantAsync(t.Id, "select count(*) from security.access_review_item where decision = 'expired'"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityAccessReviewCompleted);

        // The next review opens 90 days after the last one opened.
        await RunAsync(t, start.AddDays(89));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from security.access_review"));
        await RunAsync(t, start.AddDays(91));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from security.access_review where closed_at is null"));
    }
}
