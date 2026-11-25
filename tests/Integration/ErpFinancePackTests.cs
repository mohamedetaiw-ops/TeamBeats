using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Finance;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 R3 (INT-005, INT-006): the Oracle Fusion and SAP S/4HANA finance packs behind the finance hand-off contract.
/// A claim becomes a supplier invoice in the ERP's own API shape, the status is read in the ERP's words, a retried submit
/// finds the invoice an earlier attempt made instead of posting it twice, SAP writes carry a CSRF token, and settings, the
/// package and the tenant boundary hold. The ERPs are fake hosts; every name, number and amount is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ErpFinancePackTests(TenantWorld world)
{
    private const string Base = "/api/v1/integrations/pack-connections";

    private FakeRestApi Rest => world.Factory.Rest;

    private static string Host(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + ".rest.test";

    /// <summary>A small Oracle Fusion Payables: invoices by ID with their paid and approval status.</summary>
    private sealed class FakeOracle
    {
        public ConcurrentDictionary<string, (string Number, string Paid, string Approval)> Invoices { get; } = new();
        public ConcurrentQueue<JsonElement> Posted { get; } = new();
        public HttpStatusCode? AfterCreate;

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            var path = s.Uri.AbsolutePath;
            if (!path.StartsWith(OracleFusionFinancePack.Resource, StringComparison.Ordinal))
            {
                return FakeRestApi.Json(HttpStatusCode.NotFound, "{}");
            }
            if (s.Method == "POST")
            {
                var body = JsonDocument.Parse(s.Body!).RootElement.Clone();
                Posted.Enqueue(body);
                var id = (300000100 + Posted.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Invoices[id] = (body.GetProperty("InvoiceNumber").GetString()!, "Unpaid", "Required");
                if (AfterCreate is { } status)
                {
                    AfterCreate = null;
                    return FakeRestApi.Json(status, "{}");
                }
                return FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { InvoiceId = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture), InvoiceNumber = body.GetProperty("InvoiceNumber").GetString() }));
            }
            var query = Uri.UnescapeDataString(s.Uri.Query);
            if (path == OracleFusionFinancePack.Resource)
            {
                var items = Invoices.Where(i => query.Contains($"InvoiceNumber='{i.Value.Number}'", StringComparison.Ordinal))
                    .Select(i => new { InvoiceId = long.Parse(i.Key, System.Globalization.CultureInfo.InvariantCulture) }).ToList();
                return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items, count = items.Count }));
            }
            var key = path.Split('/')[^1];
            return Invoices.TryGetValue(key, out var invoice)
                ? FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { InvoiceId = long.Parse(key, System.Globalization.CultureInfo.InvariantCulture), PaidStatus = invoice.Paid, ApprovalStatus = invoice.Approval, CanceledFlag = false }))
                : FakeRestApi.Json(HttpStatusCode.NotFound, "{}");
        }
    }

    /// <summary>A small SAP supplier invoice service that insists on a CSRF token and its session cookie for writes.</summary>
    private sealed class FakeSap
    {
        public const string Token = "fictional-csrf-token";
        public ConcurrentDictionary<string, string> Status { get; } = new();
        public ConcurrentQueue<(JsonElement Body, IReadOnlyDictionary<string, string> Headers)> Posted { get; } = new();
        public bool RefuseNextToken;

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            var path = s.Uri.AbsolutePath;
            if (!path.StartsWith(SapS4FinancePack.Service, StringComparison.Ordinal))
            {
                return FakeRestApi.Json(HttpStatusCode.NotFound, "{}");
            }
            if (s.Method == "GET" && path.TrimEnd('/') == SapS4FinancePack.Service)
            {
                var answer = FakeRestApi.Json(HttpStatusCode.OK, """{"d":{"EntitySets":["A_SupplierInvoice"]}}""");
                if (s.Headers.TryGetValue("x-csrf-token", out var fetch) && fetch == "Fetch")
                {
                    answer.Headers.Add("x-csrf-token", Token);
                }
                answer.Headers.Add("Set-Cookie", "SAP_SESSIONID_FAKE=abc123; path=/; secure; HttpOnly");
                return answer;
            }
            if (s.Method == "POST")
            {
                if (RefuseNextToken || !s.Headers.TryGetValue("x-csrf-token", out var token) || token != Token
                    || !s.Headers.TryGetValue("Cookie", out var cookie) || !cookie.Contains("SAP_SESSIONID_FAKE=abc123", StringComparison.Ordinal))
                {
                    RefuseNextToken = false;
                    var refused = FakeRestApi.Json(HttpStatusCode.Forbidden, """{"error":{"message":"CSRF token validation failed"}}""");
                    refused.Headers.Add("x-csrf-token", "Required");
                    return refused;
                }
                var body = JsonDocument.Parse(s.Body!).RootElement.Clone();
                Posted.Enqueue((body, s.Headers));
                var invoice = (5100000000 + Posted.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Status[$"{invoice}-2026"] = "A";
                return FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { d = new { SupplierInvoice = invoice, FiscalYear = "2026", InvoicingParty = "kept out" } }));
            }
            if (path.EndsWith("/A_SupplierInvoice", StringComparison.Ordinal))
            {
                return FakeRestApi.Json(HttpStatusCode.OK, """{"d":{"results":[]}}""");
            }
            var match = System.Text.RegularExpressions.Regex.Match(Uri.UnescapeDataString(path), @"SupplierInvoice='(\d+)',FiscalYear='(\d+)'");
            return match.Success && Status.TryGetValue($"{match.Groups[1].Value}-{match.Groups[2].Value}", out var status)
                ? FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { d = new { SupplierInvoiceStatus = status } }))
                : FakeRestApi.Json(HttpStatusCode.NotFound, "{}");
        }
    }

    private static object OracleSettings(TestTenant t, string host) => new
    {
        baseUrl = $"https://{host}",
        auth = new { type = "basic", username = "tb.integration", secretRef = SecretReferences.For(t.Id, "oracle-password") },
        businessUnit = "Fictional Gulf BU",
        supplierSite = "EMPLOYEE",
        accounts = new Dictionary<string, string> { ["travel"] = "101.10.61100.000" },
        defaultAccount = "101.10.69900.000",
    };

    private static object SapSettings(TestTenant t, string host) => new
    {
        baseUrl = $"https://{host}",
        auth = new { type = "basic", username = "TB_COMM_USER", secretRef = SecretReferences.For(t.Id, "sap-password") },
        companyCode = "1010",
        client = "100",
        accounts = new Dictionary<string, string> { ["travel"] = "61100000" },
        defaultAccount = "69900000",
    };

    private async Task<Guid> ConnectAsync(TestTenant t, string kind, string host, object settings, string secret)
    {
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, secret), "fictional-erp-password");
        var created = await admin.PostAsJsonAsync(Base, new { kind, displayName = "ERP", settings });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var test = await (await admin.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.True(test.GetProperty("status").GetString() == "ok", test.GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        return id;
    }

    private static FinanceHandOffRequest Claim() => new("expense_claim", Guid.NewGuid(), "EMP-1001", "AED", 420.75m, new DateOnly(2026, 11, 12),
        "Site visit", "CC1200", null, [new FinanceLine("travel", new DateOnly(2026, 11, 11), 400m, "Taxi to site"), new FinanceLine("meals", new DateOnly(2026, 11, 11), 20.75m)]);

    private Task<T> InScope<T>(TestTenant t, Func<IServiceProvider, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await action(sp);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<FinanceHandOffView> SubmitAsync(TestTenant t, FinanceHandOffRequest claim) => InScope(t, sp => sp.GetRequiredService<IFinanceHandOff>().SubmitAsync(claim));

    private Task<FinanceHandOffView?> GetAsync(TestTenant t, Guid claim) => InScope(t, sp => sp.GetRequiredService<IFinanceHandOff>().GetAsync("expense_claim", claim));

    /// <summary>Runs the jobs due at <paramref name="at"/> and hands each finished submit to the contract, as the outbox does.</summary>
    private async Task RunAsync(TestTenant t, DateTimeOffset? at = null)
    {
        await InScope(t, sp => sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(at ?? DateTimeOffset.UtcNow.AddSeconds(1), 50));
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
    public async Task Oracle_Fusion_gets_a_payables_invoice_per_claim_and_its_status_is_read_until_paid()
    {
        var t = await world.CreateTenantAsync("erp-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        var host = Host("ora");
        var oracle = new FakeOracle();
        Rest.On(host, oracle.Respond);
        await ConnectAsync(t, OracleFusionFinancePack.KindName, host, OracleSettings(t, host), "oracle-password");

        var claim = Claim();
        await SubmitAsync(t, claim);
        await RunAsync(t);
        var body = Assert.Single(oracle.Posted);
        Assert.Equal("TB-" + claim.SourceId.ToString("N"), body.GetProperty("InvoiceNumber").GetString());
        Assert.Equal(("EMP-1001", "Fictional Gulf BU", "EMPLOYEE", "Standard"), (body.GetProperty("SupplierNumber").GetString(), body.GetProperty("BusinessUnit").GetString(),
            body.GetProperty("SupplierSite").GetString(), body.GetProperty("InvoiceType").GetString()));
        Assert.Equal((420.75m, "AED", "2026-11-12"), (body.GetProperty("InvoiceAmount").GetDecimal(), body.GetProperty("InvoiceCurrency").GetString(), body.GetProperty("InvoiceDate").GetString()));
        var lines = body.GetProperty("invoiceLines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal((1, "Item", 400m, "101.10.61100.000"), (lines[0].GetProperty("LineNumber").GetInt32(), lines[0].GetProperty("LineType").GetString(),
            lines[0].GetProperty("LineAmount").GetDecimal(), lines[0].GetProperty("DistributionCombination").GetString()));
        // A category without its own account goes to the default account.
        Assert.Equal("101.10.69900.000", lines[1].GetProperty("DistributionCombination").GetString());
        Assert.StartsWith("Basic ", Rest.SentTo(host).First(s => s.Method == "POST").Headers["Authorization"], StringComparison.Ordinal);

        var sent = await GetAsync(t, claim.SourceId);
        Assert.Equal((FinanceHandOffStates.Sent, "300000101", FinancePaymentStatuses.Submitted), (sent!.State, sent.ExternalReference, sent.PaymentStatus));

        oracle.Invoices["300000101"] = oracle.Invoices["300000101"] with { Approval = "Approved" };
        Assert.Equal(1, await CheckAsync(t, DateTimeOffset.UtcNow.AddHours(7)));
        Assert.Equal(FinancePaymentStatuses.Approved, (await GetAsync(t, claim.SourceId))!.PaymentStatus);
        oracle.Invoices["300000101"] = oracle.Invoices["300000101"] with { Paid = "Paid" };
        Assert.Equal(1, await CheckAsync(t, DateTimeOffset.UtcNow.AddHours(14)));
        Assert.Equal(FinancePaymentStatuses.Paid, (await GetAsync(t, claim.SourceId))!.PaymentStatus);
        Assert.Equal(0, await CheckAsync(t, DateTimeOffset.UtcNow.AddHours(30)));
    }

    [Fact]
    public async Task A_retry_after_a_timeout_finds_the_invoice_the_first_attempt_made()
    {
        var t = await world.CreateTenantAsync("erp-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        var host = Host("ora");
        var oracle = new FakeOracle { AfterCreate = HttpStatusCode.GatewayTimeout };
        Rest.On(host, oracle.Respond);
        await ConnectAsync(t, OracleFusionFinancePack.KindName, host, OracleSettings(t, host), "oracle-password");

        var claim = Claim();
        await SubmitAsync(t, claim);
        await RunAsync(t);
        Assert.Equal(FinanceHandOffStates.Queued, (await GetAsync(t, claim.SourceId))!.State);
        await RunAsync(t, DateTimeOffset.UtcNow.AddHours(1));
        // The invoice was created once; the retry looked it up by number and took its ID.
        Assert.Single(oracle.Posted);
        var sent = await GetAsync(t, claim.SourceId);
        Assert.Equal((FinanceHandOffStates.Sent, "300000101"), (sent!.State, sent.ExternalReference));
    }

    [Fact]
    public async Task SAP_S4HANA_gets_a_supplier_invoice_with_a_CSRF_token_and_posted_reads_as_approved()
    {
        var t = await world.CreateTenantAsync("erp-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        var host = Host("sap");
        var sap = new FakeSap { RefuseNextToken = true };
        Rest.On(host, sap.Respond);
        await ConnectAsync(t, SapS4FinancePack.KindName, host, SapSettings(t, host), "sap-password");

        var claim = Claim();
        await SubmitAsync(t, claim);
        // The first write is refused for its token: nothing was posted, so the job tries again with a fresh token.
        await RunAsync(t);
        await RunAsync(t, DateTimeOffset.UtcNow.AddHours(1));
        var (body, headers) = Assert.Single(sap.Posted);
        Assert.Equal(FakeSap.Token, headers["x-csrf-token"]);
        Assert.Contains("sap-client=100", Rest.SentTo(host).Last(s => s.Method == "POST").Uri.Query, StringComparison.Ordinal);
        Assert.Equal(("1010", "EMP-1001", "AED", "420.75"), (body.GetProperty("CompanyCode").GetString(), body.GetProperty("InvoicingParty").GetString(),
            body.GetProperty("DocumentCurrency").GetString(), body.GetProperty("InvoiceGrossAmount").GetString()));
        Assert.Equal("2026-11-12T00:00:00", body.GetProperty("DocumentDate").GetString());
        Assert.Equal(claim.SourceId.ToString("N")[..16].ToUpperInvariant(), body.GetProperty("SupplierInvoiceIDByInvcgParty").GetString());
        var items = body.GetProperty("to_SupplierInvoiceItemGLAcct").EnumerateArray().ToList();
        Assert.Equal(("1", "61100000", "400.00", "S", "CC1200"), (items[0].GetProperty("SupplierInvoiceItem").GetString(), items[0].GetProperty("GLAccount").GetString(),
            items[0].GetProperty("SupplierInvoiceItemAmount").GetString(), items[0].GetProperty("DebitCreditCode").GetString(), items[0].GetProperty("CostCenter").GetString()));
        Assert.Equal("69900000", items[1].GetProperty("GLAccount").GetString());

        var sent = await GetAsync(t, claim.SourceId);
        Assert.Equal((FinanceHandOffStates.Sent, "5100000001-2026"), (sent!.State, sent.ExternalReference));
        sap.Status["5100000001-2026"] = "5";
        Assert.Equal(1, await CheckAsync(t, DateTimeOffset.UtcNow.AddHours(7)));
        Assert.Equal(FinancePaymentStatuses.Approved, (await GetAsync(t, claim.SourceId))!.PaymentStatus);
    }

    [Fact]
    public async Task Settings_are_checked_and_the_package_and_tenant_boundary_hold()
    {
        var t = await world.CreateTenantAsync("erp-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        var host = Host("sap");
        Rest.On(host, new FakeSap().Respond);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        var bad = await t["admin"].Client.PostAsJsonAsync(Base, new
        {
            kind = SapS4FinancePack.KindName,
            displayName = "SAP",
            settings = new
            {
                baseUrl = $"https://{host}",
                auth = new { type = "api_key", header = "X-Key", secretRef = SecretReferences.For(t.Id, "sap-password") },
                companyCode = "TOO-LONG",
                client = "1",
                accounts = new Dictionary<string, string> { ["travel"] = "GL-ACCOUNT" },
                statusValues = new Dictionary<string, string[]> { ["settled"] = ["9"] },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var text = await bad.Content.ReadAsStringAsync();
        foreach (var key in new[] { "validation.custom_rest.auth_type", "validation.finance.erp_setting", "validation.finance.account", "validation.finance.erp_status_values" })
        {
            Assert.Contains(key, text, StringComparison.Ordinal);
        }
        var oracleBad = await t["admin"].Client.PostAsJsonAsync(Base, new
        {
            kind = OracleFusionFinancePack.KindName,
            displayName = "Oracle",
            settings = new { baseUrl = $"https://{host}", auth = new { type = "basic", username = "u", secretRef = SecretReferences.For(t.Id, "x") }, defaultAccount = "1" },
        });
        Assert.Contains("businessUnit", await oracleBad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The packs are listed for the package, and only Enterprise and Government has them.
        var packs = await (await t["admin"].Client.GetAsync("/api/v1/integrations/packs")).ReadAsync<JsonElement>();
        Assert.True(packs.EnumerateArray().Single(p => p.GetProperty("kind").GetString() == SapS4FinancePack.KindName).GetProperty("inPackage").GetBoolean());
        var plus = await world.CreateTenantAsync("erp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.OK, (await plus["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(plus.Id, "sap-password"), "fictional-erp-password");
        var refused = await plus["admin"].Client.PostAsJsonAsync(Base, new { kind = SapS4FinancePack.KindName, displayName = "SAP", settings = SapSettings(plus, host) });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var plusPacks = await (await plus["admin"].Client.GetAsync("/api/v1/integrations/packs")).ReadAsync<JsonElement>();
        Assert.False(plusPacks.EnumerateArray().Single(p => p.GetProperty("kind").GetString() == OracleFusionFinancePack.KindName).GetProperty("inPackage").GetBoolean());

        // With the SAP pack turned off, an enabled SAP connection sends nothing.
        var sap = new FakeSap();
        var live = Host("sap");
        Rest.On(live, sap.Respond);
        await ConnectAsync(t, SapS4FinancePack.KindName, live, SapSettings(t, live), "sap-password");
        await world.SetCapabilityAsync(t, Capabilities.IntegrationSap, new { enabled = false });
        var claim = Claim();
        await SubmitAsync(t, claim);
        await RunAsync(t);
        Assert.Empty(sap.Posted);
        Assert.NotEqual(FinanceHandOffStates.Sent, (await GetAsync(t, claim.SourceId))!.State);

        // Another tenant never reaches this tenant's ERP connection.
        var other = await world.CreateTenantAsync("erp-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        Assert.Equal(FinanceHandOffStates.Unavailable, (await SubmitAsync(other, Claim())).State);
    }
}
