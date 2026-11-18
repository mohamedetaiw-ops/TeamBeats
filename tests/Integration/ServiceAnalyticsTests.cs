using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T012 (US4, FR-008): service analytics give requests per week, cycle time, time per step, rejections, the
/// automation rate and structured reasons by unit with the minimum group size; only reportable single choice fields are
/// reasons; Services decides who may see a service, the package needs analytics.advanced, and tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceAnalyticsTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";
    private const string Analytics = "/api/v1/analytics/services";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Form(bool reportableText = false) => new
    {
        fields = new object[]
        {
            new
            {
                key = "reason", type = "choice", labels = Names("Reason", "السبب"), required = true, reportable = true,
                options = new[] { new { value = "pay", labels = Names("Pay", "الأجر") }, new { value = "growth", labels = Names("Growth", "النمو") },
                    new { value = "family", labels = Names("Family", "العائلة") } },
            },
            new { key = "note", type = "text", labels = Names("Note", "ملاحظة"), reportable = reportableText },
        },
    };

    private async Task<(TestTenant T, Guid Service)> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("san-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        foreach (var unit in new[] { "Dubai", "Cairo", "Abu" })
        {
            await world.CreateUnitAsync(t, unit, holding, "department");
        }
        foreach (var (prefix, unit, count) in new[] { ("d", "Dubai", 6), ("c", "Cairo", 5), ("a", "Abu", 2) })
        {
            for (var i = 1; i <= count; i++)
            {
                await world.SeedUserAsync(t, prefix + i, MembershipTypes.Employee);
                await world.AddMembershipAsync(t, prefix + i, unit);
            }
        }
        await world.SeedUserAsync(t, "omar", MembershipTypes.Employee);
        await world.AssignAsync(t, "omar", BuiltInRoles.Approver, "tenant", null);
        await world.SeedUserAsync(t, "viewer", MembershipTypes.Employee);
        var admin = t["admin"].Client;
        var created = await OkAsync(admin.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Resignation", "الاستقالة") }), HttpStatusCode.Created);
        var id = created.GetProperty("service").GetProperty("id").GetGuid();
        var route = new { approval = new { names = Names("Manager approval", "موافقة المدير"), approvers = new { kind = "users", users = new[] { t["omar"].Id } } } };
        await OkAsync(admin.PutAsJsonAsync($"{Admin}/{id}/draft", new { form = Form(), route }));
        await OkAsync(admin.PostAsJsonAsync($"{Admin}/{id}/publish", new { }));
        return (t, id);
    }

    private static async Task<Guid> SubmitAsync(TestTenant t, string user, Guid service, string reason) =>
        (await OkAsync(t[user].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = service, data = new { reason, note = "Personal words stay in the request." } }),
            HttpStatusCode.Created)).GetProperty("request").GetProperty("id").GetGuid();

    private async Task DecideAsync(TestTenant t, Guid request, string outcome)
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 10 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
        var tasks = await OkAsync(t["omar"].Client.GetAsync("/api/v1/tasks"));
        var task = Assert.Single(tasks.EnumerateArray(), i => i.GetProperty("requestId").GetGuid() == request).GetProperty("taskId").GetGuid();
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome, comment = outcome == "reject" ? "Personal words of the approver." : null }));
    }

    [Fact]
    public async Task Requests_cycle_time_steps_rejections_and_structured_reasons_respect_the_minimum_group_size()
    {
        var (t, service) = await FreshAsync();
        var answers = new (string User, string Reason, string Outcome)[]
        {
            ("d1", "pay", "approve"), ("d2", "pay", "approve"), ("d3", "pay", "approve"), ("d4", "pay", "approve"), ("d5", "growth", "approve"), ("d6", "growth", "approve"),
            ("c1", "pay", "reject"), ("c2", "growth", "approve"), ("c3", "growth", "approve"), ("c4", "family", "approve"), ("c5", "family", "approve"),
            ("a1", "family", "reject"), ("a2", "family", "reject"),
        };
        foreach (var (user, reason, outcome) in answers)
        {
            await DecideAsync(t, await SubmitAsync(t, user, service, reason), outcome);
        }

        var list = await OkAsync(t["admin"].Client.GetAsync(Analytics));
        Assert.Contains(list.EnumerateArray(), s => s.GetProperty("id").GetGuid() == service && s.GetProperty("processKey").GetString() == $"service.{service:N}");

        var view = await OkAsync(t["admin"].Client.GetAsync($"{Analytics}/{service}"));
        Assert.Equal(13, view.GetProperty("requests").GetInt32());
        Assert.Equal(13, Assert.Single(view.GetProperty("weeks").EnumerateArray()).GetProperty("requests").GetInt32());
        var cycle = view.GetProperty("cycle");
        Assert.Equal(13, cycle.GetProperty("closed").GetInt32());
        Assert.True(cycle.GetProperty("p90Seconds").GetDouble() >= cycle.GetProperty("medianSeconds").GetDouble());
        Assert.Equal(3, view.GetProperty("rejected").GetInt32());
        Assert.Equal(0.2308, view.GetProperty("rejectedShare").GetDouble());
        Assert.Equal(0, view.GetProperty("breached").GetInt32());
        Assert.Equal(0, view.GetProperty("automationRate").GetDouble());
        var step = Assert.Single(view.GetProperty("steps").EnumerateArray());
        Assert.Equal("Manager approval", step.GetProperty("step").GetString());
        Assert.Equal(13, step.GetProperty("decisions").GetInt32());
        Assert.Equal(3, step.GetProperty("rejected").GetInt32());
        Assert.False(step.GetProperty("slow").GetBoolean());

        // Only the reportable choice field is a reason; the free text note never leaves Services.
        var reasons = Assert.Single(view.GetProperty("reasons").EnumerateArray());
        Assert.Equal("reason", reasons.GetProperty("field").GetString());
        Assert.DoesNotContain("Personal words", view.GetRawText(), StringComparison.Ordinal);
        var all = reasons.GetProperty("all").GetProperty("counts");
        Assert.Equal(5, all.GetProperty("pay").GetInt32());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("growth").ValueKind); // Four people: hidden.
        Assert.Equal(JsonValueKind.Null, all.GetProperty("family").ValueKind);
        // Abu (two) folds, and Cairo (five) with it so "other" reaches the minimum; Dubai is shown with its small counts hidden.
        var rows = reasons.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("group").GetString()!, r => r);
        Assert.Equal(2, rows.Count);
        Assert.Equal(7, rows[ItemAnalytics.Other].GetProperty("total").GetInt32());
        var dubai = rows.Single(r => r.Key != ItemAnalytics.Other).Value;
        Assert.Equal("Dubai", dubai.GetProperty("names").GetProperty("en").GetString());
        Assert.Equal(6, dubai.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, dubai.GetProperty("counts").GetProperty("growth").ValueKind);
        Assert.Equal(JsonValueKind.Null, dubai.GetProperty("counts").GetProperty("pay").ValueKind); // Hidden too, or growth would be six minus pay.

        // A period without requests, and a bad period.
        var empty = await OkAsync(t["admin"].Client.GetAsync($"{Analytics}/{service}?from=2025-01-01&to=2025-02-01"));
        Assert.Equal(0, empty.GetProperty("requests").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("cycle").GetProperty("medianSeconds").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.GetAsync($"{Analytics}/{service}?from=2026-03-01&to=2026-01-01")).StatusCode);
    }

    [Fact]
    public async Task A_small_service_hides_its_figures_and_only_a_choice_field_can_be_a_reason()
    {
        var (t, service) = await FreshAsync();
        foreach (var user in new[] { "d1", "d2" })
        {
            await DecideAsync(t, await SubmitAsync(t, user, service, "pay"), "approve");
        }
        var view = await OkAsync(t["admin"].Client.GetAsync($"{Analytics}/{service}"));
        Assert.Equal(2, view.GetProperty("requests").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("cycle").GetProperty("medianSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("rejectedShare").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("automationRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, Assert.Single(view.GetProperty("steps").EnumerateArray()).GetProperty("medianSeconds").ValueKind);
        var reasons = Assert.Single(view.GetProperty("reasons").EnumerateArray());
        Assert.Empty(reasons.GetProperty("rows").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, reasons.GetProperty("all").GetProperty("counts").GetProperty("pay").ValueKind);

        var draft = await t["admin"].Client.PutAsJsonAsync($"{Admin}/{service}/draft", new { form = Form(reportableText: true) });
        Assert.Equal(HttpStatusCode.BadRequest, draft.StatusCode);
        Assert.Contains("validation.services.field_reportable", await draft.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_decides_who_sees_a_service_the_package_decides_access_and_tenants_stay_apart()
    {
        var (t, service) = await FreshAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await t["viewer"].Client.GetAsync($"{Analytics}/{service}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["viewer"].Client.GetAsync(Analytics)).StatusCode);

        var other = await world.CreateTenantAsync("san-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Analytics}/{service}")).StatusCode);
        Assert.DoesNotContain((await OkAsync(other["admin"].Client.GetAsync(Analytics))).EnumerateArray(), s => s.GetProperty("id").GetGuid() == service);

        var basic = await world.CreateTenantAsync("san-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync(Analytics)).StatusCode);
    }
}
