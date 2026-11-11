using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Integrations.Finance;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T023 (US8 scenarios 1 and 6, FR-023, FR-025) with specs 017 and 019: the limit follows the grade HR holds
/// for the person, and an approved claim goes to the finance system through the finance pack with the employee number
/// from HR. The finance reference and the payment status are shown on the claim until it is paid.
/// </summary>
public sealed partial class HrSyncTests
{
    private const string Expenses = "/api/v1/me/expenses";

    private sealed class FakeErp
    {
        public ConcurrentDictionary<string, string> Status { get; } = new();
        public ConcurrentQueue<JsonElement> Received { get; } = new();

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            if (s.Method == "POST")
            {
                Received.Enqueue(JsonDocument.Parse(s.Body!).RootElement.Clone());
                var reference = $"EXP-{Received.Count:D5}";
                Status[reference] = "OPEN";
                return FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { data = new { id = reference, state = "OPEN" } }));
            }
            var id = s.Uri.AbsolutePath.Split('/')[^1];
            return Status.TryGetValue(id, out var st)
                ? FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new { id, state = st } }))
                : FakeRestApi.Json(HttpStatusCode.NotFound, "{}");
        }
    }

    private async Task<FakeErp> FinanceConnectionAsync(TestTenant t)
    {
        var host = $"erp-{Guid.NewGuid():N}"[..20] + ".rest.test";
        var erp = new FakeErp();
        world.Factory.Rest.On(host, erp.Respond);
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "erp-token"), "fictional-erp-token");
        var created = await admin.PostAsJsonAsync("/api/v1/integrations/pack-connections", new
        {
            kind = FinancePack.KindName,
            displayName = "Finance",
            settings = new
            {
                baseUrl = $"https://{host}/api",
                auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "erp-token") },
                submitPath = "/expenses",
                statusPath = "/expenses/{reference}",
                fields = new Dictionary<string, string>
                {
                    ["source_id"] = "externalId", ["employee_number"] = "employee.code", ["amount"] = "total.value", ["currency"] = "total.currency", ["claim_date"] = "date",
                },
                linesField = "items",
                lineFields = new Dictionary<string, string> { ["category"] = "glCategory", ["amount"] = "value" },
                referencePath = "$.data.id",
                statusValuePath = "$.data.state",
                statusValues = new Dictionary<string, string[]> { ["submitted"] = ["OPEN"], ["approved"] = ["APPROVED"], ["paid"] = ["PAID"], ["rejected"] = ["REJECTED"] },
                idempotencyHeader = "Idempotency-Key",
            },
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal("ok", (await (await admin.PostAsync($"/api/v1/integrations/pack-connections/{id}:test", null)).ReadAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/integrations/pack-connections/{id}:enable", null)).StatusCode);
        return erp;
    }

    private async Task RunFinanceAsync(TestTenant t, DateTimeOffset at)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var ran = await sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(at, 50);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return ran;
        });
        for (var i = 0; i < 5; i++)
        {
            await DispatchAsync();
        }
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var checkedCount = await sp.GetRequiredService<FinanceHandOffs>().CheckDueAsync(at, 50, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return checkedCount;
        });
        for (var i = 0; i < 5; i++)
        {
            await DispatchAsync();
        }
    }

    private static async Task<HttpResponseMessage> DecideExpenseAsync(HttpClient client, Guid id)
    {
        var task = (await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray()
            .Single(i => i.GetProperty("source").GetString() == "expense" && i.GetProperty("link").GetString()!.Contains(id.ToString(), StringComparison.Ordinal));
        return await client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve", comment = (string?)null });
    }

    [Fact]
    public async Task The_limit_follows_the_HR_grade_and_an_approved_claim_is_paid_through_finance()
    {
        var t = await TenantAsync(people: ["sara", "fin"]);
        var finance = await world.CreateUnitAsync(t, "Finance", null, "organization");
        foreach (var name in new[] { "sara", "fin" })
        {
            await world.AddMembershipAsync(t, name, "Finance");
        }
        await world.AssignAsync(t, "fin", BuiltInRoles.FinanceOfficer, "org_subtree", finance);
        var (_, hr) = await RestConnectionAsync(t);
        hr.Employees = [Person("R1", t["sara"].Email, "E-100", grade: "G3")];
        await SyncAsync(t);
        var erp = await FinanceConnectionAsync(t);

        var admin = t["admin"].Client;
        var current = await admin.GetAsync("/api/v1/hr/expense-policy");
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/hr/expense-policy")
        {
            Content = JsonContent.Create(new
            {
                currency = "SAR", windowDays = 60,
                categories = new object[]
                {
                    new { key = "meals", names = new { en = "Meals", ar = "الوجبات" }, kind = "amount", receipt = false,
                        limits = new[] { new { grade = (string?)"g3", amount = 100m }, new { grade = (string?)null, amount = 400m } } },
                },
                tiers = Array.Empty<object>(), costCentres = Array.Empty<string>(),
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        Assert.True((await admin.SendAsync(put)).IsSuccessStatusCode);

        var sara = t["sara"].Client;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var over = await sara.PostAsJsonAsync(Expenses, new { title = "Lunch", lines = new[] { new { category = "meals", date = today, amount = 150m, currency = "SAR" } } });
        Assert.Contains("validation.expenses.justification_required", await over.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var created = await sara.PostAsJsonAsync(Expenses, new { title = "Lunch", lines = new[] { new { category = "meals", date = today, amount = 90m, currency = "SAR" } } });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Sara has no line manager, so finance takes the first step as well.
        Assert.True((await DecideExpenseAsync(t["fin"].Client, id)).IsSuccessStatusCode);
        Assert.True((await DecideExpenseAsync(t["fin"].Client, id)).IsSuccessStatusCode);
        var queued = await (await sara.GetAsync($"/api/v1/hr/expenses/{id}")).ReadAsync<JsonElement>();
        Assert.Equal(("approved", "queued"), (queued.GetProperty("status").GetString(), queued.GetProperty("financeState").GetString()));

        await RunFinanceAsync(t, DateTimeOffset.UtcNow.AddSeconds(1));
        var sent = Assert.Single(erp.Received);
        Assert.Equal(id.ToString(), sent.GetProperty("externalId").GetString());
        Assert.Equal("E-100", sent.GetProperty("employee").GetProperty("code").GetString());
        Assert.Equal(90m, sent.GetProperty("total").GetProperty("value").GetDecimal());
        var handedOff = await (await sara.GetAsync($"/api/v1/hr/expenses/{id}")).ReadAsync<JsonElement>();
        Assert.Equal(("handed_off", "EXP-00001", "submitted"),
            (handedOff.GetProperty("status").GetString(), handedOff.GetProperty("externalReference").GetString(), handedOff.GetProperty("paymentStatus").GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrExpenseHandedOff, id.ToString());

        erp.Status["EXP-00001"] = "PAID";
        await RunFinanceAsync(t, DateTimeOffset.UtcNow.AddHours(7));
        var paid = await (await sara.GetAsync($"/api/v1/hr/expenses/{id}")).ReadAsync<JsonElement>();
        Assert.Equal(("paid", "paid"), (paid.GetProperty("status").GetString(), paid.GetProperty("paymentStatus").GetString()));
        Assert.Equal(1L, await world.Database.ScalarInTenantAsync<long>(t.Id,
            "select count(*) from notifications.notification where tenant_id = @t and user_id = @u and template_key = 'hr.expense_paid'", ("t", t.Id), ("u", t["sara"].Id)));
    }
}
