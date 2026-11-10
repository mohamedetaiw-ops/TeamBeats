using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Integrations.Ticketing;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 T017 and section 12 default: first-party ServiceNow and Jira Service Management ticketing packs with fixed
/// actions, declared inputs and canonical output; a ServiceNow retry finds the incident it already made; business and
/// credential errors map to their classes; settings are checked; the package and tenant boundaries hold. The systems
/// are fake hosts of the test factory.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TicketingPackTests(TenantWorld world)
{
    private const string Base = "/api/v1/integrations/pack-connections";

    private FakeRestApi Rest => world.Factory.Rest;

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus") =>
        await world.CreateTenantAsync("tk-" + Guid.NewGuid().ToString("N")[..8], package);

    private static string Host(string label) => $"{label}-{Guid.NewGuid():N}"[..20] + ".rest.test";

    private static async Task AllowAsync(TestTenant t, string host) =>
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);

    private async Task<Guid> ConnectAsync(TestTenant t, string kind, object settings, string secret = "desk-key")
    {
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, secret), "fictional-desk-password");
        var response = await t["admin"].Client.PostAsJsonAsync(Base, new { kind, displayName = "Service desk", settings });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var test = await (await t["admin"].Client.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());
        Assert.Contains(test.GetProperty("checks").EnumerateArray(), c => c.GetProperty("resultKey").GetString() == "integrations.test.system_ok");
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        return id;
    }

    private static object Basic(TestTenant t, string host, string secret = "desk-key") =>
        new { baseUrl = $"https://{host}", auth = new { type = "basic", username = "teambeats.integration", secretRef = SecretReferences.For(t.Id, secret) } };

    private Task<ConnectorCallResult> InvokeAsync(TestTenant t, Guid connection, string action, object input, string key) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await sp.GetRequiredService<IConnectorRuntime>().InvokeAsync(
                new ConnectorCall(connection, action, JsonSerializer.SerializeToElement(input), key, "corr-ticketing"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    /// <summary>A small ServiceNow incident table: lookups by number or correlation ID, and creates that may fail after acting.</summary>
    private sealed class FakeServiceNow
    {
        public ConcurrentDictionary<string, (string Number, string Correlation)> Incidents { get; } = new();
        public int Posts;
        public bool FailNextAfterCreating;
        public HttpStatusCode? Answer;
        public HttpStatusCode? PostAnswer;

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            if (Answer is { } status)
            {
                return FakeRestApi.Json(status, """{"error":{"message":"refused"}}""");
            }
            if (s.Method == "GET")
            {
                var query = Uri.UnescapeDataString(s.Uri.Query);
                var match = Incidents.Values.Where(i => query.Contains("number=" + i.Number + "&", StringComparison.Ordinal)
                    || query.Contains("correlation_id=" + i.Correlation + "&", StringComparison.Ordinal)).ToList();
                return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { result = match.Select(i => new { number = i.Number, sys_id = "sys-" + i.Number, state = "1" }) }));
            }
            if (PostAnswer is { } refused)
            {
                return FakeRestApi.Json(refused, """{"error":{"message":"Invalid category"}}""");
            }
            Interlocked.Increment(ref Posts);
            var body = JsonDocument.Parse(s.Body!).RootElement;
            var number = $"INC{10000 + Incidents.Count + 1:D7}";
            Incidents[number] = (number, body.GetProperty("correlation_id").GetString()!);
            if (FailNextAfterCreating)
            {
                FailNextAfterCreating = false;
                return FakeRestApi.Json(HttpStatusCode.GatewayTimeout, "{}");
            }
            return FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { result = new { number, sys_id = "sys-" + number, state = "1", internal_notes = "kept out" } }));
        }
    }

    [Fact]
    public async Task ServiceNow_creates_an_incident_with_only_the_declared_fields_and_reads_its_status()
    {
        var t = await TenantAsync();
        var host = Host("snow");
        await AllowAsync(t, host);
        var fake = new FakeServiceNow();
        Rest.On(host, fake.Respond);
        var id = await ConnectAsync(t, ServiceNowPack.KindName, Basic(t, host));

        var view = await (await t["admin"].Client.GetAsync($"{Base}/{id}")).ReadAsync<JsonElement>();
        var create = view.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "create_incident");
        Assert.Equal("write", create.GetProperty("sideEffect").GetString());
        Assert.True(create.GetProperty("idempotent").GetBoolean());
        Assert.False(view.GetProperty("settings").TryGetProperty("serviceDeskId", out var desk) && desk.ValueKind != JsonValueKind.Null);

        // A service owner can place it as a route step: it declares its inputs.
        var routeActions = await (await t["admin"].Client.GetAsync("/api/v1/services/admin/connector-actions")).ReadAsync<JsonElement>();
        Assert.Contains(routeActions.GetProperty("actions").EnumerateArray(),
            a => a.GetProperty("connectionId").GetGuid() == id && a.GetProperty("action").GetString() == "create_incident");

        var result = await InvokeAsync(t, id, "create_incident",
            new { short_description = "Laptop for Mona", description = "Standard laptop", caller = "mona.saleh", urgency = 2, smuggled = "not sent" }, "wf:req-1:step-2");
        Assert.Equal(ConnectorOutcomes.Succeeded, result.Outcome);
        Assert.Equal("INC0010001", result.ExternalReference);
        Assert.Equal("1", result.Output!.Value.GetProperty("state").GetString());
        Assert.False(result.Output!.Value.TryGetProperty("internal_notes", out _));

        var post = Rest.SentTo(host).Single(s => s.Method == "POST");
        Assert.Equal("/api/now/table/incident", post.Uri.AbsolutePath);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("teambeats.integration:fictional-desk-password")), post.Headers["Authorization"]);
        var body = JsonDocument.Parse(post.Body!).RootElement;
        Assert.Equal("Laptop for Mona", body.GetProperty("short_description").GetString());
        Assert.Equal("mona.saleh", body.GetProperty("caller_id").GetString());
        Assert.Equal("2", body.GetProperty("urgency").GetString());
        Assert.Equal(ServiceNowPack.CorrelationId("wf:req-1:step-2"), body.GetProperty("correlation_id").GetString());
        Assert.False(body.TryGetProperty("smuggled", out _));
        Assert.False(body.TryGetProperty("category", out _));

        var status = await InvokeAsync(t, id, "get_incident_status", new { number = "INC0010001" }, "status-1");
        Assert.Equal(ConnectorOutcomes.Succeeded, status.Outcome);
        Assert.Equal("INC0010001", status.ExternalReference);

        var missing = await InvokeAsync(t, id, "get_incident_status", new { number = "INC9999999" }, "status-2");
        Assert.Equal(ConnectorOutcomes.BusinessError, missing.Outcome);
        Assert.Equal(TicketingPack.NotFound, missing.ErrorCode);

        var invalid = await InvokeAsync(t, id, "create_incident", new { description = "No short description" }, "wf:req-2:step-2");
        Assert.Equal(ConnectorErrorClasses.InvalidInput, invalid.ErrorClass);
        Assert.Equal(1, fake.Posts);
    }

    [Fact]
    public async Task A_ServiceNow_retry_after_the_incident_was_made_finds_it_instead_of_making_a_second()
    {
        var t = await TenantAsync();
        var host = Host("snow");
        await AllowAsync(t, host);
        var fake = new FakeServiceNow { FailNextAfterCreating = true };
        Rest.On(host, fake.Respond);
        var id = await ConnectAsync(t, ServiceNowPack.KindName, Basic(t, host));
        var settings = (await (await t["admin"].Client.GetAsync($"{Base}/{id}")).ReadAsync<JsonElement>()).GetProperty("settings");
        var input = JsonSerializer.SerializeToElement(new { short_description = "Laptop for Mona" });

        var (first, second) = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var pack = sp.GetRequiredService<ServiceNowPack>();
            var connection = new PackConnection(t.Id, id, ServiceNowPack.KindName, 1, null, settings);
            var a = await pack.CallAsync(new PackCall(connection, "create_incident", input, "wf:req-9:step-1", 1), CancellationToken.None);
            var b = await pack.CallAsync(new PackCall(connection, "create_incident", input, "wf:req-9:step-1", 2), CancellationToken.None);
            return (a, b);
        });
        Assert.Equal(PackResultKind.Technical, first.Kind);
        Assert.True(first.MayHaveActed);
        Assert.Equal(PackResultKind.Ok, second.Kind);
        Assert.Equal("INC0010001", second.ExternalReference);
        Assert.Equal(1, fake.Posts);
        Assert.Single(fake.Incidents);

        // Through the runtime as well: a second call with a new key makes a new incident, the same key does not.
        Assert.Equal("INC0010002", (await InvokeAsync(t, id, "create_incident", new { short_description = "Monitor" }, "wf:req-10:step-1")).ExternalReference);
        Assert.Equal(2, fake.Posts);
    }

    [Fact]
    public async Task ServiceNow_errors_map_to_their_classes()
    {
        var t = await TenantAsync();
        var host = Host("snow");
        await AllowAsync(t, host);
        var fake = new FakeServiceNow();
        Rest.On(host, fake.Respond);
        var id = await ConnectAsync(t, ServiceNowPack.KindName, Basic(t, host));

        fake.Answer = HttpStatusCode.Unauthorized;
        var refused = await InvokeAsync(t, id, "get_incident_status", new { number = "INC0000001" }, "e-1");
        Assert.Equal(ConnectorErrorClasses.CredentialRefused, refused.ErrorClass);
        var test = await (await t["admin"].Client.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Contains(test.GetProperty("checks").EnumerateArray(), c => c.GetProperty("resultKey").GetString() == "integrations.test.system_refused");

        fake.Answer = HttpStatusCode.BadRequest;
        var business = await InvokeAsync(t, id, "get_incident_status", new { number = "INC0000002" }, "e-2");
        Assert.Equal(ConnectorErrorClasses.RequestRefused, business.ErrorClass);
        var create = await InvokeAsync(t, id, "create_incident", new { short_description = "Bad category" }, "e-3");
        // The lookup before the create answers 400 too, so nothing is created.
        Assert.Equal(ConnectorErrorClasses.RequestRefused, create.ErrorClass);
        Assert.Equal(0, fake.Posts);

        // A 400 on the create itself is the declared business error, so a route can branch on it.
        fake.Answer = null;
        fake.PostAnswer = HttpStatusCode.BadRequest;
        var declared = await InvokeAsync(t, id, "create_incident", new { short_description = "Bad category", category = "nonsense" }, "e-5");
        Assert.Equal(ConnectorOutcomes.BusinessError, declared.Outcome);
        Assert.Equal(TicketingPack.InvalidRequest, declared.ErrorCode);
        Assert.Equal(0, fake.Posts);

        // A number that is not a record number never reaches the system.
        fake.PostAnswer = null;
        var before = Rest.SentTo(host).Count;
        var odd = await InvokeAsync(t, id, "get_incident_status", new { number = "INC1^ORnumber!=x" }, "e-4");
        Assert.Equal(ConnectorErrorClasses.InvalidInput, odd.ErrorClass);
        Assert.Equal(before, Rest.SentTo(host).Count);
    }

    [Fact]
    public async Task Jira_Service_Management_raises_a_request_in_the_configured_desk_and_reads_its_status()
    {
        var t = await TenantAsync();
        var host = Host("jira");
        await AllowAsync(t, host);
        Rest.On(host, s => s.Method switch
        {
            "POST" => FakeRestApi.Json(HttpStatusCode.Created, """{"issueId":"10042","issueKey":"IT-42","currentStatus":{"status":"Waiting for support"},"reporter":{"emailAddress":"kept out"}}"""),
            _ when s.Uri.AbsolutePath == "/rest/servicedeskapi/servicedesk/7" => FakeRestApi.Json(HttpStatusCode.OK, """{"id":"7","projectKey":"IT"}"""),
            _ when s.Uri.AbsolutePath == "/rest/servicedeskapi/request/IT-42" => FakeRestApi.Json(HttpStatusCode.OK, """{"issueId":"10042","issueKey":"IT-42","currentStatus":{"status":"In progress"}}"""),
            _ => FakeRestApi.Json(HttpStatusCode.NotFound, """{"errorMessage":"not found"}"""),
        });
        var settings = new
        {
            baseUrl = $"https://{host}",
            auth = new { type = "basic", username = "it.integration@example.test", secretRef = SecretReferences.For(t.Id, "desk-key") },
            serviceDeskId = "7",
            requestTypeId = "25",
        };
        var id = await ConnectAsync(t, JiraServiceManagementPack.KindName, settings);

        var view = await (await t["admin"].Client.GetAsync($"{Base}/{id}")).ReadAsync<JsonElement>();
        Assert.Equal("7", view.GetProperty("settings").GetProperty("serviceDeskId").GetString());
        var create = view.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "create_request");
        Assert.False(create.GetProperty("idempotent").GetBoolean());

        var result = await InvokeAsync(t, id, "create_request", new { summary = "Laptop for Mona", on_behalf_of = "mona@example.test", other = "not sent" }, "wf:req-3:step-1");
        Assert.Equal(ConnectorOutcomes.Succeeded, result.Outcome);
        Assert.Equal("IT-42", result.ExternalReference);
        Assert.Equal("Waiting for support", result.Output!.Value.GetProperty("status").GetString());
        Assert.DoesNotContain("kept out", result.Output!.Value.GetRawText());
        var post = Rest.SentTo(host).Single(s => s.Method == "POST");
        Assert.Equal("/rest/servicedeskapi/request", post.Uri.AbsolutePath);
        var body = JsonDocument.Parse(post.Body!).RootElement;
        Assert.Equal("7", body.GetProperty("serviceDeskId").GetString());
        Assert.Equal("25", body.GetProperty("requestTypeId").GetString());
        Assert.Equal("mona@example.test", body.GetProperty("raiseOnBehalfOf").GetString());
        Assert.Equal("Laptop for Mona", body.GetProperty("requestFieldValues").GetProperty("summary").GetString());
        Assert.False(body.GetProperty("requestFieldValues").TryGetProperty("description", out _));
        Assert.DoesNotContain("not sent", post.Body!);

        var status = await InvokeAsync(t, id, "get_request_status", new { issue_key = "IT-42" }, "s-1");
        Assert.Equal("In progress", status.Output!.Value.GetProperty("status").GetString());
        var missing = await InvokeAsync(t, id, "get_request_status", new { issue_key = "IT-404" }, "s-2");
        Assert.Equal(ConnectorOutcomes.BusinessError, missing.Outcome);
        Assert.Equal(TicketingPack.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task Ticketing_settings_are_checked_and_the_package_and_tenant_boundaries_hold()
    {
        var t = await TenantAsync();
        var host = Host("jira");
        await AllowAsync(t, host);
        var admin = t["admin"].Client;

        var noDesk = await admin.PostAsJsonAsync(Base, new
        {
            kind = JiraServiceManagementPack.KindName,
            displayName = "Service desk",
            settings = new { baseUrl = $"https://{host}", auth = new { type = "oauth_client_credentials", secretRef = SecretReferences.For(t.Id, "x"), tokenUrl = $"https://{host}/t", clientId = "c" }, serviceDeskId = "IT" },
        });
        var keys = await ErrorKeysAsync(noDesk);
        Assert.Contains("validation.ticketing.service_desk_id", keys);
        Assert.Contains("validation.ticketing.request_type_id", keys);
        Assert.Contains("validation.custom_rest.auth_type", keys);

        var notAllowed = await admin.PostAsJsonAsync(Base, new { kind = ServiceNowPack.KindName, displayName = "Service desk", settings = Basic(t, Host("other")) });
        Assert.Contains("validation.custom_rest.host_not_allowed", await ErrorKeysAsync(notAllowed));
        var foreignSecret = await admin.PostAsJsonAsync(Base, new
        {
            kind = ServiceNowPack.KindName,
            displayName = "Service desk",
            settings = new { baseUrl = $"https://{host}", auth = new { type = "basic", username = "u", secretRef = SecretReferences.For(Guid.NewGuid(), "desk-key") } },
        });
        Assert.Contains("validation.custom_rest.secret_ref", await ErrorKeysAsync(foreignSecret));

        var workplace = await TenantAsync("workplace");
        var packs = await (await workplace["admin"].Client.GetAsync("/api/v1/integrations/packs")).ReadAsync<JsonElement>();
        Assert.False(packs.EnumerateArray().Single(p => p.GetProperty("kind").GetString() == ServiceNowPack.KindName).GetProperty("inPackage").GetBoolean());
        var refused = await workplace["admin"].Client.PostAsJsonAsync(Base, new { kind = ServiceNowPack.KindName, displayName = "Desk", settings = Basic(workplace, host) });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        Rest.On(host, _ => FakeRestApi.Json(HttpStatusCode.OK, """{"result":[]}"""));
        var id = await ConnectAsync(t, ServiceNowPack.KindName, Basic(t, host));
        var other = await TenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Base}/{id}")).StatusCode);
        var crossed = await InvokeAsync(other, id, "get_incident_status", new { number = "INC0000001" }, "x-1");
        Assert.NotEqual(ConnectorOutcomes.Succeeded, crossed.Outcome);
        Assert.Equal(ConnectorErrorClasses.ConnectionMissing, crossed.ErrorClass);
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }
}
