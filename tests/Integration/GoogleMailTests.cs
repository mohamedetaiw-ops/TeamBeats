using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Integrations.Google;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 018 R3 T022 (FR-024): Gmail behind the mail port. My mail reads the employee's own Gmail Inbox as their own
/// address with the metadata scope only, so headers and labels and never a body; Focused is Gmail's Primary category; a
/// person outside the Google domain has no mailbox to read; an outage shows the last answer with its age; another tenant
/// never reads it, and a plan without connectors or Microsoft 365 is refused.
/// </summary>
public sealed partial class GoogleWorkspaceTests
{
    private const string MyMail = "/api/v1/me/mail";

    [Fact]
    public async Task My_mail_reads_the_employees_own_gmail_inbox_with_headers_only_and_primary_as_focused()
    {
        var s = await SetupAsync([GoogleFeatures.Mail]);
        var test = await (await s.Tenant["admin"].Client.PostAsync($"{Packs}/{s.Connection}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());

        var employee = await world.SeedUserAsync(s.Tenant, "dina", MembershipTypes.Employee);
        var now = DateTimeOffset.UtcNow;
        s.Google.Inboxes[employee.Email] =
        [
            ("m1", ["INBOX", "UNREAD", "IMPORTANT", "CATEGORY_PERSONAL"], "\"Omar Saleh\" <omar@partner.example>", "Budget & plan", now.AddMinutes(-5), "multipart/mixed"),
            ("m2", ["INBOX", "CATEGORY_PROMOTIONS"], "news@shop.example", "Offers", now.AddMinutes(-2), "text/plain"),
            ("m3", ["INBOX", "CATEGORY_PERSONAL"], "Lina <lina@partner.example>", "Minutes", now.AddMinutes(-30), "text/html"),
        ];
        // Someone else's Inbox in the same domain is never read.
        s.Google.Inboxes[s.Google.Admin] = [("x1", ["INBOX", "UNREAD"], "a@b.example", "Admin only", now, "text/plain")];

        var allResponse = await employee.Client.GetAsync($"{MyMail}?view=all&top=5");
        Assert.True(allResponse.IsSuccessStatusCode, await allResponse.Content.ReadAsStringAsync());
        var all = await allResponse.ReadAsync<JsonElement>();
        Assert.Equal("ok", all.GetProperty("state").GetString());
        Assert.Equal("google", all.GetProperty("source").GetString());
        Assert.Equal(1, all.GetProperty("unreadCount").GetInt32());
        Assert.StartsWith("https://mail.google.com/", all.GetProperty("composeLink").GetString(), StringComparison.Ordinal);
        var messages = all.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["m2", "m1", "m3"], messages.Select(m => m.GetProperty("id").GetString()));
        var m1 = messages[1];
        Assert.Equal("Omar Saleh", m1.GetProperty("sender").GetString());
        Assert.Equal("omar@partner.example", m1.GetProperty("senderAddress").GetString());
        Assert.Equal("Budget & plan", m1.GetProperty("subject").GetString());
        Assert.False(m1.GetProperty("isRead").GetBoolean());
        Assert.Equal("high", m1.GetProperty("importance").GetString());
        Assert.True(m1.GetProperty("hasAttachments").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m1.GetProperty("preview").ValueKind);
        Assert.DoesNotContain("Admin only", all.ToString(), StringComparison.Ordinal);

        // The token is the employee's own and carries the metadata scope only.
        Assert.Contains((employee.Email, GoogleHosts.GmailScope), s.Google.Tokens);
        Assert.DoesNotContain(s.Google.Tokens, t => t.Scope.Contains("gmail.readonly", StringComparison.Ordinal) || t.Scope.Contains("mail.google.com", StringComparison.Ordinal));

        var focused = await (await employee.Client.GetAsync($"{MyMail}?view=focused&top=5")).ReadAsync<JsonElement>();
        Assert.Equal(["m1", "m3"], focused.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetString()));

        // An outage shows the last good answer with its age.
        s.Google.GmailDown = true;
        var down = await (await employee.Client.GetAsync($"{MyMail}?view=all&top=5&refresh=true")).ReadAsync<JsonElement>();
        Assert.Equal("degraded", down.GetProperty("state").GetString());
        Assert.Equal(3, down.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Gmail_is_read_only_for_people_of_the_google_domain_and_only_in_the_tenant_and_plan_that_have_it()
    {
        var s = await SetupAsync([GoogleFeatures.Mail]);
        var guest = await world.SeedUserAsync(s.Tenant, "guest", MembershipTypes.Employee);
        await world.Database.TenantScalarAsync<long>(s.Tenant.Id,
            "with u as (update identity.user_account set email = 'guest@elsewhere.example' where id = @u returning 1) select count(*) from u", ("u", guest.Id));
        var before = s.Google.Tokens.Count;
        var outside = await (await guest.Client.GetAsync(MyMail)).ReadAsync<JsonElement>();
        Assert.Equal("no_mailbox", outside.GetProperty("state").GetString());
        Assert.Equal("google", outside.GetProperty("source").GetString());
        Assert.Equal(before, s.Google.Tokens.Count);

        // Without the mail feature on the connection, My mail stays off.
        var calendarOnly = await SetupAsync([GoogleFeatures.Calendar]);
        var off = await (await calendarOnly.Tenant["admin"].Client.GetAsync(MyMail)).ReadAsync<JsonElement>();
        Assert.Equal("turned_off", off.GetProperty("state").GetString());

        // Another tenant has no Gmail from this connection.
        var other = await world.CreateTenantAsync("gw-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var theirs = await (await other["admin"].Client.GetAsync(MyMail)).ReadAsync<JsonElement>();
        Assert.NotEqual("ok", theirs.GetProperty("state").GetString());
        Assert.Equal(0, theirs.GetProperty("messages").GetArrayLength());

        // A plan with neither the connector packs nor Microsoft 365 is refused.
        var workplace = await world.CreateTenantAsync("gw-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await workplace["admin"].Client.GetAsync(MyMail)).StatusCode);
    }
    [Fact]
    public async Task The_default_home_offers_gmail_my_mail_only_while_the_google_mail_feature_is_on()
    {
        var s = await SetupAsync([GoogleFeatures.Mail]);
        var admin = s.Tenant["admin"].Client;
        var slug = "home-" + Guid.NewGuid().ToString("N")[..8];
        var created = await admin.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "H01", slug });
        var home = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var published = await admin.PostAsJsonAsync($"/api/v1/experience/pages/{home}/publish", new { reason = "home" });
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());

        var page = await (await admin.GetAsync($"/api/v1/experience/render?slug={slug}")).ReadAsync<JsonElement>();
        var grid = page.GetProperty("regions").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "grid");
        var refs = grid.GetProperty("placements").EnumerateArray().Select(p => p.GetProperty("componentRef").GetString()).ToList();
        Assert.Contains("google.my_mail@1", refs);
        Assert.DoesNotContain("outlook.my_mail@1", refs);
        var panel = (await (await admin.GetAsync($"/api/v1/me/pages/{home}/add-panel?region=grid")).ReadAsync<JsonElement>())
            .EnumerateArray().ToDictionary(i => i.GetProperty("componentRef").GetString()!);
        Assert.True(panel["google.my_mail@1"].GetProperty("eligible").GetBoolean());

        // A connection without the mail feature offers no Gmail My mail.
        var calendarOnly = await SetupAsync([GoogleFeatures.Calendar]);
        var otherSlug = "home-" + Guid.NewGuid().ToString("N")[..8];
        var otherHome = (await (await calendarOnly.Tenant["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "H01", slug = otherSlug }))
            .ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.True((await calendarOnly.Tenant["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{otherHome}/publish", new { reason = "home" })).IsSuccessStatusCode);
        var otherPanel = (await (await calendarOnly.Tenant["admin"].Client.GetAsync($"/api/v1/me/pages/{otherHome}/add-panel?region=grid")).ReadAsync<JsonElement>())
            .EnumerateArray().ToDictionary(i => i.GetProperty("componentRef").GetString()!);
        Assert.Equal("validation.personalization.feature_off", otherPanel["google.my_mail@1"].GetProperty("reasonKey").GetString());
    }
}
