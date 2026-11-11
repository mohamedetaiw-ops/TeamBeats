using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Hr.Journeys;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T019 part 2 (US4 scenario 3, FR-016), with the personal email of spec 019: pre-boarding is off until a
/// tenant-wide HR officer turns it on. A hire HR knows before their account exists then gets one email at the personal
/// address HR holds, with a time-limited link to a page that shows only the pre-boarding text of the template. Nothing
/// else about the tenant reaches the page, the address never leaves HR, and other tenants' hosts do not open the link.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed partial class PreboardingTests(TenantWorld world)
{
    private const string Templates = "/api/v1/hr/journey-templates";
    private const string Settings = "/api/v1/hr/journey-settings";
    private const string Personal = "joiner.personal@example.org";

    private TestEmailSender Email => world.Factory.Email;

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<(TestTenant T, Guid Gulf)> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pb-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "mona", "hr_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        return (t, gulf);
    }

    private Task<DateOnly> TodayAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById((await sp.GetRequiredService<ITenantSettingsReader>().GetLocaleSettingsAsync()).TimeZone);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        });

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetProperty("key").GetString()!)).ToArray();
        }
        return body.TryGetProperty("detailKey", out var detail) ? [detail.GetString()!] : [body.GetProperty("code").GetString()!];
    }

    private static async Task TurnOnAsync(TestTenant t)
    {
        var current = await t["admin"].Client.GetAsync(Settings);
        var put = new HttpRequestMessage(HttpMethod.Put, Settings) { Content = JsonContent.Create(new { preboardingEnabled = true }) };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        await OkAsync(t["admin"].Client.SendAsync(put));
    }

    private static Task TemplateAsync(TestTenant t, int offset = -7) =>
        OkAsync(t["admin"].Client.PostAsJsonAsync(Templates, new
        {
            key = "welcome", kind = "onboarding", names = Names("Welcome", "ترحيب"), startOn = "hire",
            steps = new object[]
            {
                new
                {
                    key = "before", names = Names("Before you start", "قبل أن تبدأ"), offsetDays = offset, assignee = "person", type = "preboarding",
                    content = Names("Bring your ID to reception at 9.\nParking is on level 2.", "أحضر هويتك إلى الاستقبال في التاسعة."),
                },
                new { key = "laptop", names = Names("Issue the laptop", "تسليم الحاسوب"), offsetDays = 0, assignee = "permission", permission = Permissions.HrManage },
            },
        }), HttpStatusCode.Created);

    /// <summary>A record HR holds for a joiner with no account, its org code mapped to the unit.</summary>
    private async Task HireRecordAsync(TestTenant t, string record, Guid unit, string? personalEmail = Personal)
    {
        var connection = Guid.NewGuid();
        var data = new Dictionary<string, string> { ["record_id"] = record, ["status"] = "active", ["org_unit_code"] = "GULF", ["name_en"] = "New Joiner" };
        if (personalEmail is not null)
        {
            data["personal_email"] = personalEmail;
        }
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into integrations.connection (tenant_id, id, kind, display_name, status, enabled, rate_limit_per_minute)
            values (@t, @c, 'hr_rest', 'HR', 'healthy', true, 600);
            insert into integrations.hr_employee (tenant_id, record_id, connection_id, data, user_id, match_state, first_seen_at, last_seen_at, last_run_id)
            values (@t, @r, @c, @data::jsonb, null, 'unmatched', now(), now(), gen_random_uuid());
            insert into integrations.hr_org_code (tenant_id, code, unit_id, first_seen_at, last_seen_at) values (@t, 'GULF', @u, now(), now()) on conflict do nothing
            """, ("t", t.Id), ("c", connection), ("r", record), ("data", JsonSerializer.Serialize(data)), ("u", unit));
    }

    private Task HiredAsync(TestTenant t, string record, DateOnly start) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<JourneyStarter>().HandleAsync(new EmployeeHired(null, record, start, Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private Task<int> SendDueAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var sent = await sp.GetRequiredService<Preboarding>().SendDueAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return sent;
        });

    private Task<string?> StatusAsync(TestTenant t, string record) =>
        world.Database.TenantScalarAsync<string>(t.Id, "select status from hr.preboarding where record_id = @r", ("r", record));

    private string TokenSentTo(TestTenant t, string address)
    {
        var mail = Assert.Single(Email.To(address), m => m.TenantId == t.Id);
        return LinkPattern().Match(mail.Message.Text).Groups[1].Value;
    }

    [GeneratedRegex("/preboarding/([A-Za-z0-9_-]{43})")]
    private static partial Regex LinkPattern();

    [Fact]
    public async Task Pre_boarding_is_off_until_a_tenant_wide_HR_officer_turns_it_on_against_the_version_read()
    {
        var (t, _) = await FreshAsync();
        var view = await OkAsync(t["admin"].Client.GetAsync(Settings));
        Assert.False(view.GetProperty("preboardingEnabled").GetBoolean());
        Assert.True(view.GetProperty("canChange").GetBoolean());

        // An HR officer of one unit reads it and cannot change it; someone without HR rights cannot read it.
        var unit = await OkAsync(t["hr_gulf"].Client.GetAsync(Settings));
        Assert.False(unit.GetProperty("canChange").GetBoolean());
        var denied = new HttpRequestMessage(HttpMethod.Put, Settings) { Content = JsonContent.Create(new { preboardingEnabled = true }) };
        denied.Headers.TryAddWithoutValidation("If-Match", "\"v0\"");
        Assert.Equal(HttpStatusCode.Forbidden, (await t["hr_gulf"].Client.SendAsync(denied)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync(Settings)).StatusCode);

        var stale = new HttpRequestMessage(HttpMethod.Put, Settings) { Content = JsonContent.Create(new { preboardingEnabled = true }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"v7\"");
        var conflict = await t["admin"].Client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("conflict.journey.changed", await ErrorKeysAsync(conflict));

        await TurnOnAsync(t);
        var on = await OkAsync(t["admin"].Client.GetAsync(Settings));
        Assert.True(on.GetProperty("preboardingEnabled").GetBoolean());
        Assert.Equal(1, on.GetProperty("version").GetInt32());
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a",
            ("a", AuditActions.HrJourneySettingsChanged)));
    }

    [Fact]
    public async Task A_hire_without_an_account_gets_one_email_at_the_personal_address_whose_link_shows_only_the_pre_boarding_text()
    {
        var (t, gulf) = await FreshAsync();
        await TemplateAsync(t);
        await HireRecordAsync(t, "H-1", gulf);
        var start = (await TodayAsync(t)).AddDays(3);

        // Off: nothing is planned.
        await HiredAsync(t, "H-1", start);
        Assert.Null(await StatusAsync(t, "H-1"));

        await TurnOnAsync(t);
        await HiredAsync(t, "H-1", start);
        Assert.Equal(PreboardingStatuses.Scheduled, await StatusAsync(t, "H-1"));
        Assert.Equal(1, await SendDueAsync(t));
        Assert.Equal(0, await SendDueAsync(t));
        Assert.Equal(PreboardingStatuses.Sent, await StatusAsync(t, "H-1"));
        var token = TokenSentTo(t, Personal);
        Assert.Equal(43, token.Length);
        var anonymous = world.Factory.TenantClient(t.Key);
        var page = await OkAsync(anonymous.GetAsync($"/api/v1/preboarding/{token}"));
        Assert.Equal(start.ToString("yyyy-MM-dd"), page.GetProperty("startDate").GetString());
        var step = Assert.Single(page.GetProperty("steps").EnumerateArray());
        Assert.Equal("before", step.GetProperty("key").GetString());
        Assert.Contains("Bring your ID", step.GetProperty("content").GetProperty("en").GetString());
        Assert.Equal(start.AddDays(-7).ToString("yyyy-MM-dd"), step.GetProperty("showsOn").GetString());
        // No other step, no names of people, no address.
        var text = page.GetRawText();
        Assert.DoesNotContain("laptop", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Personal, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New Joiner", text, StringComparison.Ordinal);
        Assert.NotNull(await world.Database.TenantScalarAsync<DateTime?>(t.Id, "select opened_at from hr.preboarding where record_id = 'H-1'"));

        // The audit names the record, never the address.
        var audit = await world.Database.TenantScalarAsync<string>(t.Id, "select changes::text from audit.audit_event where action = @a", ("a", AuditActions.HrPreboardingSent));
        Assert.Contains("H-1", audit);
        Assert.DoesNotContain(Personal, audit!, StringComparison.OrdinalIgnoreCase);

        var settings = await OkAsync(t["admin"].Client.GetAsync(Settings));
        Assert.Equal(1, settings.GetProperty("preboarding").GetProperty("sent").GetInt32());
        Assert.Equal(1, settings.GetProperty("preboarding").GetProperty("opened").GetInt32());

        // A wrong or malformed token, another tenant's host, and the platform host are all not found.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/preboarding/{new string('A', 43)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/preboarding/short")).StatusCode);
        var other = await world.CreateTenantAsync("pb-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.TenantClient(other.Key).GetAsync($"/api/v1/preboarding/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.PlatformClient().GetAsync($"/api/v1/preboarding/{token}")).StatusCode);
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(other.Id, "select count(*) from hr.preboarding"));

        // The link ends: expired is not found like any other.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update hr.preboarding set expires_at = now() - interval '1 minute' where record_id = 'H-1'");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/preboarding/{token}")).StatusCode);
    }

    [Fact]
    public async Task The_link_ends_with_the_start_date_and_an_invitation_is_cancelled_when_the_hire_gets_an_account_or_HR_turns_it_off()
    {
        var (t, gulf) = await FreshAsync();
        await TemplateAsync(t, offset: -30);
        await TurnOnAsync(t);
        var today = await TodayAsync(t);

        // Sent now: the link lasts until the end of the start date, not 30 days.
        await HireRecordAsync(t, "H-2", gulf, "second.joiner@example.org");
        await HiredAsync(t, "H-2", today.AddDays(2));
        Assert.Equal(1, await SendDueAsync(t));
        var expires = await world.Database.TenantScalarAsync<DateTime>(t.Id, "select expires_at from hr.preboarding where record_id = 'H-2'");
        Assert.InRange(expires.ToUniversalTime(), DateTime.UtcNow.AddDays(2), DateTime.UtcNow.AddDays(4));

        // The hire gets an account before it goes: cancelled, nothing sent.
        await HireRecordAsync(t, "H-3", gulf, "third.joiner@example.org");
        await HiredAsync(t, "H-3", today.AddDays(5));
        await world.Database.OwnerExecInTenantAsync(t.Id, "update integrations.hr_employee set user_id = @u, match_state = 'matched' where record_id = 'H-3'",
            ("u", t["mona"].Id));
        Assert.Equal(0, await SendDueAsync(t));
        Assert.Equal(PreboardingStatuses.Cancelled, await StatusAsync(t, "H-3"));
        Assert.Empty(Email.To("third.joiner@example.org"));

        // No personal email in HR: nothing is planned.
        await HireRecordAsync(t, "H-4", gulf, null);
        await HiredAsync(t, "H-4", today.AddDays(5));
        Assert.Null(await StatusAsync(t, "H-4"));

        // Turning it off cancels what is still waiting; a relay failure keeps it waiting and a bounce ends it.
        await HireRecordAsync(t, "H-5", gulf, "fifth.joiner@example.org");
        Email.Answer("fifth.joiner@example.org", EmailOutcome.Transient);
        await HiredAsync(t, "H-5", today.AddDays(5));
        Assert.Equal(0, await SendDueAsync(t));
        Assert.Equal(PreboardingStatuses.Scheduled, await StatusAsync(t, "H-5"));
        Assert.Equal(1, await world.Database.TenantScalarAsync<int>(t.Id, "select attempts from hr.preboarding where record_id = 'H-5'"));
        await HireRecordAsync(t, "H-6", gulf, "sixth.joiner@example.org");
        Email.Answer("sixth.joiner@example.org", EmailOutcome.HardBounce);
        await HiredAsync(t, "H-6", today.AddDays(5));
        await SendDueAsync(t);
        Assert.Equal(PreboardingStatuses.Failed, await StatusAsync(t, "H-6"));

        var current = await t["admin"].Client.GetAsync(Settings);
        var off = new HttpRequestMessage(HttpMethod.Put, Settings) { Content = JsonContent.Create(new { preboardingEnabled = false }) };
        off.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        await OkAsync(t["admin"].Client.SendAsync(off));
        Assert.Equal(PreboardingStatuses.Cancelled, await StatusAsync(t, "H-5"));
    }

    [Fact]
    public async Task A_pre_boarding_step_belongs_to_a_hire_started_onboarding_before_day_one_and_needs_its_text()
    {
        var (t, _) = await FreshAsync();
        async Task<string[]> RefusedAsync(string kind, string startOn, object step)
        {
            var response = await t["admin"].Client.PostAsJsonAsync(Templates, new { key = "t_" + Guid.NewGuid().ToString("N")[..6], kind, names = Names("T", "ت"), startOn, steps = new[] { step } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await ErrorKeysAsync(response);
        }
        var text = Names("Hello", "مرحبا");
        Assert.Contains("validation.journey.step_type", await RefusedAsync("onboarding", "hire",
            new { key = "pb", names = text, offsetDays = 0, assignee = "person", type = "preboarding", content = text }));
        Assert.Contains("validation.journey.step_type", await RefusedAsync("onboarding", "account",
            new { key = "pb", names = text, offsetDays = -3, assignee = "person", type = "preboarding", content = text }));
        Assert.Contains("validation.journey.step_type", await RefusedAsync("offboarding", "termination",
            new { key = "pb", names = text, offsetDays = -3, assignee = "person", type = "preboarding", content = text }));
        Assert.Contains("validation.journey.step_type", await RefusedAsync("onboarding", "hire",
            new { key = "pb", names = text, offsetDays = -3, assignee = "manager", type = "preboarding", content = text }));
        Assert.Contains("validation.journey.content", await RefusedAsync("onboarding", "hire",
            new { key = "pb", names = text, offsetDays = -3, assignee = "person", type = "preboarding" }));
        Assert.Contains("validation.journey.content", await RefusedAsync("onboarding", "hire",
            new { key = "pb", names = text, offsetDays = -3, assignee = "person", type = "preboarding", content = Names(new string('x', 2001), "ت") }));
        Assert.Contains("validation.journey.content", await RefusedAsync("onboarding", "hire",
            new { key = "pb", names = text, offsetDays = 1, assignee = "person", content = text }));

        // A joiner with an account reads the same text as their own step.
        await TemplateAsync(t);
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<JourneyStarter>().HandleAsync(new EmployeeHired(t["mona"].Id, "H-9", (await TodayAsync(t)).AddDays(10), Guid.NewGuid(), Guid.NewGuid()),
                CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        var mine = (await OkAsync(t["mona"].Client.GetAsync("/api/v1/me/journeys"))).EnumerateArray().Single();
        var journey = await OkAsync(t["mona"].Client.GetAsync($"/api/v1/hr/journeys/{mine.GetProperty("id").GetGuid()}"));
        var step = journey.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "before");
        Assert.Equal("preboarding", step.GetProperty("type").GetString());
        Assert.Contains("Bring your ID", step.GetProperty("content").GetProperty("en").GetString());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from hr.preboarding"));
    }

    [Fact]
    public async Task The_personal_email_never_leaves_HR_with_a_persons_record()
    {
        var (t, gulf) = await FreshAsync();
        await HireRecordAsync(t, "H-7", gulf);
        await world.Database.OwnerExecInTenantAsync(t.Id, "update integrations.hr_employee set user_id = @u, match_state = 'matched' where record_id = 'H-7'",
            ("u", t["mona"].Id));
        var records = await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IHrRecords>().MatchedAsync([t["mona"].Id]));
        var record = Assert.Single(records);
        Assert.Equal("GULF", record.Fields[HrFields.OrgUnitCode]);
        Assert.False(record.Fields.ContainsKey(HrFields.PersonalEmail));
        // Matched to an account: no longer a pre-hire.
        Assert.Null(await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IHrRecords>().PreHireAsync("H-7")));
    }
}
