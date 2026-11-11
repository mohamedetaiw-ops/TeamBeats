using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T011 (US1 scenarios 1 to 7, FR-001 to FR-005, FR-009): a request shows the balance, the working days and
/// the balance after, refuses what the balance cannot cover and a long sick leave without a document, shows overlap
/// with names and hides sensitive types from colleagues, asks the replacement, and is approved by the line manager
/// from the inbox; nobody approves their own leave. Cancel and shorten give days back, with approval when the type
/// says so. Accrual and carry-over run once per period. HR officers stay in their subtree, other tenants see nothing,
/// and a plan without <c>hr.leave</c> refuses.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LeaveTests(TenantWorld world)
{
    private const string Leave = "/api/v1/hr/leave";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("lv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "mona", "karim", "rana", "kofi", "hr_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        foreach (var name in new[] { "nadia", "hr_egypt" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Egypt");
        }
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "hr_egypt", BuiltInRoles.HrOfficer, "org_subtree", egypt);
        foreach (var name in new[] { "mona", "karim", "rana" })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[name].Id}/manager", new { managerId = t["kofi"].Id })).StatusCode);
        }
        await TypeAsync(t, "annual", Names("Annual leave", "إجازة سنوية"), new { sensitive = false, carryOverMax = 5 });
        await TypeAsync(t, "sick", Names("Sick leave", "إجازة مرضية"), new { sensitive = true, documentAfterDays = 2 });
        await PolicyAsync(t, "annual", 21, "yearly");
        await PolicyAsync(t, "sick", 10, "yearly");
        await AccrueAsync(t);
        return t;
    }

    private static async Task<JsonElement> TypeAsync(TestTenant t, string key, Dictionary<string, string> names, object options, string? ifMatch = null)
    {
        var body = JsonSerializer.SerializeToElement(options).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
        body["names"] = names;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Leave}/types/{key}") { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        var response = await t["admin"].Client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<Guid> PolicyAsync(TestTenant t, string typeKey, decimal days, string accrual, string user = "admin", Guid? unit = null)
    {
        var response = await t[user].Client.PostAsJsonAsync($"{Leave}/policies", new { typeKey, orgUnitId = unit, bands = new[] { new { days } }, accrual });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<int> AccrueAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var written = await sp.GetRequiredService<LeaveLedger>().RunAccrualAsync(default);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return written;
        });

    /// <summary>A Monday to Friday week at least two weeks ahead, inside one calendar year.</summary>
    private static (DateOnly Mon, DateOnly Fri) Week(int weeksAhead)
    {
        var d = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14 + 7 * weeksAhead);
        while (d.DayOfWeek != DayOfWeek.Monday)
        {
            d = d.AddDays(1);
        }
        while (d.Year != d.AddDays(4).Year)
        {
            d = d.AddDays(7);
        }
        return (d, d.AddDays(4));
    }

    private static object Input(string type, DateOnly start, DateOnly end, Guid? replacement = null, Guid? documentId = null, Guid? onBehalfOf = null) =>
        new { typeKey = type, start, end, replacementUserId = replacement, documentId, onBehalfOf };

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, object input, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Leave}/requests") { Content = JsonContent.Create(input) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> SubmittedAsync(HttpClient client, object input, string? key = null)
    {
        var response = await SubmitAsync(client, input, key);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<string[]> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    private static async Task<JsonElement[]> InboxAsync(HttpClient client, string source) =>
        (await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray().Where(i => i.GetProperty("source").GetString() == source).ToArray();

    private static async Task DecideAsync(HttpClient client, Guid requestId, string outcome, string source = "leave")
    {
        var task = (await InboxAsync(client, source)).Single(i => i.GetProperty("link").GetString()!.Contains(requestId.ToString(), StringComparison.Ordinal));
        var decided = await client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome, comment = outcome == "approve" ? null : "Too many people away that week" });
        Assert.True(decided.IsSuccessStatusCode, await decided.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id) => await (await client.GetAsync($"{Leave}/requests/{id}")).ReadAsync<JsonElement>();

    private static async Task<decimal> AvailableAsync(HttpClient client, string type) =>
        (await (await client.GetAsync("/api/v1/me/leave")).ReadAsync<JsonElement>()).GetProperty("balances").EnumerateArray()
            .Single(b => b.GetProperty("typeKey").GetString() == type).GetProperty("available").GetDecimal();

    /// <summary>The ledger total of one leave year, from the person's own ledger.</summary>
    private static async Task<decimal> LedgerAsync(TestTenant t, string user, string type, int year) =>
        (await (await t[user].Client.GetAsync($"{Leave}/ledger/{t[user].Id}")).ReadAsync<JsonElement>()).EnumerateArray()
            .Where(e => e.GetProperty("typeKey").GetString() == type && e.GetProperty("leaveYear").GetInt32() == year).Sum(e => e.GetProperty("delta").GetDecimal());

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<TeamBeats.Messaging.OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private static async Task<JsonElement> CalendarAsync(TestTenant t, Guid id) => await (await t["admin"].Client.GetAsync($"/api/v1/hr/calendars/{id}")).ReadAsync<JsonElement>();

    private static async Task SetHolidaysAsync(TestTenant t, Guid calendar, int year, params DateOnly[] dates)
    {
        var current = await CalendarAsync(t, calendar);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/hr/calendars/{calendar}/holidays")
        {
            Content = JsonContent.Create(new { year, holidays = dates.Select(d => new { date = d, name = new { en = "National day", ar = "اليوم الوطني" }, halfDay = false }) }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"v{current.GetProperty("version").GetInt32()}\"");
        var response = await t["admin"].Client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_holiday_added_or_removed_recounts_open_leave_gives_days_back_and_tells_the_person()
    {
        var t = await FreshAsync();
        var (mon, fri) = Week(1);
        var monas = (await SubmittedAsync(t["mona"].Client, Input("annual", mon, fri))).GetProperty("id").GetGuid();
        await DecideAsync(t["kofi"].Client, monas, "approve");
        var karims = (await SubmittedAsync(t["karim"].Client, Input("annual", mon, mon.AddDays(2)))).GetProperty("id").GetGuid();
        Assert.Equal(16m, await LedgerAsync(t, "mona", "annual", mon.Year));

        // A calendar with the same week for the Gulf changes nothing.
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/hr/calendars", new { name = new { en = "Gulf", ar = "الخليج" }, weekDays = new[] { 1, 2, 3, 4, 5 } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var calendar = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/hr/assignments/{t.Unit("Gulf")}", new { calendarId = calendar })).StatusCode);
        await DispatchAsync();
        Assert.Equal(5m, (await GetAsync(t["mona"].Client, monas)).GetProperty("days").GetDecimal());

        // A holiday on the Wednesday: approved leave takes a day less and gives it back; pending leave is counted again too.
        await SetHolidaysAsync(t, calendar, mon.Year, mon.AddDays(2));
        await DispatchAsync();
        Assert.Equal(4m, (await GetAsync(t["mona"].Client, monas)).GetProperty("days").GetDecimal());
        Assert.Equal(17m, await LedgerAsync(t, "mona", "annual", mon.Year));
        Assert.Equal(2m, (await GetAsync(t["karim"].Client, karims)).GetProperty("days").GetDecimal());
        Assert.Equal(21m, await LedgerAsync(t, "karim", "annual", mon.Year));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveRecounted, monas.ToString());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t["mona"].Id), ("k", LeaveRequests.RecountedTemplate)));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t["rana"].Id), ("k", LeaveRequests.RecountedTemplate)));

        // Removing it takes the day again.
        await SetHolidaysAsync(t, calendar, mon.Year);
        await DispatchAsync();
        Assert.Equal(5m, (await GetAsync(t["mona"].Client, monas)).GetProperty("days").GetDecimal());
        Assert.Equal(16m, await LedgerAsync(t, "mona", "annual", mon.Year));
        Assert.Equal(3m, (await GetAsync(t["karim"].Client, karims)).GetProperty("days").GetDecimal());
    }

    [Fact]
    public async Task A_request_shows_balance_and_overlap_names_the_replacement_and_is_approved_by_the_manager_from_the_inbox()
    {
        var t = await FreshAsync();
        Assert.Equal(0, await AccrueAsync(t)); // Once per period.
        var (mon, fri) = Week(0);
        Assert.Equal(21m, await AvailableAsync(t["mona"].Client, "annual"));

        // Karim is off on Wednesday (approved); Rana is off sick on Thursday (pending).
        var karims = (await SubmittedAsync(t["karim"].Client, Input("annual", mon.AddDays(2), mon.AddDays(2)))).GetProperty("id").GetGuid();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveSubmitted, karims.ToString());
        var approvedBefore = await world.Database.OutboxCountAsync(nameof(LeaveApproved), t.Id);
        await DecideAsync(t["kofi"].Client, karims, "approve");
        Assert.Equal("approved", (await GetAsync(t["karim"].Client, karims)).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveApproved, karims.ToString());
        Assert.Equal(approvedBefore + 1, await world.Database.OutboxCountAsync(nameof(LeaveApproved), t.Id));
        await SubmittedAsync(t["rana"].Client, Input("sick", mon.AddDays(3), mon.AddDays(3)));

        // Scenario 1 and 4: working days, balance after, and overlap with names; the sick type stays hidden from Mona.
        var preview = await (await t["mona"].Client.PostAsJsonAsync($"{Leave}/preview", Input("annual", mon, fri))).ReadAsync<JsonElement>();
        Assert.Equal(5m, preview.GetProperty("days").GetDecimal());
        var year = preview.GetProperty("years").EnumerateArray().Single();
        Assert.Equal(year.GetProperty("available").GetDecimal() - 5m, year.GetProperty("after").GetDecimal());
        Assert.Empty(preview.GetProperty("problems").EnumerateArray());
        var overlap = preview.GetProperty("overlap").EnumerateArray().ToDictionary(o => o.GetProperty("userId").GetGuid());
        Assert.Equal(2, overlap.Count);
        Assert.Equal(("annual", "approved", mon.AddDays(2).ToString("yyyy-MM-dd")), (overlap[t["karim"].Id].GetProperty("typeKey").GetString(),
            overlap[t["karim"].Id].GetProperty("status").GetString(), overlap[t["karim"].Id].GetProperty("dates")[0].GetString()));
        Assert.Equal(JsonValueKind.Null, overlap[t["rana"].Id].GetProperty("typeKey").ValueKind);
        Assert.False(string.IsNullOrEmpty(overlap[t["rana"].Id].GetProperty("displayName").GetString()));

        // Submitting twice with the same key makes one request; her own leave on the same dates is refused.
        var mine = await SubmittedAsync(t["mona"].Client, Input("annual", mon, fri, replacement: t["karim"].Id), key: "mona-week");
        var id = mine.GetProperty("id").GetGuid();
        Assert.Equal(id, (await SubmittedAsync(t["mona"].Client, Input("annual", mon, fri, replacement: t["karim"].Id), key: "mona-week")).GetProperty("id").GetGuid());
        Assert.Contains("validation.leave.overlaps_own", await ErrorsAsync(await SubmitAsync(t["mona"].Client, Input("annual", mon.AddDays(1), mon.AddDays(1)))));
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from hr.leave_request where user_id = '" + t["mona"].Id + "'"));

        // The approver sees the overlap with the sick type, since Rana reports to him.
        var forKofi = await GetAsync(t["kofi"].Client, id);
        Assert.True(forKofi.GetProperty("rights").GetProperty("decide").GetBoolean());
        Assert.Equal("sick", forKofi.GetProperty("overlap").EnumerateArray().Single(o => o.GetProperty("userId").GetGuid() == t["rana"].Id).GetProperty("typeKey").GetString());

        // Scenario 5: Karim is asked, declines, Mona names Rana, who accepts.
        Assert.Single(await InboxAsync(t["karim"].Client, "leave_replacement"));
        var replacementView = await GetAsync(t["karim"].Client, id);
        Assert.Equal(JsonValueKind.Null, replacementView.GetProperty("comment").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await t["karim"].Client.PostAsJsonAsync($"{Leave}/requests/{id}/replacement:answer", new { accept = false })).StatusCode);
        Assert.Empty(await InboxAsync(t["karim"].Client, "leave_replacement"));
        Assert.True((await GetAsync(t["mona"].Client, id)).GetProperty("rights").GetProperty("nameReplacement").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await t["mona"].Client.PutAsJsonAsync($"{Leave}/requests/{id}/replacement", new { userId = t["rana"].Id })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["rana"].Client.PostAsJsonAsync($"{Leave}/requests/{id}/replacement:answer", new { accept = true })).StatusCode);
        Assert.Equal("accepted", (await GetAsync(t["mona"].Client, id)).GetProperty("replacementAnswer").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["nadia"].Client.GetAsync($"{Leave}/requests/{id}")).StatusCode);

        // Nobody approves their own leave: Mona has no task, and Kofi's task is not hers to complete.
        Assert.Empty(await InboxAsync(t["mona"].Client, "leave"));
        var kofisTask = (await InboxAsync(t["kofi"].Client, "leave")).Single(i => i.GetProperty("link").GetString()!.Contains(id.ToString(), StringComparison.Ordinal));
        Assert.False((await t["mona"].Client.PostAsJsonAsync($"/api/v1/tasks/{kofisTask.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" })).IsSuccessStatusCode);

        // Scenario 6: approval takes the days from the balance of the year they fall in.
        await DecideAsync(t["kofi"].Client, id, "approve");
        Assert.Equal("approved", (await GetAsync(t["mona"].Client, id)).GetProperty("status").GetString());
        var ledger = await (await t["mona"].Client.GetAsync($"{Leave}/ledger/{t["mona"].Id}")).ReadAsync<JsonElement>();
        Assert.Contains(ledger.EnumerateArray(), e => e.GetProperty("reason").GetString() == "request" && e.GetProperty("delta").GetDecimal() == -5m
            && e.GetProperty("leaveYear").GetInt32() == mon.Year);

        // Scenario 2: a type with a two-day policy cannot cover three days.
        await TypeAsync(t, "study", Names("Study leave", "إجازة دراسية"), new { sensitive = false });
        await PolicyAsync(t, "study", 2, "yearly");
        await AccrueAsync(t);
        var (mon3, _) = Week(2);
        var tight = await (await t["mona"].Client.PostAsJsonAsync($"{Leave}/preview", Input("study", mon3, mon3.AddDays(2)))).ReadAsync<JsonElement>();
        Assert.Contains("validation.leave.balance", tight.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("key").GetString()));
        Assert.Contains("validation.leave.balance", await ErrorsAsync(await SubmitAsync(t["mona"].Client, Input("study", mon3, mon3.AddDays(2)))));

        // Scenario 3: three days of sick leave need a document; with one it goes in, and colleagues cannot read it.
        Assert.Contains("validation.leave.document_required", await ErrorsAsync(await SubmitAsync(t["rana"].Client, Input("sick", mon3, mon3.AddDays(2)))));
        var upload = new ByteArrayContent("%PDF-1.4 sick note"u8.ToArray());
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var uploaded = await t["rana"].Client.PostAsync($"{Leave}/documents?name=note.pdf", upload);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var doc = (await uploaded.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Contains("validation.leave.document_unknown", await ErrorsAsync(await SubmitAsync(t["mona"].Client, Input("sick", mon3, mon3.AddDays(2), documentId: doc))));
        var sick = (await SubmittedAsync(t["rana"].Client, Input("sick", mon3, mon3.AddDays(2), documentId: doc))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["kofi"].Client.GetAsync($"{Leave}/requests/{sick}/document")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["mona"].Client.GetAsync($"{Leave}/requests/{sick}/document")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["mona"].Client.GetAsync($"{Leave}/requests/{sick}")).StatusCode);
    }

    [Fact]
    public async Task Cancel_and_shorten_give_days_back_with_approval_when_the_type_needs_it_and_a_rejected_request_takes_nothing()
    {
        var t = await FreshAsync();
        var karim = t["karim"].Client;
        var (mon, fri) = Week(4);
        var start = await LedgerAsync(t, "karim", "annual", mon.Year);

        // Scenario 7: shorten approved leave by two days, then cancel the rest; the balance comes back in full.
        var id = (await SubmittedAsync(karim, Input("annual", mon, fri))).GetProperty("id").GetGuid();
        await DecideAsync(t["kofi"].Client, id, "approve");
        var cancelledBefore = await world.Database.OutboxCountAsync(nameof(LeaveCancelled), t.Id);
        var shortened = await (await karim.PostAsJsonAsync($"{Leave}/requests/{id}:shorten", new { end = mon.AddDays(2) })).ReadAsync<JsonElement>();
        Assert.Equal((3m, mon.AddDays(2).ToString("yyyy-MM-dd")), (shortened.GetProperty("days").GetDecimal(), shortened.GetProperty("end").GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveShortened, id.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await karim.PostAsJsonAsync($"{Leave}/requests/{id}:shorten", new { end = fri })).StatusCode);
        Assert.Equal("cancelled", (await (await karim.PostAsync($"{Leave}/requests/{id}:cancel", null)).ReadAsync<JsonElement>()).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveCancelled, id.ToString());
        Assert.Equal(cancelledBefore + 2, await world.Database.OutboxCountAsync(nameof(LeaveCancelled), t.Id));
        Assert.Equal(start, await LedgerAsync(t, "karim", "annual", mon.Year));
        Assert.Equal(HttpStatusCode.Conflict, (await karim.PostAsync($"{Leave}/requests/{id}:cancel", null)).StatusCode);

        // A type whose cancellation needs approval waits for the manager before the days come back.
        await TypeAsync(t, "training", Names("Training leave", "إجازة تدريبية"), new { sensitive = false, cancelNeedsApproval = true });
        await PolicyAsync(t, "training", 5, "yearly");
        await AccrueAsync(t);
        var training = (await SubmittedAsync(karim, Input("training", mon, mon))).GetProperty("id").GetGuid();
        await DecideAsync(t["kofi"].Client, training, "approve");
        var asked = await (await karim.PostAsync($"{Leave}/requests/{training}:cancel", null)).ReadAsync<JsonElement>();
        Assert.Equal(("approved", "cancel"), (asked.GetProperty("status").GetString(), asked.GetProperty("changeKind").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await karim.PostAsync($"{Leave}/requests/{training}:cancel", null)).StatusCode);
        var held = await LedgerAsync(t, "karim", "training", mon.Year);
        await DecideAsync(t["kofi"].Client, training, "approve", source: "leave_change");
        Assert.Equal("cancelled", (await GetAsync(karim, training)).GetProperty("status").GetString());
        Assert.Equal(held + 1m, await LedgerAsync(t, "karim", "training", mon.Year));

        // A pending request is withdrawn and leaves the manager's inbox; a rejected one takes nothing.
        var (mon2, _) = Week(6);
        var pending = (await SubmittedAsync(karim, Input("annual", mon2, mon2))).GetProperty("id").GetGuid();
        Assert.Equal("withdrawn", (await (await karim.PostAsync($"{Leave}/requests/{pending}:cancel", null)).ReadAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.DoesNotContain(await InboxAsync(t["kofi"].Client, "leave"), i => i.GetProperty("link").GetString()!.Contains(pending.ToString(), StringComparison.Ordinal));
        var rejected = (await SubmittedAsync(t["mona"].Client, Input("annual", mon2, mon2))).GetProperty("id").GetGuid();
        await DecideAsync(t["kofi"].Client, rejected, "reject");
        Assert.Equal("rejected", (await GetAsync(t["mona"].Client, rejected)).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveRejected, rejected.ToString());
        Assert.Equal(0m, await world.Database.TenantScalarAsync<decimal>(t.Id, $"select coalesce(sum(delta), 0) from hr.leave_ledger where request_id = '{rejected}'"));
    }

    [Fact]
    public async Task Accrual_runs_monthly_once_per_period_and_carry_over_keeps_up_to_the_limit()
    {
        var t = await FreshAsync();
        await TypeAsync(t, "monthly", Names("Monthly leave", "إجازة شهرية"), new { sensitive = false });
        await PolicyAsync(t, "monthly", 12, "monthly");
        await AccrueAsync(t);
        Assert.Equal(0, await AccrueAsync(t));
        var months = await world.Database.TenantScalarAsync<long>(t.Id,
            $"select count(*) from hr.leave_ledger l join hr.leave_type ty on ty.id = l.leave_type_id where ty.key = 'monthly' and l.reason = 'accrual' and l.user_id = '{t["mona"].Id}'");
        var utc = DateTime.UtcNow;
        Assert.Contains(months, new long[] { utc.AddHours(-14).Month, utc.AddHours(14).Month });
        Assert.Equal((decimal)months, await AvailableAsync(t["mona"].Client, "monthly"));

        // Eight days left from last year: five carry over and three lapse, once.
        var lastYear = utc.Year - 1;
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            $"""
            with c as (insert into hr.leave_ledger (tenant_id, id, user_id, leave_type_id, leave_year, delta, reason, at)
              select tenant_id, gen_random_uuid(), '{t["mona"].Id}', id, {lastYear}, 8, 'adjustment', now() from hr.leave_type where key = 'annual' returning 1)
            select count(*) from c
            """));
        Assert.True(await AccrueAsync(t) >= 3);
        Assert.Equal(0, await AccrueAsync(t));
        Assert.Equal(26m, await AvailableAsync(t["mona"].Client, "annual"));
        var entries = (await (await t["mona"].Client.GetAsync($"{Leave}/ledger/{t["mona"].Id}")).ReadAsync<JsonElement>()).EnumerateArray()
            .Where(e => e.GetProperty("typeKey").GetString() == "annual").ToList();
        Assert.Contains(entries, e => e.GetProperty("reason").GetString() == "lapse" && e.GetProperty("delta").GetDecimal() == -3m && e.GetProperty("leaveYear").GetInt32() == lastYear);
        Assert.Contains(entries, e => e.GetProperty("reason").GetString() == "carry_over" && e.GetProperty("delta").GetDecimal() == 5m && e.GetProperty("leaveYear").GetInt32() == lastYear + 1);
    }

    [Fact]
    public async Task Hr_officers_stay_in_their_subtree_other_tenants_see_nothing_and_a_plan_without_leave_refuses()
    {
        var t = await FreshAsync();
        var gulf = t.Unit("Gulf");
        var (mon, fri) = Week(8);

        // Types are tenant configuration: an employee and a scoped HR officer cannot change them.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.PutAsJsonAsync($"{Leave}/types/annual", new { names = Names("x", "x") })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["hr_gulf"].Client.PutAsJsonAsync($"{Leave}/types/annual", new { names = Names("x", "x") })).StatusCode);
        var annual = (await (await t["admin"].Client.GetAsync($"{Leave}/types?all=true")).ReadAsync<JsonElement>()).EnumerateArray().Single(x => x.GetProperty("key").GetString() == "annual");
        await TypeAsync(t, "annual", Names("Annual leave", "إجازة سنوية"), new { sensitive = false, carryOverMax = 10 }, annual.GetProperty("eTag").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveTypeChanged, "annual");
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PutAsJsonAsync($"{Leave}/types/annual", new { names = Names("x", "x") })).StatusCode);

        // A Gulf policy for the Gulf HR officer only; the Egypt officer neither sees nor changes it.
        var policy = await PolicyAsync(t, "annual", 25, "yearly", user: "hr_gulf", unit: gulf);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeavePolicyChanged, policy.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["hr_egypt"].Client.PostAsJsonAsync($"{Leave}/policies", new { typeKey = "sick", orgUnitId = gulf, bands = new[] { new { days = 5 } } })).StatusCode);
        Assert.DoesNotContain((await (await t["hr_egypt"].Client.GetAsync($"{Leave}/policies")).ReadAsync<JsonElement>()).EnumerateArray(), p => p.GetProperty("id").GetGuid() == policy);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.DeleteAsync($"{Leave}/policies/{policy}")).StatusCode);
        await AccrueAsync(t);
        Assert.Equal(21m, await AvailableAsync(t["nadia"].Client, "annual"));

        // HR on behalf of a person in scope; the manager approves. The Egypt officer cannot file, read or adjust for her.
        var filed = await SubmittedAsync(t["hr_gulf"].Client, Input("annual", mon, fri, onBehalfOf: t["mona"].Id));
        var id = filed.GetProperty("id").GetGuid();
        Assert.Equal((t["mona"].Id, t["hr_gulf"].Id), (filed.GetProperty("person").GetProperty("id").GetGuid(), filed.GetProperty("filedBy").GetProperty("id").GetGuid()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveOnBehalf, id.ToString());
        Assert.Contains("validation.leave.person_unknown", await ErrorsAsync(await SubmitAsync(t["hr_egypt"].Client, Input("annual", mon, fri, onBehalfOf: t["mona"].Id))));
        Assert.Contains("validation.leave.person_unknown", await ErrorsAsync(await SubmitAsync(t["karim"].Client, Input("annual", mon, fri, onBehalfOf: t["mona"].Id))));
        Assert.Equal(HttpStatusCode.OK, (await t["hr_gulf"].Client.GetAsync($"{Leave}/requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync($"{Leave}/requests/{id}")).StatusCode);
        Assert.Empty(await InboxAsync(t["hr_gulf"].Client, "leave"));
        await DecideAsync(t["kofi"].Client, id, "approve");

        var adjusted = await t["hr_gulf"].Client.PostAsJsonAsync($"{Leave}/adjustments", new { userId = t["mona"].Id, typeKey = "annual", delta = 2, note = "Overtime in October" });
        Assert.Equal(HttpStatusCode.Created, adjusted.StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrBalanceAdjusted, t["mona"].Id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.PostAsJsonAsync($"{Leave}/adjustments", new { userId = t["mona"].Id, typeKey = "annual", delta = 2, note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["karim"].Client.PostAsJsonAsync($"{Leave}/adjustments", new { userId = t["mona"].Id, typeKey = "annual", delta = 2, note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync($"{Leave}/ledger/{t["mona"].Id}")).StatusCode);

        // The team view: Kofi's team, the Gulf unit for its HR officer, never for the Egypt one.
        var team = await (await t["kofi"].Client.GetAsync($"{Leave}/team?from={mon:yyyy-MM-dd}&to={fri:yyyy-MM-dd}")).ReadAsync<JsonElement>();
        Assert.Contains(team.GetProperty("people").EnumerateArray(), p => p.GetProperty("userId").GetGuid() == t["mona"].Id);
        Assert.Equal(5, team.GetProperty("absences").EnumerateArray().Count(a => a.GetProperty("userId").GetGuid() == t["mona"].Id && a.GetProperty("typeKey").GetString() == "annual"));
        Assert.Equal(HttpStatusCode.OK, (await t["hr_gulf"].Client.GetAsync($"{Leave}/team?from={mon:yyyy-MM-dd}&to={fri:yyyy-MM-dd}&orgUnitId={gulf}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync($"{Leave}/team?from={mon:yyyy-MM-dd}&to={fri:yyyy-MM-dd}&orgUnitId={gulf}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["kofi"].Client.GetAsync($"{Leave}/team?from={mon:yyyy-MM-dd}&to={mon.AddDays(90):yyyy-MM-dd}")).StatusCode);

        // The Leave service is in the catalogue.
        var catalogue = await (await t["mona"].Client.GetAsync("/api/v1/services")).ReadAsync<JsonElement>();
        Assert.Contains(catalogue.GetProperty("services").EnumerateArray(), s => s.GetProperty("launchRoute").GetString() == "/leave");

        // Another tenant finds neither the request nor the person.
        var other = await world.CreateTenantAsync("lv-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Leave}/requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Leave}/ledger/{t["mona"].Id}")).StatusCode);
        Assert.Empty((await (await other["admin"].Client.GetAsync($"{Leave}/types?all=true")).ReadAsync<JsonElement>()).EnumerateArray());
        Assert.Contains("validation.leave.person_unknown", await ErrorsAsync(await SubmitAsync(other["admin"].Client, Input("annual", mon, fri, onBehalfOf: t["mona"].Id))));

        // A plan without hr.leave refuses every leave endpoint. New tenants take the latest published version, so the
        // package's capabilities are published again straight after.
        var plain = await world.CreateTenantAsync("lv-p-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        var current = (await (await plain["admin"].Client.GetAsync("/api/v1/tenant/entitlements")).ReadAsync<JsonElement>()).GetProperty("capabilities");
        var capabilities = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.GetRawText())!.ToDictionary(p => p.Key, p => (object)p.Value);
        capabilities[Capabilities.HrLeave] = new { enabled = false };
        var created = await (await world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace/versions", new { capabilities })).ReadAsync<JsonElement>();
        var version = created.GetProperty("version").GetInt32();
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace/versions/{version}:publish", null)).EnsureSuccessStatusCode();
        (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{plain.Id}/subscription", new { packageKey = "workplace", version })).EnsureSuccessStatusCode();
        var restore = await (await world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace/versions", new { capabilities = current })).ReadAsync<JsonElement>();
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace/versions/{restore.GetProperty("version").GetInt32()}:publish", null)).EnsureSuccessStatusCode();
        foreach (var call in new Func<Task<HttpResponseMessage>>[]
        {
            () => plain["admin"].Client.GetAsync("/api/v1/me/leave"),
            () => plain["admin"].Client.GetAsync($"{Leave}/types"),
            () => plain["admin"].Client.PostAsJsonAsync($"{Leave}/preview", Input("annual", mon, fri)),
            () => SubmitAsync(plain["admin"].Client, Input("annual", mon, fri)),
        })
        {
            var response = await call();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("entitlement.not_in_package", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Approved_leave_shows_as_out_of_office_on_the_profile_with_dates_only()
    {
        var t = await FreshAsync();
        // Every day a working day, so the leave can start today whatever day it is.
        var calendar = (await (await t["admin"].Client.GetAsync("/api/v1/hr/calendars")).ReadAsync<JsonElement>())[0];
        using (var week = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/hr/calendars/{calendar.GetProperty("id").GetGuid()}")
        {
            Content = JsonContent.Create(new { name = new { }, weekDays = new[] { 1, 2, 3, 4, 5, 6, 7 } }),
        })
        {
            week.Headers.TryAddWithoutValidation("If-Match", calendar.GetProperty("eTag").GetString());
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.SendAsync(week)).StatusCode);
        }
        var today = await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<LeaveLedger>().TodayAsync(default));
        var profile = $"/api/v1/people/{t["mona"].Id}";
        Assert.Equal(JsonValueKind.Null, (await (await t["karim"].Client.GetAsync(profile)).ReadAsync<JsonElement>()).GetProperty("away").ValueKind);

        var id = (await SubmittedAsync(t["mona"].Client, Input("annual", today, today.AddDays(2)))).GetProperty("id").GetGuid();
        // Waiting leave is not out of office yet.
        Assert.Equal(JsonValueKind.Null, (await (await t["karim"].Client.GetAsync(profile)).ReadAsync<JsonElement>()).GetProperty("away").ValueKind);
        await DecideAsync(t["kofi"].Client, id, "approve");

        var seen = await (await t["karim"].Client.GetAsync(profile)).ReadAsync<JsonElement>();
        var away = seen.GetProperty("away");
        Assert.Equal((today, today.AddDays(2)), (DateOnly.Parse(away.GetProperty("from").GetString()!), DateOnly.Parse(away.GetProperty("to").GetString()!)));
        Assert.Equal(2, away.EnumerateObject().Count());
        Assert.DoesNotContain("annual", seen.GetRawText(), StringComparison.Ordinal);

        // Another tenant does not find the person at all.
        var other = await world.CreateTenantAsync("lv-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(profile)).StatusCode);
    }
}
