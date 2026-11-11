using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T023 (US8 scenarios 1, 3, 4 and 5, FR-023, FR-025): finance sets the expense policy and rates; a claim's
/// lines are checked against the window and the limit for the person's grade, converted at the rate on the receipt
/// date, and flagged when they look like a duplicate. The claim goes to the line manager, an approver per amount tier,
/// then finance covering the person; nobody decides their own claim, and a sibling unit's finance officer and other
/// tenants see nothing. Without a finance connection an approved claim is paid outside TeamBeats.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ExpenseTests(TenantWorld world)
{
    private const string Mine = "/api/v1/me/expenses";
    private const string PolicyUrl = "/api/v1/hr/expense-policy";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ex-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "sara", "omar", "head", "fin_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.SeedUserAsync(t, "fin_egypt", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "fin_egypt", "Egypt");
        await world.AssignAsync(t, "fin_gulf", BuiltInRoles.FinanceOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "fin_egypt", BuiltInRoles.FinanceOfficer, "org_subtree", egypt);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["sara"].Id}/manager", new { managerId = t["omar"].Id })).StatusCode);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Policy(TestTenant t) => new
    {
        currency = "AED",
        windowDays = 60,
        categories = new object[]
        {
            new { key = "meals", names = new { en = "Meals", ar = "الوجبات" }, kind = "amount", receipt = true, limits = new[] { new { grade = (string?)null, amount = 400m } } },
            new { key = "taxi", names = new { en = "Taxi", ar = "سيارة أجرة" }, kind = "amount", receipt = false },
            new { key = "per_diem", names = new { en = "Per diem", ar = "بدل يومي" }, kind = "per_diem", receipt = false, rate = 150m },
            new { key = "mileage", names = new { en = "Mileage", ar = "المسافة" }, kind = "mileage", receipt = false, rate = 0.5m },
        },
        tiers = new[] { new { above = 1000m, approvers = new[] { t["head"].Id } } },
        costCentres = new[] { "CC-100", "CC-200" },
    };

    private static async Task SetPolicyAsync(TestTenant t)
    {
        var admin = t["admin"].Client;
        var current = await admin.GetAsync(PolicyUrl);
        var put = new HttpRequestMessage(HttpMethod.Put, PolicyUrl) { Content = JsonContent.Create(Policy(t)) };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        await OkAsync(admin.SendAsync(put));
        await OkAsync(admin.PutAsJsonAsync("/api/v1/hr/expense-rates", new { rates = new[] { new { currency = "USD", date = Today.AddDays(-30), rate = 3.6725m } } }));
    }

    private static async Task<Guid> ReceiptAsync(HttpClient client, string text)
    {
        var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 " + text)) { Headers = { ContentType = new("application/pdf") } };
        return (await OkAsync(client.PostAsync($"{Mine}/receipts?name=receipt.pdf", content), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid id, string outcome = "approve")
    {
        var task = (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray()
            .Single(i => i.GetProperty("source").GetString() == "expense" && i.GetProperty("link").GetString()!.Contains(id.ToString(), StringComparison.Ordinal));
        return await client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome, comment = outcome == "approve" ? null : "No receipt" });
    }

    private static bool HasTask(JsonElement tasks, Guid id) =>
        tasks.EnumerateArray().Any(i => i.GetProperty("source").GetString() == "expense" && i.GetProperty("link").GetString()!.Contains(id.ToString(), StringComparison.Ordinal));

    [Fact]
    public async Task Lines_are_checked_converted_and_the_claim_goes_to_the_manager_then_finance()
    {
        var t = await FreshAsync();
        var sara = t["sara"].Client;

        // Before finance sets a currency nothing can be sent; the policy is finance's alone.
        Assert.Contains("conflict.expenses.not_configured",
            await (await sara.PostAsJsonAsync(Mine, new { title = "Trip", lines = new[] { new { category = "travel", date = Today, amount = 10m, currency = "AED" } } })).Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await sara.GetAsync(PolicyUrl)).StatusCode);
        var gulfPolicy = await t["fin_gulf"].Client.GetAsync(PolicyUrl);
        Assert.False((await OkAsync(Task.FromResult(gulfPolicy))).GetProperty("canChange").GetBoolean());
        await SetPolicyAsync(t);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrExpensePolicyChanged, "expenses");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrExpenseRatesChanged, "expenses");

        var form = await OkAsync(sara.GetAsync($"{Mine}/form"));
        Assert.Equal("AED", form.GetProperty("currency").GetString());
        Assert.Equal(400m, form.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "meals").GetProperty("limit").GetDecimal());

        var bad = await sara.PostAsJsonAsync(Mine, new
        {
            title = "Client visit",
            costCentre = "CC-999",
            lines = new object[]
            {
                new { category = "meals", date = Today, amount = 120m, currency = "AED" },
                new { category = "taxi", date = Today.AddDays(-61), amount = 40m, currency = "AED" },
                new { category = "taxi", date = Today.AddDays(1), amount = 40m, currency = "AED" },
                new { category = "taxi", date = Today, amount = 40m, currency = "EUR" },
                new { category = "per_diem", date = Today, days = 40 },
                new { category = "meals", date = Today, amount = 500m, currency = "AED", receiptId = await ReceiptAsync(sara, "big dinner") },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var text = await bad.Content.ReadAsStringAsync();
        foreach (var expected in new[]
        {
            "costCentre", "lines[0].receiptId", "validation.expenses.too_old", "lines[2].date", "validation.expenses.no_rate", "lines[4].days",
            "validation.expenses.justification_required",
        })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        var dinner = await ReceiptAsync(sara, "dinner with a client");
        var claim = await OkAsync(sara.PostAsJsonAsync(Mine, new
        {
            title = "Client visit",
            costCentre = "CC-100",
            lines = new object[]
            {
                new { category = "meals", date = Today.AddDays(-2), amount = 120.50m, currency = "USD", merchant = "Fictional Grill", receiptId = dinner, justification = "Client dinner for four" },
                new { category = "per_diem", date = Today.AddDays(-2), days = 2 },
                new { category = "mileage", date = Today.AddDays(-2), km = 30.5m },
            },
        }), HttpStatusCode.Created);
        var id = claim.GetProperty("id").GetGuid();
        Assert.Equal("EX-000001", claim.GetProperty("reference").GetString());
        var lines = claim.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal((120.50m, "USD", 442.54m), (lines[0].GetProperty("amount").GetDecimal(), lines[0].GetProperty("currency").GetString(), lines[0].GetProperty("baseAmount").GetDecimal()));
        Assert.True(lines[0].GetProperty("overLimit").GetBoolean());
        Assert.Equal(300m, lines[1].GetProperty("baseAmount").GetDecimal());
        Assert.Equal(15.25m, lines[2].GetProperty("baseAmount").GetDecimal());
        Assert.Equal(757.79m, claim.GetProperty("total").GetDecimal());
        Assert.False(claim.GetProperty("duplicate").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrExpenseSubmitted, id.ToString());

        // Nobody else sees it, and Sara never decides her own claim.
        var url = $"/api/v1/hr/expenses/{id}";
        Assert.Equal(HttpStatusCode.NotFound, (await t["fin_egypt"].Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["head"].Client.GetAsync(url)).StatusCode);
        var other = await world.CreateTenantAsync("ex-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(url)).StatusCode);
        Assert.False(HasTask(await OkAsync(sara.GetAsync("/api/v1/tasks")), id));
        Assert.Empty((await OkAsync(t["fin_egypt"].Client.GetAsync("/api/v1/hr/expenses"))).EnumerateArray());
        Assert.Single((await OkAsync(t["fin_gulf"].Client.GetAsync("/api/v1/hr/expenses"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await t["fin_gulf"].Client.GetAsync($"{url}/receipts/{dinner}")).StatusCode);

        // The manager first, then finance covering Sara's unit; the total is under the tier, so no tier step.
        Assert.False(HasTask(await OkAsync(t["fin_gulf"].Client.GetAsync("/api/v1/tasks")), id));
        Assert.True((await DecideAsync(t["omar"].Client, id)).IsSuccessStatusCode);
        Assert.False(HasTask(await OkAsync(t["head"].Client.GetAsync("/api/v1/tasks")), id));
        Assert.False(HasTask(await OkAsync(t["fin_egypt"].Client.GetAsync("/api/v1/tasks")), id));
        Assert.True((await DecideAsync(t["fin_gulf"].Client, id)).IsSuccessStatusCode);
        var approved = await OkAsync(sara.GetAsync(url));
        Assert.Equal(("approved", "unavailable"), (approved.GetProperty("status").GetString(), approved.GetProperty("financeState").GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrExpenseApproved, id.ToString());
        Assert.Equal(1, await world.Database.OutboxCountAsync("ExpenseApproved", t.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await sara.PostAsync($"{url}:withdraw", null)).StatusCode);
    }

    [Fact]
    public async Task A_large_claim_needs_the_tier_approver_and_duplicates_are_flagged()
    {
        var t = await FreshAsync();
        await SetPolicyAsync(t);
        var sara = t["sara"].Client;

        var first = await OkAsync(sara.PostAsJsonAsync(Mine, new
        {
            title = "Taxi", lines = new[] { new { category = "taxi", date = Today.AddDays(-1), amount = 80m, currency = "AED", merchant = "Fictional Cabs" } },
        }), HttpStatusCode.Created);
        Assert.False(first.GetProperty("duplicate").GetBoolean());

        // The same merchant, date and amount again, and a receipt file already on a live claim, are flagged, not refused.
        var receipt = await ReceiptAsync(sara, "hotel");
        var hotel = await OkAsync(sara.PostAsJsonAsync(Mine, new
        {
            title = "Conference",
            lines = new object[]
            {
                new { category = "taxi", date = Today.AddDays(-1), amount = 80m, currency = "AED", merchant = "fictional cabs" },
                new { category = "meals", date = Today.AddDays(-1), amount = 350m, currency = "AED", receiptId = receipt },
                new { category = "per_diem", date = Today.AddDays(-1), days = 5 },
            },
        }), HttpStatusCode.Created);
        var id = hotel.GetProperty("id").GetGuid();
        Assert.True(hotel.GetProperty("duplicate").GetBoolean());
        Assert.Equal([true, false, false], hotel.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("duplicate").GetBoolean()).ToArray());
        Assert.Equal(1180m, hotel.GetProperty("total").GetDecimal());

        var again = await ReceiptAsync(sara, "hotel");
        var copy = await OkAsync(sara.PostAsJsonAsync(Mine, new { title = "Copy", lines = new[] { new { category = "meals", date = Today, amount = 10m, currency = "AED", receiptId = again } } }),
            HttpStatusCode.Created);
        Assert.True(copy.GetProperty("lines")[0].GetProperty("duplicate").GetBoolean());
        Assert.Contains("validation.expenses.receipt_unknown",
            await (await sara.PostAsJsonAsync(Mine, new { title = "Reuse", lines = new[] { new { category = "meals", date = Today, amount = 10m, currency = "AED", receiptId = receipt } } }))
                .Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Above 1,000 AED: manager, then the tier approver, then finance. The approver sees the claim while it waits on them.
        Assert.True((await DecideAsync(t["omar"].Client, id)).IsSuccessStatusCode);
        Assert.True(HasTask(await OkAsync(t["head"].Client.GetAsync("/api/v1/tasks")), id));
        Assert.True((await OkAsync(t["head"].Client.GetAsync($"/api/v1/hr/expenses/{id}"))).GetProperty("rights").GetProperty("decide").GetBoolean());
        Assert.False(HasTask(await OkAsync(t["fin_gulf"].Client.GetAsync("/api/v1/tasks")), id));
        Assert.True((await DecideAsync(t["head"].Client, id)).IsSuccessStatusCode);
        Assert.True((await DecideAsync(t["fin_gulf"].Client, id, "reject")).IsSuccessStatusCode);
        Assert.Equal("rejected", (await OkAsync(sara.GetAsync($"/api/v1/hr/expenses/{id}"))).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrExpenseRejected, id.ToString());

        // Withdrawn while it waits, by its owner only.
        var firstId = first.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["fin_gulf"].Client.PostAsync($"/api/v1/hr/expenses/{firstId}:withdraw", null)).StatusCode);
        Assert.Equal("withdrawn", (await OkAsync(sara.PostAsync($"/api/v1/hr/expenses/{firstId}:withdraw", null))).GetProperty("status").GetString());
        Assert.Equal(3, (await OkAsync(sara.GetAsync(Mine))).GetArrayLength());
    }

    [Fact]
    public async Task The_policy_is_validated_and_needs_the_package()
    {
        var t = await FreshAsync();
        var admin = t["admin"].Client;
        var current = await admin.GetAsync(PolicyUrl);
        var put = new HttpRequestMessage(HttpMethod.Put, PolicyUrl)
        {
            Content = JsonContent.Create(new
            {
                currency = "dirham", windowDays = 0,
                categories = new object[] { new { key = "Meals!", names = new { en = "Meals" }, kind = "per_diem", receipt = false } },
                tiers = new[] { new { above = 500m, approvers = new[] { Guid.NewGuid() } }, new { above = 100m, approvers = new[] { Guid.NewGuid() } } },
                costCentres = Array.Empty<string>(),
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        var refused = await admin.SendAsync(put);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        foreach (var key in new[] { "validation.expenses.currency", "validation.expenses.window", "validation.expenses.category_key", "validation.expenses.category_names",
                     "validation.expenses.category_rate", "validation.expenses.tiers" })
        {
            Assert.Contains(key, text, StringComparison.Ordinal);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/hr/expense-rates", new { rates = new[] { new { currency = "usd", date = Today, rate = 0m } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["fin_gulf"].Client.PutAsJsonAsync("/api/v1/hr/expense-rates",
            new { rates = new[] { new { currency = "USD", date = Today, rate = 3.67m } } })).StatusCode);

        var basic = await world.CreateTenantAsync("ex-w-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync($"{Mine}/form")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync(PolicyUrl)).StatusCode);
    }

    private async Task AllowConfidentialReceiptsAsync(TestTenant t)
    {
        // A zero retention, no training model with vision, allowed by the tenant for Confidential summaries.
        await OkAsync(world.Platform.PutAsJsonAsync("/api/v1/platform/ai/catalogue", new
        {
            model = "fake/teambeats-large", provider = "openrouter", capabilities = new[] { "chat", "tools", "structured_output", "vision" }, contextTokens = 32000,
            regions = new[] { "eu" }, zeroRetention = true, noTraining = true, maxClassification = "confidential", inputPrice = 1.0, outputPrice = 2.0,
        }));
        var routes = AiUseCases.All.ToDictionary(u => u, u => u switch
        {
            AiUseCases.Embed => new[] { "openai/text-embedding-3-small" },
            AiUseCases.Assistant or AiUseCases.Agent => ["openai/gpt-4o-mini", "openai/gpt-4o"],
            _ => ["openai/gpt-4o-mini", "fake/teambeats-large"],
        });
        var client = t["admin"].Client;
        var current = await client.GetAsync("/api/v1/ai/policy");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/ai/policy")
        {
            Content = JsonContent.Create(new
            {
                allowlist = new[] { "openai/gpt-4o-mini", "openai/gpt-4o", "openai/text-embedding-3-small", "fake/teambeats-large" }, routes,
                confidential = new[] { "fake/teambeats-large" },
            }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
        await OkAsync(client.SendAsync(request));
    }

    [Fact]
    public async Task Receipt_reading_only_suggests_values_and_follows_the_tenant_AI_policy()
    {
        var t = await FreshAsync();
        await SetPolicyAsync(t);
        var sara = t["sara"].Client;
        Assert.True((await OkAsync(sara.GetAsync($"{Mine}/form"))).GetProperty("receiptReading").GetBoolean());
        var yesterday = Today.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var receipt = await ReceiptAsync(sara, $$"""[fake:receipt:{"merchant":"Al Baik","date":"{{yesterday}}","amount":42.5,"currency":"sar","tax":99}]""");

        // Receipts are Confidential: under the default AI policy nothing is sent.
        var provider = world.Factory.OpenRouter;
        var before = provider.Requests;
        var blocked = await OkAsync(sara.PostAsync($"{Mine}/receipts/{receipt}:read", null));
        Assert.Equal("policy_blocked", blocked.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, blocked.GetProperty("suggestion").ValueKind);
        Assert.Equal(before, provider.Requests);

        await AllowConfidentialReceiptsAsync(t);
        var read = await OkAsync(sara.PostAsync($"{Mine}/receipts/{receipt}:read", null));
        Assert.Equal("suggested", read.GetProperty("outcome").GetString());
        var suggestion = read.GetProperty("suggestion");
        Assert.Equal("Al Baik", suggestion.GetProperty("merchant").GetString());
        Assert.Equal(yesterday, suggestion.GetProperty("date").GetString());
        Assert.Equal(42.5m, suggestion.GetProperty("amount").GetDecimal());
        Assert.Equal("SAR", suggestion.GetProperty("currency").GetString());
        // A tax above the amount cannot be right, so it is not suggested.
        Assert.Equal(JsonValueKind.Null, suggestion.GetProperty("tax").ValueKind);
        Assert.Equal("fake/teambeats-large", provider.LastModel);

        // A file with nothing on it suggests nothing.
        var blank = await ReceiptAsync(sara, "no receipt text");
        Assert.Equal("nothing_read", (await OkAsync(sara.PostAsync($"{Mine}/receipts/{blank}:read", null))).GetProperty("outcome").GetString());

        // Nothing was stored: the receipt is still unattached and the person can use it on a claim.
        var claim = await OkAsync(sara.PostAsJsonAsync(Mine, new { title = "Lunch", lines = new[] { new { category = "meals", date = Today, amount = 42.5m, currency = "AED", receiptId = receipt } } }),
            HttpStatusCode.Created);
        Assert.Equal(receipt, claim.GetProperty("lines")[0].GetProperty("receiptId").GetGuid());
        // Once on a claim, or for anyone else, the receipt cannot be read again.
        Assert.Equal(HttpStatusCode.NotFound, (await sara.PostAsync($"{Mine}/receipts/{receipt}:read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["omar"].Client.PostAsync($"{Mine}/receipts/{blank}:read", null)).StatusCode);
        var other = await world.CreateTenantAsync("ex-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"{Mine}/receipts/{blank}:read", null)).StatusCode);
        var basic = await world.CreateTenantAsync("ex-r-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.PostAsync($"{Mine}/receipts/{blank}:read", null)).StatusCode);
    }
}
