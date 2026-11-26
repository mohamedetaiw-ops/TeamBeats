using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.DevFakes.FakeMicrosoftGraph;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 018 R3 (FR-015): answering Outlook invitations from TeamBeats as the employee, with its own connection feature and
/// <c>Calendars.ReadWrite</c>. Only invitations can be answered, the answer is audited without the event, and the calendar
/// shows it at once. All content is fictional.
/// </summary>
public sealed partial class MailCalendarTests
{
    private async Task<Setup> InvitationsAsync(bool answers = true, params string[] users)
    {
        var s = await SetupAsync(answers ? ["calendar", "calendar_responses"] : ["calendar"], "workplace_plus", users);
        Graph.AddDelegatedConsent(s.Directory, "Calendars.ReadWrite");
        var tomorrow = TodayDubai().AddDays(1);
        Graph.MailboxFor(s.Tenant["employee"].Email).Events.AddRange(
        [
            new FakeEvent("invite", "Budget review", tomorrow.AddHours(10), tomorrow.AddHours(11), Response: "notResponded"),
            new FakeEvent("maybe", "Design sync", tomorrow.AddHours(12), tomorrow.AddHours(13), ShowAs: "tentative", Response: "tentativelyAccepted"),
            new FakeEvent("mine", "Team planning", tomorrow.AddHours(14), tomorrow.AddHours(15), Response: "organizer"),
        ]);
        await ConnectAsync(s.Tenant);
        return s;
    }

    private static async Task<HttpResponseMessage> AnswerAsync(TestTenant t, string eventId, object body, string user = "employee") =>
        await t[user].Client.PostAsJsonAsync($"/api/v1/me/calendar/events/{Uri.EscapeDataString(eventId)}/response", body);

    private static async Task<string> AnswerStateAsync(TestTenant t, string eventId, string response, string user = "employee") =>
        State(await (await AnswerAsync(t, eventId, new { response }, user)).ReadAsync<JsonElement>());

    private static Dictionary<string, JsonElement> Items(JsonElement calendar) =>
        calendar.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!);

    [Fact]
    public async Task An_employee_answers_an_invitation_and_Outlook_tells_the_organizer_only_when_asked()
    {
        var s = await InvitationsAsync();
        var items = Items(await GetAsync(s.Tenant, "/api/v1/me/calendar"));
        Assert.True(items["invite"].GetProperty("canRespond").GetBoolean());
        Assert.Equal("not_responded", items["invite"].GetProperty("response").GetString());
        Assert.True(items["maybe"].GetProperty("canRespond").GetBoolean());
        // The employee's own meeting is never answered.
        Assert.False(items["mine"].GetProperty("canRespond").GetBoolean());

        var sent = await AnswerAsync(s.Tenant, "invite", new { response = "accept", comment = "  I will bring the figures. ", notifyOrganizer = false });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("ok", State(await sent.ReadAsync<JsonElement>()));
        var answer = Assert.Single(Graph.InvitationAnswers, a => a.Upn == s.Tenant["employee"].Email);
        Assert.Equal(("invite", "accept", "I will bring the figures.", false), (answer.EventId, answer.Action, answer.Comment, answer.SendResponse));

        // The calendar shows the answer at once, without a refresh.
        Assert.Equal("accepted", Items(await GetAsync(s.Tenant, "/api/v1/me/calendar"))["invite"].GetProperty("response").GetString());

        // A decline hides the meeting, and the organizer is told by default.
        Assert.Equal("ok", await AnswerStateAsync(s.Tenant, "maybe", "decline"));
        Assert.True(Graph.InvitationAnswers.Single(a => a.Upn == s.Tenant["employee"].Email && a.EventId == "maybe").SendResponse);
        Assert.DoesNotContain("maybe", Items(await GetAsync(s.Tenant, "/api/v1/me/calendar")).Keys);

        // The audit has the answer and never the event, its subject or the note.
        var audits = await world.Database.TenantScalarAsync<string>(s.Tenant.Id,
            "select string_agg(changes::text, '|') from audit.audit_event where action = @a", ("a", AuditActions.CalendarInvitationAnswered));
        Assert.Contains("accept", audits, StringComparison.Ordinal);
        Assert.Contains("decline", audits, StringComparison.Ordinal);
        Assert.DoesNotContain("invite", audits, StringComparison.Ordinal);
        Assert.DoesNotContain("Budget", audits, StringComparison.Ordinal);
        Assert.DoesNotContain("figures", audits, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_Outlook_cannot_take_is_a_state_and_bad_input_is_refused()
    {
        var s = await InvitationsAsync();
        Assert.Equal("refused", await AnswerStateAsync(s.Tenant, "mine", "accept"));
        Assert.Equal("not_found", await AnswerStateAsync(s.Tenant, "no-such-event", "accept"));

        Assert.Equal(HttpStatusCode.BadRequest, (await AnswerAsync(s.Tenant, "invite", new { response = "maybe" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AnswerAsync(s.Tenant, "invite", new { response = "accept", comment = new string('x', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AnswerAsync(s.Tenant, "invite", new { response = "accept", comment = "a\u0007b" })).StatusCode);
        Assert.DoesNotContain(Graph.InvitationAnswers, a => a.Upn == s.Tenant["employee"].Email);
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(s.Tenant.Id,
            "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.CalendarInvitationAnswered)));
    }

    [Fact]
    public async Task Without_the_answers_feature_nothing_is_offered_and_nothing_is_sent()
    {
        var s = await InvitationsAsync(answers: false);
        Assert.All(Items(await GetAsync(s.Tenant, "/api/v1/me/calendar")).Values, i => Assert.False(i.GetProperty("canRespond").GetBoolean()));
        Assert.Equal("turned_off", await AnswerStateAsync(s.Tenant, "invite", "accept"));
        Assert.DoesNotContain(Graph.InvitationAnswers, a => a.Upn == s.Tenant["employee"].Email);

        var day = DateOnly.FromDateTime(TodayDubai().AddDays(1).DateTime);
        var unified = await GetAsync(s.Tenant, $"/api/v1/calendar/events?from={day:yyyy-MM-dd}&to={day.AddDays(1):yyyy-MM-dd}&layers=meeting");
        Assert.All(unified.GetProperty("items").EnumerateArray(), i => Assert.DoesNotContain("answer", i.GetProperty("actions").EnumerateArray().Select(a => a.GetString())));
    }

    [Fact]
    public async Task The_unified_calendar_offers_the_answer_on_invitations_only()
    {
        var s = await InvitationsAsync();
        var day = DateOnly.FromDateTime(TodayDubai().AddDays(1).DateTime);
        var unified = await GetAsync(s.Tenant, $"/api/v1/calendar/events?from={day:yyyy-MM-dd}&to={day.AddDays(1):yyyy-MM-dd}&layers=meeting");
        var items = unified.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("sourceId").GetString()!);
        Assert.Contains("answer", items["invite"].GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("not_responded", items["invite"].GetProperty("invitation").GetString());
        Assert.Equal("tentative", items["maybe"].GetProperty("invitation").GetString());
        Assert.DoesNotContain("answer", items["mine"].GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(JsonValueKind.Null, items["mine"].GetProperty("invitation").ValueKind);
    }

    [Fact]
    public async Task An_answer_reaches_only_the_signed_in_employees_own_calendar()
    {
        var s = await InvitationsAsync(true, "colleague");
        await ConnectAsync(s.Tenant, "colleague");
        var other = await InvitationsAsync();
        // The event ID of another person, in this tenant or another, is not in the employee's calendar.
        Assert.Equal("not_found", await AnswerStateAsync(s.Tenant, "invite", "accept", "colleague"));
        Assert.Equal("ok", await AnswerStateAsync(other.Tenant, "invite", "decline"));
        Assert.Equal("not_responded", Items(await GetAsync(s.Tenant, "/api/v1/me/calendar"))["invite"].GetProperty("response").GetString());
        Assert.DoesNotContain(Graph.InvitationAnswers, a => a.Upn == s.Tenant["employee"].Email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(s.Tenant.Key).PostAsJsonAsync("/api/v1/me/calendar/events/invite/response", new { response = "accept" })).StatusCode);
    }
}
