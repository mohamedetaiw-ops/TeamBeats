using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Booking;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 031 R3 T013 (US8, FR-022 to FR-025): a host registers a visitor, who gets an email with a code; the limits on
/// time, length and count; changing and cancelling tell the visitor; reception of the site only sees the day and checks
/// in by code, on the visit day only, or from the list, and the host is told; check-out; the email is retried when the
/// relay is down; a host who leaves has their visits cancelled; the details go 30 days after the visit and reports show
/// counts; the package, scope and tenant boundaries. Names and addresses are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BookingVisitTests(TenantWorld world)
{
    private const string Api = "/api/v1/booking";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Site, TimeZoneInfo Zone);

    private TestEmailSender Email => world.Factory.Email;

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
        var problem = JsonDocument.Parse(text).RootElement;
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return problem.GetProperty("detailKey").GetString()!;
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    /// <summary>A site time zone where it is now between 06:00 and 18:00, so a visit later today fits in the day.</summary>
    private static TimeZoneInfo Daytime()
    {
        foreach (var id in new[] { "Asia/Riyadh", "Asia/Tokyo", "Europe/London", "America/New_York", "Pacific/Honolulu" })
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            if (TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Hour is >= 6 and < 18)
            {
                return zone;
            }
        }
        throw new InvalidOperationException("No daytime zone.");
    }

    private static DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);

    /// <summary>The next quarter hour plus fifteen minutes, local to the site.</summary>
    private static TimeOnly Soon(TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var minutes = (local.Hour * 60 + local.Minute) / 15 * 15 + 30;
        return new TimeOnly(minutes / 60, minutes % 60);
    }

    /// <summary>Hana is a facilities administrator for Gulf, which owns the site; Omar and Laila are employees there.</summary>
    private async Task<Org> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("vi-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var gulf = await world.CreateUnitAsync(t, "Gulf", null, "organization");
        foreach (var name in new[] { "omar", "laila", "hana" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "hana", BuiltInRoles.FacilitiesAdministrator, "org_subtree", gulf);
        var zone = Daytime();
        var site = Id(await OkAsync(t["hana"].Client.PostAsJsonAsync($"{Api}/manage/sites", new
        {
            names = new { en = "Head office", ar = "المكتب الرئيسي" }, orgUnitId = gulf, timeZone = zone.Id, opensAt = new TimeOnly(0, 0), closesAt = new TimeOnly(23, 59),
        }), HttpStatusCode.Created));
        return new Org(t, gulf, site, zone);
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, Org o, DateOnly date, TimeOnly start, TimeOnly end, string email, string name = "Nadia Haddad",
        Guid? bookingId = null) =>
        client.PostAsJsonAsync($"{Api}/visits", new { siteId = o.Site, date, start, end, name, email, company = "Example Trading", bookingId });

    private Task<BookingMaintenance.Run> MaintainAsync(Org o, DateTimeOffset now) =>
        world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            var run = await sp.GetRequiredService<BookingMaintenance>().RunDueAsync(now, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });

    private Task<long> AuditAsync(Org o, string action) =>
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from audit.audit_event where action = @a", ("a", action));

    [Fact]
    public async Task A_host_expects_a_visitor_who_reception_checks_in_by_code_on_the_day_and_the_host_is_told()
    {
        var o = await SetupAsync();
        var omar = o.T["omar"].Client;
        var hana = o.T["hana"].Client;
        var today = Today(o.Zone);
        var start = Soon(o.Zone);
        var address = $"nadia.{Guid.NewGuid():N}@visitor.example";

        // Limits (section 9): not in the past, at most 90 days ahead, 15 minutes to 12 hours, a real address.
        Assert.Equal("validation.booking.visit_window", await ProblemKeyAsync(RegisterAsync(omar, o, today.AddDays(-1), new TimeOnly(10, 0), new TimeOnly(11, 0), address),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.visit_window", await ProblemKeyAsync(RegisterAsync(omar, o, today.AddDays(91), new TimeOnly(10, 0), new TimeOnly(11, 0), address),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.visit_length", await ProblemKeyAsync(RegisterAsync(omar, o, today.AddDays(2), new TimeOnly(10, 0), new TimeOnly(10, 10), address),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.visit_length", await ProblemKeyAsync(RegisterAsync(omar, o, today.AddDays(2), new TimeOnly(6, 0), new TimeOnly(18, 30), address),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.visitor_email", await ProblemKeyAsync(RegisterAsync(omar, o, today.AddDays(2), new TimeOnly(10, 0), new TimeOnly(11, 0), "not an address"),
            HttpStatusCode.BadRequest));
        // Only the host's own live booking at this site may go with the visit.
        Assert.Equal("validation.booking.visit_booking", await ProblemKeyAsync(RegisterAsync(omar, o, today.AddDays(2), new TimeOnly(10, 0), new TimeOnly(11, 0), address,
            bookingId: Guid.NewGuid()), HttpStatusCode.BadRequest));

        // US8 scenario 1: Omar expects Nadia later today; she gets an email with the site, time, host and code.
        var visit = await OkAsync(RegisterAsync(omar, o, today, start, start.AddHours(1), address), HttpStatusCode.Created);
        var code = visit.GetProperty("code").GetString()!;
        Assert.Matches("^[A-HJ-NP-Z2-9]{8}$", code);
        Assert.Equal(("expected", "sent"), (visit.GetProperty("status").GetString(), visit.GetProperty("emailState").GetString()));
        var mail = Assert.Single(Email.To(address)).Message;
        Assert.Contains("Head office", mail.Subject, StringComparison.Ordinal);
        Assert.Contains(code, mail.Text, StringComparison.Ordinal);
        Assert.Contains(start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), mail.Text, StringComparison.Ordinal);
        Assert.Single(await OkAsync(omar.GetAsync($"{Api}/visits/mine")).ContinueWith(t => t.Result.EnumerateArray().ToList()));
        // The audit never carries the visitor's details.
        Assert.Equal(1, await AuditAsync(o, AuditActions.BookingVisitRegistered));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from audit.audit_event where changes::text ilike '%nadia%'"));

        // Laila is not reception; another host cannot see or cancel Omar's visit.
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["laila"].Client.GetAsync($"{Api}/reception/sites/{o.Site}/visits")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["laila"].Client.PostAsync($"{Api}/visits/{Id(visit)}:cancel", null)).StatusCode);

        // US8 scenario 2: reception sees today's visitors with the host and no code, and checks Nadia in by her code.
        var rights = await OkAsync(hana.GetAsync($"{Api}/rights"));
        Assert.True(rights.GetProperty("reception").GetBoolean());
        Assert.Single((await OkAsync(hana.GetAsync($"{Api}/reception/sites"))).EnumerateArray());
        var day = await OkAsync(hana.GetAsync($"{Api}/reception/sites/{o.Site}/visits"));
        Assert.True(day.GetProperty("today").GetBoolean());
        var row = Assert.Single(day.GetProperty("visits").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("code").ValueKind);
        Assert.False(string.IsNullOrEmpty(row.GetProperty("hostName").GetString()));
        Assert.Equal("validation.booking.visit_code", await ProblemKeyAsync(hana.PostAsJsonAsync($"{Api}/reception/sites/{o.Site}/visits:check-in", new { code = "ABC" }),
            HttpStatusCode.BadRequest));
        Assert.Equal("conflict.booking.visit_code", await ProblemKeyAsync(hana.PostAsJsonAsync($"{Api}/reception/sites/{o.Site}/visits:check-in", new { code = "ZZZZZZZZ" }),
            HttpStatusCode.Conflict));
        var arrived = await OkAsync(hana.PostAsJsonAsync($"{Api}/reception/sites/{o.Site}/visits:check-in", new { code = $" {code[..4].ToLowerInvariant()}-{code[4..]} " }));
        Assert.Equal("arrived", arrived.GetProperty("status").GetString());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k",
            ("u", o.T["omar"].Id), ("k", Visits.ArrivedTemplate)));
        // A second check-in changes nothing; check-out follows arrival.
        await OkAsync(hana.PostAsync($"{Api}/reception/visits/{Id(visit)}:check-in", null));
        Assert.Equal("left", (await OkAsync(hana.PostAsync($"{Api}/reception/visits/{Id(visit)}:check-out", null))).GetProperty("status").GetString());
        Assert.Equal(2, await AuditAsync(o, AuditActions.BookingVisitorCheckedIn));
        Assert.Equal("conflict.booking.visit_closed", await ProblemKeyAsync(omar.PostAsync($"{Api}/visits/{Id(visit)}:cancel", null), HttpStatusCode.Conflict));

        // A code works only on the visit day: a visit next week is not checked in today, by code or from the list.
        var later = await OkAsync(RegisterAsync(omar, o, today.AddDays(7), new TimeOnly(10, 0), new TimeOnly(11, 0), $"sami.{Guid.NewGuid():N}@visitor.example", "Sami Aziz"),
            HttpStatusCode.Created);
        Assert.Equal("conflict.booking.visit_code", await ProblemKeyAsync(hana.PostAsJsonAsync($"{Api}/reception/sites/{o.Site}/visits:check-in",
            new { code = later.GetProperty("code").GetString() }), HttpStatusCode.Conflict));
        Assert.Equal("conflict.booking.visit_not_today", await ProblemKeyAsync(hana.PostAsync($"{Api}/reception/visits/{Id(later)}:check-in", null), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Changing_or_cancelling_tells_the_visitor_and_leavers_relays_and_the_thirty_day_purge_are_handled()
    {
        var o = await SetupAsync();
        var omar = o.T["omar"].Client;
        var next = Today(o.Zone).AddDays(3);
        var address = $"rania.{Guid.NewGuid():N}@visitor.example";

        // US8 scenario 3: the time changes; Rania is told, and the code stays.
        var visit = await OkAsync(RegisterAsync(omar, o, next, new TimeOnly(9, 0), new TimeOnly(10, 0), address, "Rania Saleh"), HttpStatusCode.Created);
        var code = visit.GetProperty("code").GetString();
        Assert.Equal("conflict.booking.changed", await ProblemKeyAsync(omar.PutAsJsonAsync($"{Api}/visits/{Id(visit)}", new
        {
            siteId = o.Site, date = next, start = new TimeOnly(14, 0), end = new TimeOnly(15, 0), name = "Rania Saleh", email = address, version = 99,
        }), HttpStatusCode.Conflict));
        var changed = await OkAsync(omar.PutAsJsonAsync($"{Api}/visits/{Id(visit)}", new
        {
            siteId = o.Site, date = next, start = new TimeOnly(14, 0), end = new TimeOnly(15, 0), name = "Rania Saleh", email = address, version = visit.GetProperty("version").GetInt32(),
        }));
        Assert.Equal(code, changed.GetProperty("code").GetString());
        Assert.Equal(2, Email.To(address).Count);
        Assert.Contains("14:00", Email.To(address)[1].Message.Text, StringComparison.Ordinal);

        // Cancelling tells her too, and the code is gone.
        var cancelled = await OkAsync(omar.PostAsync($"{Api}/visits/{Id(visit)}:cancel", null));
        Assert.Equal(("cancelled", JsonValueKind.Null), (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("code").ValueKind));
        Assert.Equal(3, Email.To(address).Count);
        Assert.Equal(1, await AuditAsync(o, AuditActions.BookingVisitCancelled));

        // At most 20 visitors a host a day; cancelled ones do not count.
        for (var i = 0; i < Visits.MaxPerHostPerDay; i++)
        {
            await OkAsync(RegisterAsync(omar, o, next, new TimeOnly(9, 0), new TimeOnly(10, 0), $"guest{i}.{Guid.NewGuid():N}@visitor.example", $"Guest {i}"), HttpStatusCode.Created);
        }
        Assert.Equal("conflict.booking.visit_limit", await ProblemKeyAsync(RegisterAsync(omar, o, next, new TimeOnly(9, 0), new TimeOnly(10, 0), address), HttpStatusCode.Conflict));

        // The relay is down: the email stays pending and the clock sends it later.
        var laila = o.T["laila"].Client;
        var down = $"karim.{Guid.NewGuid():N}@visitor.example";
        Email.Answer(down, EmailOutcome.Transient);
        var waiting = await OkAsync(RegisterAsync(laila, o, next, new TimeOnly(11, 0), new TimeOnly(12, 0), down, "Karim Nasser"), HttpStatusCode.Created);
        Assert.Equal("pending", waiting.GetProperty("emailState").GetString());
        Email.Answer(down, EmailOutcome.Sent);
        await MaintainAsync(o, DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal("sent", (await OkAsync(laila.GetAsync($"{Api}/visits/{Id(waiting)}"))).GetProperty("emailState").GetString());

        // Laila leaves: her future visit is cancelled and Karim is told.
        await OkAsync(o.T["admin"].Client.PostAsync($"/api/v1/admin/users/{o.T["laila"].Id}:disable", null), HttpStatusCode.NoContent);
        await world.DispatchTenantAsync(o.T.Id);
        Assert.Equal("left_company", await world.Database.TenantScalarAsync<string>(o.T.Id, "select end_reason from booking.visit where id = @id", ("id", Id(waiting))));
        Assert.Contains(Email.To(down), m => m.Message.Subject.Contains("cancelled", StringComparison.OrdinalIgnoreCase));

        // Reports show counts only.
        var report = await OkAsync(o.T["hana"].Client.GetAsync($"{Api}/reports/utilization?site={o.Site}&from={next:yyyy-MM-dd}&to={next:yyyy-MM-dd}"));
        Assert.Equal((20, 0, 2), (report.GetProperty("visits").GetProperty("expected").GetInt32(), report.GetProperty("visits").GetProperty("arrived").GetInt32(),
            report.GetProperty("visits").GetProperty("cancelled").GetInt32()));
        Assert.DoesNotContain("Guest", report.GetRawText(), StringComparison.Ordinal);

        // FR-025: thirty days after the visit the name, email, company and code are gone; the counts stay.
        await MaintainAsync(o, DateTimeOffset.UtcNow.AddDays(Visits.RetentionDays + 5));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from booking.visit where visitor_name is not null or visitor_email is not null or code is not null"));
        Assert.Equal(22L, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from booking.visit where purged_at is not null"));
        report = await OkAsync(o.T["hana"].Client.GetAsync($"{Api}/reports/utilization?site={o.Site}&from={next:yyyy-MM-dd}&to={next:yyyy-MM-dd}"));
        Assert.Equal(20, report.GetProperty("visits").GetProperty("expected").GetInt32());
    }

    [Fact]
    public async Task Visitors_need_the_package_reception_needs_the_site_scope_and_visits_stay_in_their_tenant()
    {
        var o = await SetupAsync();
        var visit = await OkAsync(RegisterAsync(o.T["omar"].Client, o, Today(o.Zone).AddDays(2), new TimeOnly(10, 0), new TimeOnly(11, 0),
            $"lina.{Guid.NewGuid():N}@visitor.example", "Lina Farah"), HttpStatusCode.Created);

        // Another tenant reaches neither the visit nor the site's reception.
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["omar"].Client.GetAsync($"{Api}/visits/{Id(visit)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["hana"].Client.GetAsync($"{Api}/reception/sites/{o.Site}/visits")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["hana"].Client.PostAsync($"{Api}/reception/visits/{Id(visit)}:check-in", null)).StatusCode);

        // A facilities administrator of another unit works no reception here.
        await world.SeedUserAsync(o.T, "kofi", MembershipTypes.Employee);
        var africa = await world.CreateUnitAsync(o.T, "Africa", null, "organization");
        await world.AddMembershipAsync(o.T, "kofi", "Africa");
        await world.AssignAsync(o.T, "kofi", BuiltInRoles.FacilitiesAdministrator, "org_subtree", africa);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["kofi"].Client.GetAsync($"{Api}/reception/sites/{o.Site}/visits")).StatusCode);
        Assert.Empty((await OkAsync(o.T["kofi"].Client.GetAsync($"{Api}/reception/sites"))).EnumerateArray());

        // Without booking.visitors nobody registers or receives visitors; booking still works.
        await world.SetCapabilityAsync(o.T, Capabilities.BookingVisitors, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterAsync(o.T["omar"].Client, o, Today(o.Zone).AddDays(2), new TimeOnly(10, 0), new TimeOnly(11, 0),
            "x@visitor.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["hana"].Client.GetAsync($"{Api}/reception/sites/{o.Site}/visits")).StatusCode);
        Assert.False((await OkAsync(o.T["hana"].Client.GetAsync($"{Api}/rights"))).GetProperty("reception").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await OkAsync(o.T["hana"].Client.GetAsync(
            $"{Api}/reports/utilization?site={o.Site}&from={Today(o.Zone):yyyy-MM-dd}&to={Today(o.Zone):yyyy-MM-dd}"))).GetProperty("visits").ValueKind);
    }
}
