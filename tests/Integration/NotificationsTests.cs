using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 015 R1: the send contract, the in-app centre, email delivery with preferences, digest, quiet hours, retries and
/// bounces, and tenant notification settings and templates. Every send runs in a tenant scope, as a sending module does.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class NotificationsTests(TenantWorld world)
{
    private const string Sample = "system.sample";

    private TestEmailSender Email => world.Factory.Email;

    private async Task<TestTenant> TenantAsync(string package = "workplace", string locale = "en")
    {
        var t = await world.CreateTenantAsync("nt-" + Guid.NewGuid().ToString("N")[..8], package, locale);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "other", MembershipTypes.Employee);
        return t;
    }

    private Task<SendResult> SendAsync(TestTenant t, NotificationRequest request) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await sp.GetRequiredService<INotificationSender>().SendAsync(request);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<SendResult> SendAsync(TestTenant t, string category, params Guid[] recipients) =>
        SendAsync(t, new NotificationRequest(recipients, category, Sample, "/p/home", new Dictionary<string, string> { ["label"] = "HR" }));

    private Task<int> DeliverAsync(TestTenant t, DateTimeOffset? at = null) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var count = await sp.GetRequiredService<EmailDelivery>().DeliverDueAsync(at ?? DateTimeOffset.UtcNow, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return count;
        });

    private Task<int> DigestAsync(TestTenant t, DateTimeOffset at) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var count = await sp.GetRequiredService<EmailDelivery>().SendDigestsAsync(at, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return count;
        });

    private Task<long> DeliveriesAsync(TestTenant t, string status) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery where status = @s", ("s", status));

    private static async Task<JsonElement> ListAsync(TestUser user, string query = "")
    {
        var response = await user.Client.GetAsync("/api/v1/me/notifications" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<int> CountAsync(TestUser user) =>
        (await (await user.Client.GetAsync("/api/v1/me/notifications/count")).ReadAsync<JsonElement>()).GetProperty("unread").GetInt32();

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    private static Task<HttpResponseMessage> SetPreferencesAsync(TestUser user, object body) =>
        user.Client.PutAsJsonAsync("/api/v1/me/notification-preferences", body);

    // In-app centre (US1)

    [Fact]
    public async Task A_notification_reaches_the_centre_with_its_text_and_the_count_and_read_state_follow()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        var result = await SendAsync(t, NotificationCategories.Announcement, employee.Id);
        Assert.Equal(1, result.Created);

        var page = await ListAsync(employee);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("Sample notification from HR", item.GetProperty("title").GetString());
        Assert.Equal("/p/home", item.GetProperty("link").GetString());
        Assert.Equal(1, page.GetProperty("unread").GetInt32());
        Assert.Equal(1, await CountAsync(employee));

        var read = await employee.Client.PostAsync($"/api/v1/me/notifications/{item.GetProperty("id").GetGuid()}/read", null);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("/p/home", (await read.ReadAsync<JsonElement>()).GetProperty("link").GetString());
        Assert.Equal(0, await CountAsync(employee));
        Assert.Equal(0, (await ListAsync(employee, "?filter=unread")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Text_is_rendered_in_the_readers_language()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        await employee.Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" });
        await SendAsync(t, NotificationCategories.Announcement, employee.Id);
        var item = (await ListAsync(employee)).GetProperty("items")[0];
        Assert.Equal("إشعار تجريبي من HR", item.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Each_user_sees_only_their_own_notifications_and_cannot_read_anothers()
    {
        var t = await TenantAsync();
        await SendAsync(t, NotificationCategories.Announcement, t["employee"].Id);
        var mine = (await ListAsync(t["employee"])).GetProperty("items")[0].GetProperty("id").GetGuid();

        Assert.Equal(0, (await ListAsync(t["other"])).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await t["other"].Client.PostAsync($"/api/v1/me/notifications/{mine}/read", null)).StatusCode);
        Assert.Equal(1, await CountAsync(t["employee"]));
    }

    [Fact]
    public async Task Another_tenants_user_is_skipped_and_another_tenant_cannot_see_the_row()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var result = await SendAsync(a, NotificationCategories.Announcement, a["employee"].Id, b["employee"].Id);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, (await ListAsync(b["employee"])).GetProperty("items").GetArrayLength());

        var id = (await ListAsync(a["employee"])).GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await b["employee"].Client.PostAsync($"/api/v1/me/notifications/{id}/read", null)).StatusCode);
        Assert.Equal(0, await world.Database.CountInTenantAsync(b.Id, "select count(*) from notifications.notification"));
    }

    [Fact]
    public async Task A_disabled_member_receives_nothing()
    {
        var t = await TenantAsync();
        await world.Database.OwnerExecInTenantAsync(t.Id, "update tenancy.tenant_membership set status = 'disabled' where user_id = @u", ("u", t["other"].Id));
        var result = await SendAsync(t, NotificationCategories.Announcement, t["other"].Id, t["employee"].Id);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(Email.To(t["other"].Email));
    }

    [Fact]
    public async Task The_same_dedupe_key_within_an_hour_is_sent_once()
    {
        var t = await TenantAsync();
        var request = new NotificationRequest([t["employee"].Id], NotificationCategories.ApprovalWaiting, Sample, "/approvals/1",
            new Dictionary<string, string> { ["label"] = "x" }, DedupeKey: "approval:1:waiting");
        Assert.Equal(1, (await SendAsync(t, request)).Created);
        Assert.Equal(1, (await SendAsync(t, request)).Duplicates);
        Assert.Equal(1, (await ListAsync(t["employee"])).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Related_notifications_are_grouped_into_one_entry_with_a_count()
    {
        var t = await TenantAsync();
        var request = new NotificationRequest([t["employee"].Id], NotificationCategories.Comment, Sample, "/p/news",
            new Dictionary<string, string> { ["label"] = "News" }, GroupKey: "comments:news");
        await SendAsync(t, request);
        Assert.Equal(1, (await SendAsync(t, request)).Grouped);
        Assert.Equal(1, (await SendAsync(t, request)).Grouped);

        var item = Assert.Single((await ListAsync(t["employee"])).GetProperty("items").EnumerateArray());
        Assert.Equal(3, item.GetProperty("count").GetInt32());
        Assert.Contains("3", item.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example")]
    [InlineData("p/home")]
    public async Task Links_must_stay_on_the_tenant_host(string link)
    {
        var t = await TenantAsync();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            SendAsync(t, new NotificationRequest([t["employee"].Id], NotificationCategories.System, Sample, link)));
        Assert.Contains("validation.notification.link_not_relative", ex.Errors!.SelectMany(e => e.Value));
    }

    [Fact]
    public async Task Unknown_categories_and_long_parameters_are_refused()
    {
        var t = await TenantAsync();
        var ex = await Assert.ThrowsAsync<AppException>(() => SendAsync(t, new NotificationRequest(
            [t["employee"].Id], "made_up", Sample, "/", new Dictionary<string, string> { ["label"] = new string('x', 501) })));
        var keys = ex.Errors!.SelectMany(e => e.Value).ToArray();
        Assert.Contains("validation.notification.category_unknown", keys);
        Assert.Contains("validation.notification.parameter_too_long", keys);
    }

    [Fact]
    public async Task The_centre_pages_with_a_cursor_and_filters_by_mandatory()
    {
        var t = await TenantAsync();
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(t, NotificationCategories.Announcement, t["employee"].Id);
        }
        await SendAsync(t, NotificationCategories.SecurityNotice, t["employee"].Id);

        var first = await ListAsync(t["employee"], "?limit=2");
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await ListAsync(t["employee"], "?limit=2&cursor=" + cursor);
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        var ids = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("id").GetGuid());
        Assert.Equal(4, ids.Distinct().Count());

        var mandatory = await ListAsync(t["employee"], "?filter=mandatory");
        Assert.True(Assert.Single(mandatory.GetProperty("items").EnumerateArray()).GetProperty("mandatory").GetBoolean());

        Assert.Contains("validation.cursor.invalid", await ErrorKeysAsync(await t["employee"].Client.GetAsync("/api/v1/me/notifications?cursor=nope")));
        Assert.Contains("validation.notification.filter_invalid", await ErrorKeysAsync(await t["employee"].Client.GetAsync("/api/v1/me/notifications?filter=x")));
    }

    [Fact]
    public async Task Read_all_clears_the_count()
    {
        var t = await TenantAsync();
        await SendAsync(t, NotificationCategories.Announcement, t["employee"].Id);
        await SendAsync(t, NotificationCategories.Mention, t["employee"].Id);
        Assert.Equal(2, await CountAsync(t["employee"]));
        Assert.Equal(HttpStatusCode.NoContent, (await t["employee"].Client.PostAsync("/api/v1/me/notifications/read-all", null)).StatusCode);
        Assert.Equal(0, await CountAsync(t["employee"]));
    }

    // Email (US2)

    [Fact]
    public async Task Email_goes_out_in_the_recipients_language_with_an_absolute_link_and_the_tenant_sender_name()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        await employee.Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" });
        var saved = await t["admin"].Client.PutAsJsonAsync("/api/v1/notifications/settings", new { senderName = "Contoso HR", version = 0 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        Assert.Equal(1, await DeliverAsync(t));

        var mail = Assert.Single(Email.To(employee.Email));
        Assert.Equal(t.Id, mail.TenantId);
        Assert.Equal("ar", mail.Message.Locale);
        Assert.Equal("Contoso HR", mail.Message.FromName);
        Assert.Equal("إشعار تجريبي من HR", mail.Message.Subject);
        Assert.Contains("dir=\"rtl\"", mail.Message.Html, StringComparison.Ordinal);
        Assert.Contains($"https://{t.Key}.{ApiFactory.BaseDomain}/p/home", mail.Message.Text, StringComparison.Ordinal);
        Assert.Null(mail.Relay);
        Assert.Equal(1, await DeliveriesAsync(t, "sent"));
    }

    [Fact]
    public async Task Categories_with_email_off_by_default_send_no_email_until_the_user_turns_it_on()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        await SendAsync(t, NotificationCategories.Announcement, employee.Id);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery"));

        var updated = await SetPreferencesAsync(employee, new { categories = new[] { new { category = "announcement", email = true, digest = false } } });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        await SendAsync(t, NotificationCategories.Announcement, employee.Id);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery"));
    }

    [Fact]
    public async Task Turning_off_email_for_a_mandatory_category_has_no_effect()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        await SetPreferencesAsync(employee, new
        {
            categories = new[] { new { category = "security_notice", email = false, digest = false }, new { category = "mention", email = false, digest = false } },
        });
        var view = await (await employee.Client.GetAsync("/api/v1/me/notification-preferences")).ReadAsync<JsonElement>();
        var security = view.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("category").GetString() == "security_notice");
        Assert.True(security.GetProperty("mandatory").GetBoolean());
        Assert.True(security.GetProperty("email").GetBoolean());

        await SendAsync(t, NotificationCategories.SecurityNotice, employee.Id);
        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        await DeliverAsync(t);
        var mail = Assert.Single(Email.To(employee.Email));
        Assert.Contains("[", mail.Message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tenant_can_make_another_category_mandatory()
    {
        var t = await TenantAsync();
        var saved = await t["admin"].Client.PutAsJsonAsync("/api/v1/notifications/settings", new { extraMandatory = new[] { "announcement" }, version = 0 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        await SendAsync(t, NotificationCategories.Announcement, t["employee"].Id);
        Assert.True((await ListAsync(t["employee"])).GetProperty("items")[0].GetProperty("mandatory").GetBoolean());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery"));
    }

    [Fact]
    public async Task Digest_categories_are_combined_into_one_email_at_the_digest_time()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        var saved = await SetPreferencesAsync(employee, new
        {
            categories = new[] { new { category = "mention", email = true, digest = true }, new { category = "request_update", email = true, digest = true } },
            digestTime = "09:00",
            timeZone = "UTC",
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        await SendAsync(t, NotificationCategories.RequestUpdate, employee.Id);
        Assert.Equal(0, await DeliverAsync(t));
        Assert.Equal(2, await DeliveriesAsync(t, "digest"));

        var today = DateTimeOffset.UtcNow.Date;
        Assert.Equal(0, await DigestAsync(t, new DateTimeOffset(today.AddHours(8), TimeSpan.Zero).AddDays(1)));
        Assert.Equal(1, await DigestAsync(t, new DateTimeOffset(today.AddHours(9).AddMinutes(1), TimeSpan.Zero).AddDays(1)));
        var mail = Assert.Single(Email.To(employee.Email));
        Assert.Contains("2", mail.Message.Subject, StringComparison.Ordinal);
        Assert.Equal(2, await DeliveriesAsync(t, "sent"));
        // Once a day: a second run the same day sends nothing.
        Assert.Equal(0, await DigestAsync(t, new DateTimeOffset(today.AddHours(10), TimeSpan.Zero).AddDays(1)));
    }

    [Fact]
    public async Task Quiet_hours_hold_email_until_they_end_but_urgent_mandatory_messages_go_at_once()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        var now = DateTimeOffset.UtcNow;
        var start = TimeOnly.FromDateTime(now.AddHours(-1).UtcDateTime).ToString("HH:mm");
        var end = TimeOnly.FromDateTime(now.AddHours(2).UtcDateTime).ToString("HH:mm");
        var saved = await SetPreferencesAsync(employee, new { quietHours = new { start, end }, timeZone = "UTC" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        await SendAsync(t, new NotificationRequest([employee.Id], NotificationCategories.SiteEmergency, Sample, "/", Urgent: true));
        Assert.Equal(1, await DeliverAsync(t));
        Assert.Single(Email.To(employee.Email));

        Assert.Equal(1, await DeliverAsync(t, now.AddHours(2).AddMinutes(1)));
        Assert.Equal(2, Email.To(employee.Email).Count);
    }

    [Fact]
    public async Task A_failing_relay_is_retried_with_backoff_and_marked_failed_after_24_hours()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        Email.Answer(employee.Email, EmailOutcome.Transient);
        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(1, await DeliverAsync(t, now));
        Assert.Equal(0, await DeliverAsync(t, now.AddSeconds(30)));
        Assert.Equal(1, await DeliverAsync(t, now.AddMinutes(2)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery where attempts = 2 and status = 'pending'"));

        Assert.Equal(1, await DeliverAsync(t, now.AddHours(25)));
        Assert.Equal(1, await DeliveriesAsync(t, "failed"));
        Assert.Equal(1, await CountAsync(employee)); // The in-app notification is unaffected.
    }

    [Fact]
    public async Task A_hard_bounce_pauses_email_to_the_address_and_the_user_sees_it()
    {
        var t = await TenantAsync();
        var employee = t["employee"];
        Email.Answer(employee.Email, EmailOutcome.HardBounce);
        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        await DeliverAsync(t);
        Assert.Equal(1, await DeliveriesAsync(t, "failed"));
        Assert.Equal(1, await world.Database.OutboxCountAsync(nameof(NotificationBounced), t.Id));

        var view = await (await employee.Client.GetAsync("/api/v1/me/notification-preferences")).ReadAsync<JsonElement>();
        Assert.True(view.GetProperty("emailPaused").GetBoolean());

        Email.Answer(employee.Email, EmailOutcome.Sent);
        await SendAsync(t, NotificationCategories.Mention, employee.Id);
        await DeliverAsync(t);
        Assert.Equal(1, await DeliveriesAsync(t, "dropped"));
        Assert.Single(Email.To(employee.Email));
    }

    [Fact]
    public async Task A_member_disabled_before_delivery_gets_no_email()
    {
        var t = await TenantAsync();
        await SendAsync(t, NotificationCategories.Mention, t["other"].Id);
        await world.Database.OwnerExecInTenantAsync(t.Id, "update tenancy.tenant_membership set status = 'disabled' where user_id = @u", ("u", t["other"].Id));
        await DeliverAsync(t);
        Assert.Empty(Email.To(t["other"].Email));
        Assert.Equal(1, await DeliveriesAsync(t, "dropped"));
    }

    [Fact]
    public async Task Invalid_preferences_are_refused_with_resource_keys()
    {
        var t = await TenantAsync();
        var keys = await ErrorKeysAsync(await SetPreferencesAsync(t["employee"], new
        {
            categories = new[] { new { category = "nope", email = true, digest = false } },
            quietHours = new { start = "22:00", end = "22:00" },
            digestTime = "25:00",
            timeZone = "Mars/Base",
        }));
        Assert.Contains("validation.notification.category_unknown", keys);
        Assert.Contains("validation.notification.quiet_hours_invalid", keys);
        Assert.Contains("validation.notification.digest_time_invalid", keys);
        Assert.Contains("validation.notification.time_zone_invalid", keys);
    }

    // Tenant settings and templates (US4)

    [Fact]
    public async Task Employees_cannot_read_or_change_tenant_notification_settings()
    {
        var t = await TenantAsync();
        var client = t["employee"].Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/notifications/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/notifications/settings", new { senderName = "x", version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/notifications/settings/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/notifications/templates/{Sample}/en")).StatusCode);
    }

    [Fact]
    public async Task A_tenant_relay_needs_the_capability_in_the_plan()
    {
        var t = await TenantAsync("workplace");
        var view = await (await t["admin"].Client.GetAsync("/api/v1/notifications/settings")).ReadAsync<JsonElement>();
        Assert.False(view.GetProperty("tenantRelayInPlan").GetBoolean());
        var refused = await t["admin"].Client.PutAsJsonAsync("/api/v1/notifications/settings", new
        {
            relay = new { host = "smtp.contoso.example", port = 587, startTls = true }, version = 0,
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("entitlement.not_in_package", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_tenant_relay_is_used_for_delivery_and_the_test_email_is_audited_without_secrets()
    {
        var t = await TenantAsync("workplace_plus");
        var reference = SecretReferences.For(t.Id, "smtp");
        var saved = await t["admin"].Client.PutAsJsonAsync("/api/v1/notifications/settings", new
        {
            relay = new { host = "smtp.contoso.example", port = 587, username = "mailer", secretReference = reference, startTls = true }, version = 0,
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, (await saved.ReadAsync<JsonElement>()).GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.NotificationSettingsChanged);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and changes::text like '%secret://%'", ("a", AuditActions.NotificationSettingsChanged)));

        var test = await t["admin"].Client.PostAsync("/api/v1/notifications/settings/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.Equal("sent", (await test.ReadAsync<JsonElement>()).GetProperty("outcome").GetString());
        var mail = Email.To(t["admin"].Email).Last();
        Assert.Equal("smtp.contoso.example", mail.Relay!.Host);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.NotificationRelayTested);

        await SendAsync(t, NotificationCategories.Mention, t["employee"].Id);
        await DeliverAsync(t);
        Assert.Equal("smtp.contoso.example", Assert.Single(Email.To(t["employee"].Email)).Relay!.Host);
    }

    [Fact]
    public async Task Settings_reject_invalid_values_and_stale_versions()
    {
        var t = await TenantAsync("workplace_plus");
        var admin = t["admin"].Client;
        var keys = await ErrorKeysAsync(await admin.PutAsJsonAsync("/api/v1/notifications/settings", new
        {
            senderName = "<b>HR</b>",
            relay = new { host = "smtp://x", port = 0, username = "u", startTls = true },
            extraMandatory = new[] { "nope" },
            version = 0,
        }));
        Assert.Contains("validation.notification.sender_name_invalid", keys);
        Assert.Contains("validation.notification.relay_host_invalid", keys);
        Assert.Contains("validation.notification.relay_port_invalid", keys);
        Assert.Contains("validation.notification.secret_reference_required", keys);
        Assert.Contains("validation.notification.category_unknown", keys);

        var otherTenantSecret = await admin.PutAsJsonAsync("/api/v1/notifications/settings", new
        {
            relay = new { host = "smtp.contoso.example", port = 587, username = "u", secretReference = SecretReferences.For(Guid.NewGuid(), "smtp"), startTls = true }, version = 0,
        });
        Assert.Contains("validation.notification.secret_reference_invalid", await ErrorKeysAsync(otherTenantSecret));

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/notifications/settings", new { senderName = "HR", version = 0 })).StatusCode);
        var stale = await admin.PutAsJsonAsync("/api/v1/notifications/settings", new { senderName = "HR 2", version = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task A_template_override_must_keep_the_platform_parameters_and_changes_the_text()
    {
        var t = await TenantAsync();
        var admin = t["admin"].Client;
        var missing = await admin.PutAsJsonAsync($"/api/v1/notifications/templates/{Sample}/en", new { title = "Hello", body = "No params" });
        var keys = await ErrorKeysAsync(missing);
        Assert.Contains("validation.notification.template_parameter_missing", keys);
        var unknown = await admin.PutAsJsonAsync($"/api/v1/notifications/templates/{Sample}/en", new { title = "From {label} {secret}", body = "{count}" });
        Assert.Contains("validation.notification.template_parameter_unknown", await ErrorKeysAsync(unknown));

        var saved = await admin.PutAsJsonAsync($"/api/v1/notifications/templates/{Sample}/en", new { title = "Note from {label}", body = "You have {count}." });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True((await saved.ReadAsync<JsonElement>()).GetProperty("overridden").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.NotificationTemplateOverridden);

        await SendAsync(t, NotificationCategories.Announcement, t["employee"].Id);
        Assert.Equal("Note from HR", (await ListAsync(t["employee"])).GetProperty("items")[0].GetProperty("title").GetString());

        var reset = await admin.DeleteAsync($"/api/v1/notifications/templates/{Sample}/en");
        Assert.False((await reset.ReadAsync<JsonElement>()).GetProperty("overridden").GetBoolean());
        Assert.Equal("Sample notification from HR", (await ListAsync(t["employee"])).GetProperty("items")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_template_override_in_one_tenant_does_not_change_another()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        await a["admin"].Client.PutAsJsonAsync($"/api/v1/notifications/templates/{Sample}/en", new { title = "A says {label}", body = "{count}" });
        await SendAsync(b, NotificationCategories.Announcement, b["employee"].Id);
        Assert.Equal("Sample notification from HR", (await ListAsync(b["employee"])).GetProperty("items")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Old_notifications_are_purged_after_90_days()
    {
        var t = await TenantAsync();
        await SendAsync(t, NotificationCategories.Announcement, t["employee"].Id);
        var removed = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var n = await sp.GetRequiredService<EmailDelivery>().PurgeAsync(DateTimeOffset.UtcNow.AddDays(91), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });
        Assert.Equal(1, removed);
        Assert.Equal(0, await CountAsync(t["employee"]));
    }
}
