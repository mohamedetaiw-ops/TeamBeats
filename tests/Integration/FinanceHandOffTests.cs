using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Finance;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 T016 (US7 scenario 2, FR-021): the finance hand-off contract over the generic finance REST pack. Only the
/// mapped fields are sent, the reference is stored, the payment status is read until it is paid, a claim is sent once,
/// and errors, settings, the package and the tenant boundary hold. The finance system is a fake host.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class FinanceHandOffTests(TenantWorld world)
{
    private const string Base = "/api/v1/integrations/pack-connections";

    private FakeRestApi Rest => world.Factory.Rest;

    private static string Host() => $"erp-{Guid.NewGuid():N}"[..20] + ".rest.test";

    /// <summary>A small finance system: claims by reference, the status of each, and the bodies it received.</summary>
    private sealed class FakeErp
    {
        public ConcurrentDictionary<string, string> Status { get; } = new();
        public ConcurrentQueue<(JsonElement Body, string? Key)> Received { get; } = new();
        public HttpStatusCode? Answer;

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            if (Answer is { } status)
            {
                return FakeRestApi.Json(status, """{"error":"refused"}""");
            }
            if (s.Method == "POST")
            {
                var body = JsonDocument.Parse(s.Body!).RootElement.Clone();
                s.Headers.TryGetValue("Idempotency-Key", out var key);
                Received.Enqueue((body, key));
                var reference = $"EXP-{Received.Count:D5}";
                Status[reference] = "OPEN";
                return FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { data = new { id = reference, state = "OPEN", internal_ledger = "kept out" } }));
            }
            var id = s.Uri.AbsolutePath.Split('/')[^1];
            return Status.TryGetValue(id, out var st)
                ? FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new { id, state = st } }))
                : FakeRestApi.Json(HttpStatusCode.NotFound, "{}");
        }
    }

    private static object Settings(TestTenant t, string host, bool idempotent = true) => new
    {
        baseUrl = $"https://{host}/api",
        auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "erp-token") },
        submitPath = "/expenses",
        statusPath = "/expenses/{reference}",
        fields = new Dictionary<string, string>
        {
            ["source_id"] = "externalId", ["employee_number"] = "employee.code", ["amount"] = "total.value", ["currency"] = "total.currency",
            ["claim_date"] = "date",
        },
        linesField = "items",
        lineFields = new Dictionary<string, string> { ["category"] = "glCategory", ["amount"] = "value" },
        referencePath = "$.data.id",
        statusValuePath = "$.data.state",
        statusValues = new Dictionary<string, string[]> { ["submitted"] = ["OPEN"], ["approved"] = ["APPROVED"], ["paid"] = ["PAID", "SETTLED"], ["rejected"] = ["REJECTED"] },
        idempotencyHeader = idempotent ? "Idempotency-Key" : null,
    };

    private async Task<(TestTenant T, Guid Connection, FakeErp Erp, string Host)> SetupAsync(bool idempotent = true)
    {
        var t = await world.CreateTenantAsync("fi-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var host = Host();
        var erp = new FakeErp();
        Rest.On(host, erp.Respond);
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "erp-token"), "fictional-erp-token");
        var created = await admin.PostAsJsonAsync(Base, new { kind = FinancePack.KindName, displayName = "Finance", settings = Settings(t, host, idempotent) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var test = await (await admin.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        return (t, id, erp, host);
    }

    private static FinanceHandOffRequest Claim(Guid? id = null) => new("expense_claim", id ?? Guid.NewGuid(), "E100", "SAR", 350.50m, new DateOnly(2026, 11, 9),
        "Client visit", "CC-200", null, [new FinanceLine("travel", new DateOnly(2026, 11, 8), 300m, "Taxi"), new FinanceLine("meals", new DateOnly(2026, 11, 8), 50.50m)]);

    private Task<T> InScope<T>(TestTenant t, Func<IServiceProvider, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await action(sp);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<FinanceHandOffView> SubmitAsync(TestTenant t, FinanceHandOffRequest claim) =>
        InScope(t, sp => sp.GetRequiredService<IFinanceHandOff>().SubmitAsync(claim));

    private Task<FinanceHandOffView?> GetAsync(TestTenant t, Guid claim) =>
        InScope(t, sp => sp.GetRequiredService<IFinanceHandOff>().GetAsync("expense_claim", claim));

    /// <summary>Runs due jobs and hands each finished submit to the contract, as the outbox does.</summary>
    private async Task RunAsync(TestTenant t)
    {
        await InScope(t, sp => sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 50));
        var finished = await world.Database.OwnerScalarAsync<string>(
            "select coalesce(json_agg(payload), '[]')::text from messaging.outbox where tenant_id = @t and message_type = 'ConnectorActionCompleted'", ("t", t.Id));
        foreach (var e in JsonSerializer.Deserialize<List<ConnectorActionCompleted>>(finished!, JsonDefaults.Options)!)
        {
            await InScope(t, async sp =>
            {
                await sp.GetRequiredService<FinanceHandOffs>().OnCompletedAsync(e, CancellationToken.None);
                return 0;
            });
        }
    }

    private Task<int> CheckAsync(TestTenant t, DateTimeOffset at) =>
        InScope(t, sp => sp.GetRequiredService<FinanceHandOffs>().CheckDueAsync(at, 50, CancellationToken.None));

    [Fact]
    public async Task An_approved_claim_is_sent_with_only_the_mapped_fields_and_its_payment_status_is_read_until_paid()
    {
        var (t, _, erp, host) = await SetupAsync();
        var claim = Claim();
        var queued = await SubmitAsync(t, claim);
        Assert.Equal(FinanceHandOffStates.Queued, queued.State);
        await RunAsync(t);

        var (body, key) = Assert.Single(erp.Received);
        Assert.Equal(claim.SourceId.ToString(), body.GetProperty("externalId").GetString());
        Assert.Equal("E100", body.GetProperty("employee").GetProperty("code").GetString());
        Assert.Equal(350.50m, body.GetProperty("total").GetProperty("value").GetDecimal());
        Assert.Equal("SAR", body.GetProperty("total").GetProperty("currency").GetString());
        Assert.Equal("2026-11-09", body.GetProperty("date").GetString());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(("travel", 300m), (items[0].GetProperty("glCategory").GetString(), items[0].GetProperty("value").GetDecimal()));
        Assert.False(items[0].TryGetProperty("description", out _));
        // Fields that are not mapped are not sent.
        Assert.DoesNotContain("Client visit", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("CC-200", body.GetRawText(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal("Bearer fictional-erp-token", Rest.SentTo(host).First(s => s.Method == "POST").Headers["Authorization"]);

        var sent = await GetAsync(t, claim.SourceId);
        Assert.Equal((FinanceHandOffStates.Sent, "EXP-00001", FinancePaymentStatuses.Submitted), (sent!.State, sent.ExternalReference, sent.PaymentStatus));
        Assert.True(await world.Database.OutboxCountAsync(nameof(FinanceHandOffChanged), t.Id) >= 1);

        // A second approval of the same claim sends nothing.
        Assert.Equal(FinanceHandOffStates.Sent, (await SubmitAsync(t, claim)).State);
        await RunAsync(t);
        Assert.Single(erp.Received);

        // Status checks every 6 hours until paid.
        Assert.Equal(0, await CheckAsync(t, DateTimeOffset.UtcNow.AddHours(1)));
        erp.Status["EXP-00001"] = "APPROVED";
        Assert.Equal(1, await CheckAsync(t, DateTimeOffset.UtcNow.AddHours(7)));
        Assert.Equal(FinancePaymentStatuses.Approved, (await GetAsync(t, claim.SourceId))!.PaymentStatus);
        erp.Status["EXP-00001"] = "SETTLED";
        Assert.Equal(1, await CheckAsync(t, DateTimeOffset.UtcNow.AddHours(14)));
        Assert.Equal(FinancePaymentStatuses.Paid, (await GetAsync(t, claim.SourceId))!.PaymentStatus);
        Assert.Equal(0, await CheckAsync(t, DateTimeOffset.UtcNow.AddHours(30)));
        Assert.Equal(3, await world.Database.OutboxCountAsync(nameof(FinanceHandOffChanged), t.Id));

        // Administrators see the hand-offs on the connection, without claim content.
        var connection = await world.Database.TenantScalarAsync<Guid>(t.Id, "select connection_id from integrations.finance_handoff limit 1");
        var list = await (await t["admin"].Client.GetAsync($"{Base}/{connection}/finance-handoffs")).ReadAsync<JsonElement>();
        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal(("EXP-00001", "paid"), (row.GetProperty("externalReference").GetString(), row.GetProperty("paymentStatus").GetString()));
        Assert.DoesNotContain("E100", list.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_claim_fails_with_its_business_error_and_can_be_sent_again()
    {
        var (t, _, erp, _) = await SetupAsync();
        var claim = Claim();
        erp.Answer = HttpStatusCode.UnprocessableEntity;
        await SubmitAsync(t, claim);
        await RunAsync(t);
        var failed = await GetAsync(t, claim.SourceId);
        Assert.Equal((FinanceHandOffStates.Failed, ConnectorErrorClasses.BusinessError), (failed!.State, failed.ErrorClass));

        erp.Answer = null;
        Assert.Equal(FinanceHandOffStates.Queued, (await SubmitAsync(t, claim)).State);
        await RunAsync(t);
        Assert.Equal(FinanceHandOffStates.Sent, (await GetAsync(t, claim.SourceId))!.State);
        Assert.Single(erp.Received);
    }

    [Fact]
    public async Task Without_an_idempotency_header_a_timeout_after_sending_stops_as_unknown()
    {
        var (t, _, erp, _) = await SetupAsync(idempotent: false);
        var claim = Claim();
        erp.Answer = HttpStatusCode.GatewayTimeout;
        await SubmitAsync(t, claim);
        await RunAsync(t);
        Assert.Equal(FinanceHandOffStates.Unknown, (await GetAsync(t, claim.SourceId))!.State);
    }

    [Fact]
    public async Task Claims_and_settings_are_checked_and_the_package_and_tenant_boundary_hold()
    {
        var (t, connection, _, host) = await SetupAsync();
        await Assert.ThrowsAsync<AppException>(() => SubmitAsync(t, Claim() with { Amount = 1m }));
        await Assert.ThrowsAsync<AppException>(() => SubmitAsync(t, Claim() with { Currency = "sar" }));

        var bad = await t["admin"].Client.PostAsJsonAsync(Base, new
        {
            kind = FinancePack.KindName,
            displayName = "Finance",
            settings = new
            {
                baseUrl = $"https://{host}/api",
                auth = new { type = "none" },
                submitPath = "/expenses/{x}",
                statusPath = "/expenses",
                fields = new Dictionary<string, string> { ["amount"] = "total", ["currency"] = "total.currency", ["salary"] = "x" },
                referencePath = "data.id",
                statusValues = new Dictionary<string, string[]> { ["approved"] = ["OK"] },
                idempotencyHeader = "Authorization",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var text = await bad.Content.ReadAsStringAsync();
        foreach (var key in new[]
        {
            "validation.custom_rest.auth_type", "validation.finance.submit_path", "validation.finance.status_path", "validation.finance.field_name",
            "validation.finance.field_twice", "validation.finance.fields_required", "validation.finance.response_path", "validation.finance.status_values",
            "validation.custom_rest.headers",
        })
        {
            Assert.Contains(key, text, StringComparison.Ordinal);
        }

        // Claims carry personal finance data, so payload capture is not offered on a finance connection.
        var capture = await t["admin"].Client.PutAsJsonAsync($"/api/v1/integrations/connections/{connection}/capture", new { hours = 1, publicOrInternal = true });
        Assert.Equal(HttpStatusCode.BadRequest, capture.StatusCode);
        Assert.Contains("validation.connector.capture_kind", await capture.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Another tenant has no finance connection, and never reaches this one.
        var other = await world.CreateTenantAsync("fi-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(FinanceHandOffStates.Unavailable, (await SubmitAsync(other, Claim())).State);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Base}/{connection}/finance-handoffs")).StatusCode);
        var workplace = await world.CreateTenantAsync("fi-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(FinanceHandOffStates.Unavailable, (await SubmitAsync(workplace, Claim())).State);
    }
}
