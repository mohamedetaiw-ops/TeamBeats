using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 (R2) T007 and T012: the connection lifecycle for connector packs (add disabled, test, enable, disable, change
/// credentials, delete with confirmation, credential expiry and alerts) and the custom REST connector (actions defined
/// without code, secret references, sign-in methods, business errors, the host allowlist and refused addresses), with
/// package limits and the tenant boundary. External systems are the fake REST hosts of the test factory.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class CustomRestConnectorTests(TenantWorld world)
{
    private const string Base = "/api/v1/integrations/pack-connections";

    private FakeRestApi Rest => world.Factory.Rest;

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus") =>
        await world.CreateTenantAsync("rest-" + Guid.NewGuid().ToString("N")[..8], package);

    private static string Host(string label) => $"{label}-{Guid.NewGuid():N}"[..20] + ".rest.test";

    private static async Task AllowAsync(TestTenant t, string host) =>
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);

    private static object Bearer(TestTenant t, string host, string secret = "permits-key", string? expiresOn = null) => new
    {
        baseUrl = $"https://{host}/v1",
        auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, secret), expiresOn },
    };

    private static async Task<JsonElement> CreateAsync(TestTenant t, object settings, string name = "Facilities permits")
    {
        var response = await t["admin"].Client.PostAsJsonAsync(Base, new { kind = "custom_rest", displayName = name, settings });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static object PermitAction(string sideEffect = "write", string? idempotencyHeader = "Idempotency-Key") => new
    {
        definition = new
        {
            method = "POST",
            path = "/sites/{{site}}/permits",
            query = new Dictionary<string, string> { ["notify"] = "{{notify}}" },
            headers = new Dictionary<string, string> { ["X-Requested-For"] = "{{requester}}" },
            body = JsonSerializer.SerializeToElement(new { visitor = "{{visitor}}", days = "{{days}}", note = "Visit by {{visitor}}" }),
            inputs = new object[]
            {
                new { name = "site", type = "string", required = true },
                new { name = "visitor", type = "string", required = true },
                new { name = "days", type = "integer", required = true },
                new { name = "requester", type = "string", required = false },
                new { name = "notify", type = "boolean", required = false },
            },
            output = new Dictionary<string, string> { ["permitId"] = "$.permit.id", ["status"] = "$.permit.status" },
            referencePath = "$.permit.id",
            sideEffect,
            idempotencyHeader,
            businessErrors = new[] { new { status = 409, code = "permit_exists" } },
        },
    };

    private static readonly JsonElement PermitInput =
        JsonSerializer.SerializeToElement(new { site = "dubai hq/2", visitor = "Mona \"M\" Saleh", days = 3, requester = "hr\r\nX-Evil: 1", ignored = "not sent" });

    private Task<ConnectorCallResult> InvokeAsync(TestTenant t, Guid connection, string action, string key) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await sp.GetRequiredService<IConnectorRuntime>().InvokeAsync(new ConnectorCall(connection, action, PermitInput, key, "corr-rest"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private async Task<Guid> ReadyConnectionAsync(TestTenant t, string host, object? settings = null, object? action = null)
    {
        await AllowAsync(t, host);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "permits-key"), "fictional-bearer-value");
        var created = await CreateAsync(t, settings ?? Bearer(t, host));
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"{Base}/{id}/actions/create_permit", action ?? PermitAction())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        return id;
    }

    private static HttpResponseMessage Permit(string id = "PRM-1001") =>
        FakeRestApi.Json(HttpStatusCode.Created, $$$"""{"permit":{"id":"{{{id}}}","status":"issued","internal":"kept out"}}""");

    [Fact]
    public async Task The_catalogue_lists_packs_with_their_actions_and_marks_what_the_package_allows()
    {
        var plus = await TenantAsync();
        var packs = await (await plus["admin"].Client.GetAsync("/api/v1/integrations/packs")).ReadAsync<JsonElement>();
        var rest = packs.EnumerateArray().Single(p => p.GetProperty("kind").GetString() == "custom_rest");
        Assert.True(rest.GetProperty("inPackage").GetBoolean());
        var ticketing = packs.EnumerateArray().Single(p => p.GetProperty("kind").GetString() == "test_ticketing");
        Assert.Contains(ticketing.GetProperty("actions").EnumerateArray(), a => a.GetProperty("name").GetString() == "create_incident");

        var workplace = await TenantAsync("workplace");
        var none = await (await workplace["admin"].Client.GetAsync("/api/v1/integrations/packs")).ReadAsync<JsonElement>();
        Assert.All(none.EnumerateArray(), p => Assert.False(p.GetProperty("inPackage").GetBoolean()));
        var refused = await workplace["admin"].Client.PostAsJsonAsync(Base, new { kind = "custom_rest", displayName = "Permits", settings = Bearer(workplace, "x.rest.test") });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("entitlement.not_in_package", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_starts_disabled_is_tested_enabled_and_calls_the_system_with_only_the_declared_inputs()
    {
        var t = await TenantAsync();
        var host = Host("permits");
        await AllowAsync(t, host);
        Rest.On(host, _ => Permit());

        var created = await CreateAsync(t, Bearer(t, host));
        var id = created.GetProperty("id").GetGuid();
        Assert.False(created.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", created.GetProperty("status").GetString());
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ConnectionCreated, id.ToString()));
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.CredentialChanged, id.ToString()));

        // The secret is not in the secret manager yet.
        var test = await (await t["admin"].Client.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("failed", test.GetProperty("status").GetString());
        Assert.Contains(test.GetProperty("checks").EnumerateArray(), c => c.GetProperty("resultKey").GetString() == "integrations.test.credential_missing");
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "permits-key"), "fictional-bearer-value");
        test = await (await t["admin"].Client.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());

        var put = await t["admin"].Client.PutAsJsonAsync($"{Base}/{id}/actions/create_permit", PermitAction());
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ActionDefined, id.ToString()));
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"{Base}/{id}/actions/create_permit", PermitAction())).StatusCode);
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ActionChanged, id.ToString()));

        // Disabled connections are never called.
        var early = await InvokeAsync(t, id, "create_permit", "k-early");
        Assert.Equal(ConnectorOutcomes.Refused, early.Outcome);
        Assert.Empty(Rest.SentTo(host));

        var enabled = await (await t["admin"].Client.PostAsync($"{Base}/{id}:enable", null)).ReadAsync<JsonElement>();
        Assert.Equal("healthy", enabled.GetProperty("status").GetString());
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ConnectionEnabled, id.ToString()));
        var view = await (await t["admin"].Client.GetAsync($"{Base}/{id}")).ReadAsync<JsonElement>();
        Assert.Equal("create_permit", view.GetProperty("actions")[0].GetProperty("name").GetString());
        Assert.Equal("write", view.GetProperty("actions")[0].GetProperty("sideEffect").GetString());
        Assert.True(view.GetProperty("actions")[0].GetProperty("idempotent").GetBoolean());

        var result = await InvokeAsync(t, id, "create_permit", "request-7:step-2");
        Assert.Equal(ConnectorOutcomes.Succeeded, result.Outcome);
        Assert.Equal("PRM-1001", result.ExternalReference);
        Assert.Equal("issued", result.Output!.Value.GetProperty("status").GetString());
        Assert.False(result.Output!.Value.TryGetProperty("internal", out _));

        var sent = Assert.Single(Rest.SentTo(host));
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/v1/sites/dubai%20hq%2F2/permits", sent.Uri.AbsolutePath);
        Assert.Equal("", sent.Uri.Query);
        Assert.Equal("Bearer fictional-bearer-value", sent.Headers["Authorization"]);
        Assert.Equal("request-7:step-2", sent.Headers["Idempotency-Key"]);
        Assert.Equal("hrX-Evil: 1", sent.Headers["X-Requested-For"]);
        Assert.False(sent.Headers.ContainsKey("X-Evil"));
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        Assert.Equal("Mona \"M\" Saleh", body.GetProperty("visitor").GetString());
        Assert.Equal(3, body.GetProperty("days").GetInt32());
        Assert.Equal("Visit by Mona \"M\" Saleh", body.GetProperty("note").GetString());
        Assert.DoesNotContain("not sent", sent.Body, StringComparison.Ordinal);
        // The secret value is stored nowhere in the connection.
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from integrations.connection where settings::text like '%fictional-bearer-value%'"));

        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Base}/{id}:disable", null)).StatusCode);
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ConnectionDisabled, id.ToString()));
        var later = await InvokeAsync(t, id, "create_permit", "k-later");
        Assert.Equal(ConnectorErrorClasses.ConnectionDisabled, later.ErrorClass);
        Assert.Single(Rest.SentTo(host));
    }

    [Fact]
    public async Task Changing_credentials_and_settings_is_audited_apart_and_deleting_needs_confirmation_when_used()
    {
        var t = await TenantAsync();
        var host = Host("change");
        var id = await ReadyConnectionAsync(t, host);

        var renamed = await t["admin"].Client.PatchAsJsonAsync($"{Base}/{id}", new { displayName = "Permits (Dubai)", rateLimitPerMinute = 120 });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ConnectionChanged, id.ToString()));
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.CredentialChanged, id.ToString()));

        var rotated = await t["admin"].Client.PatchAsJsonAsync($"{Base}/{id}", new { settings = Bearer(t, host, "permits-key-2") });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Equal(2, await world.Database.AuditCountAsync(t.Id, AuditActions.CredentialChanged, id.ToString()));
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ConnectionChanged, id.ToString()));
        Assert.Equal(120, (await rotated.ReadAsync<JsonElement>()).GetProperty("rateLimitPerMinute").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await t["admin"].Client.PatchAsJsonAsync($"{Base}/{id}", new { rateLimitPerMinute = 0 })).StatusCode);

        var unconfirmed = await t["admin"].Client.DeleteAsync($"{Base}/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Contains("validation.integrations.delete_needs_confirmation", await unconfirmed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var preview = await (await t["admin"].Client.GetAsync($"{Base}/{id}/dependents")).ReadAsync<JsonElement>();
        Assert.Equal("create_permit", preview.GetProperty("enabledFeatures")[0].GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.DeleteAsync($"{Base}/{id}?confirm=true")).StatusCode);
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ConnectionDeleted, id.ToString()));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from integrations.connector_action where connection_id = @c", ("c", id)));
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.GetAsync($"{Base}/{id}")).StatusCode);
    }

    [Fact]
    public async Task Settings_and_action_definitions_are_validated_and_secrets_must_be_references_of_the_tenant()
    {
        var t = await TenantAsync();
        var other = await TenantAsync();
        var host = Host("valid");
        await AllowAsync(t, host);

        async Task<string> Refused(object settings)
        {
            var r = await t["admin"].Client.PostAsJsonAsync(Base, new { kind = "custom_rest", displayName = "Permits", settings });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        Assert.Contains("validation.custom_rest.base_url", await Refused(new { baseUrl = $"http://{host}", auth = new { type = "none" } }), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.base_url", await Refused(new { baseUrl = "https://10.0.0.8", auth = new { type = "none" } }), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.host_not_allowed", await Refused(new { baseUrl = "https://elsewhere.rest.test", auth = new { type = "none" } }), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.secret_ref", await Refused(new { baseUrl = $"https://{host}", auth = new { type = "bearer", secretRef = "plain-token-value" } }), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.secret_ref", await Refused(new { baseUrl = $"https://{host}",
            auth = new { type = "bearer", secretRef = SecretReferences.For(other.Id, "permits-key") } }), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.auth_type", await Refused(new { baseUrl = $"https://{host}", auth = new { type = "kerberos" } }), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.host_not_allowed", await Refused(new { baseUrl = $"https://{host}",
            auth = new { type = "oauth_client_credentials", secretRef = SecretReferences.For(t.Id, "oauth"), tokenUrl = "https://login.other.test/token", clientId = "tb" } }),
            StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.expires_on", await Refused(new { baseUrl = $"https://{host}",
            auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "k"), expiresOn = "31/12/2026" } }), StringComparison.Ordinal);

        var id = (await CreateAsync(t, new { baseUrl = $"https://{host}", auth = new { type = "none" } })).GetProperty("id").GetGuid();

        async Task<string> RefusedAction(object definition, string name = "lookup")
        {
            var r = await t["admin"].Client.PutAsJsonAsync($"{Base}/{id}/actions/{name}", new { definition });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        object Def(string method = "GET", string path = "/permits/{{id}}", object? body = null, Dictionary<string, string>? headers = null,
            Dictionary<string, string>? output = null, string sideEffect = "read", object[]? businessErrors = null) => new
        {
            method, path, headers, body, sideEffect, businessErrors,
            inputs = new[] { new { name = "id", type = "string", required = true } },
            output = output ?? new Dictionary<string, string> { ["status"] = "$.status" },
        };

        Assert.Contains("validation.custom_rest.action_name", await RefusedAction(Def(), "Bad-Name"), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.method", await RefusedAction(Def(method: "TRACE")), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.undeclared_input", await RefusedAction(Def(path: "/permits/{{other}}")), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.path", await RefusedAction(Def(path: "https://evil.test/permits")), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.body_not_allowed", await RefusedAction(Def(body: new { id = "{{id}}" })), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.headers", await RefusedAction(Def(headers: new() { ["Authorization"] = "Bearer x" })), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.output", await RefusedAction(Def(output: new() { ["status"] = "status" })), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.side_effect", await RefusedAction(Def(sideEffect: "maybe")), StringComparison.Ordinal);
        Assert.Contains("validation.custom_rest.business_errors", await RefusedAction(Def(businessErrors: [new { status = 401, code = "nope" }])), StringComparison.Ordinal);

        // The builder test runs a definition once with sample input, before it is saved, and records no run.
        Rest.On(host, s => FakeRestApi.Json(HttpStatusCode.OK, """{"status":"issued"}"""));
        var sample = await t["admin"].Client.PostAsJsonAsync($"{Base}/{id}/actions:test", new { definition = Def(), input = new { id = "P-1" } });
        var tested = await sample.ReadAsync<JsonElement>();
        Assert.Equal("succeeded", tested.GetProperty("outcome").GetString());
        Assert.Equal(200, tested.GetProperty("httpStatus").GetInt32());
        Assert.Equal("issued", tested.GetProperty("output").GetProperty("status").GetString());
        Assert.Equal("/permits/P-1", Assert.Single(Rest.SentTo(host)).Uri.AbsolutePath);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from integrations.run where connection_id = @c", ("c", id)));
        var missing = await t["admin"].Client.PostAsJsonAsync($"{Base}/{id}/actions:test", new { definition = Def(), input = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("validation.custom_rest.sample_input", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declared_4xx_answers_are_business_errors_refused_credentials_stop_and_server_errors_follow_the_retry_rules()
    {
        var t = await TenantAsync();
        var host = Host("errors");
        var id = await ReadyConnectionAsync(t, host);

        Rest.On(host, _ => FakeRestApi.Json(HttpStatusCode.Conflict, """{"error":"exists"}"""));
        var business = await InvokeAsync(t, id, "create_permit", "k-409");
        Assert.Equal(ConnectorOutcomes.BusinessError, business.Outcome);
        Assert.Equal("permit_exists", business.ErrorCode);
        Assert.Single(Rest.SentTo(host));

        Rest.On(host, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var refused = await InvokeAsync(t, id, "create_permit", "k-401");
        Assert.Equal(ConnectorErrorClasses.CredentialRefused, refused.ErrorClass);
        Assert.Equal(2, Rest.SentTo(host).Count);

        Rest.On(host, _ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        var undeclared = await InvokeAsync(t, id, "create_permit", "k-422");
        Assert.Equal(ConnectorErrorClasses.RequestRefused, undeclared.ErrorClass);
        Assert.Equal(3, Rest.SentTo(host).Count);

        // A write with an idempotency header is retried with the same key after a server error.
        var answers = 0;
        Rest.On(host, _ => Interlocked.Increment(ref answers) == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Permit("PRM-2"));
        var retried = await InvokeAsync(t, id, "create_permit", "k-502");
        Assert.Equal(ConnectorOutcomes.Succeeded, retried.Outcome);
        Assert.Equal("PRM-2", retried.ExternalReference);
        Assert.All(Rest.SentTo(host).Skip(3), s => Assert.Equal("k-502", s.Headers["Idempotency-Key"]));

        // Without one, a write that may have acted stops as outcome unknown.
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"{Base}/{id}/actions/create_permit", PermitAction(idempotencyHeader: null))).StatusCode);
        Rest.On(host, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var before = Rest.SentTo(host).Count;
        var unknown = await InvokeAsync(t, id, "create_permit", "k-500");
        Assert.Equal(ConnectorOutcomes.Unknown, unknown.Outcome);
        Assert.Equal(before + 1, Rest.SentTo(host).Count);
    }

    [Fact]
    public async Task OAuth_client_credentials_fetch_a_token_once_reuse_it_and_fetch_again_after_a_refusal()
    {
        var t = await TenantAsync();
        var host = Host("oauth");
        var login = Host("login");
        await AllowAsync(t, login);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "oauth-secret"), "fictional-client-secret");
        var issued = 0;
        Rest.On(login, _ => FakeRestApi.Json(HttpStatusCode.OK, $$"""{"access_token":"tok-{{Interlocked.Increment(ref issued)}}","expires_in":3600}"""));
        var calls = 0;
        Rest.On(host, s => Interlocked.Increment(ref calls) == 3 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Permit());

        var id = await ReadyConnectionAsync(t, host, new
        {
            baseUrl = $"https://{host}",
            auth = new { type = "oauth_client_credentials", secretRef = SecretReferences.For(t.Id, "oauth-secret"), tokenUrl = $"https://{login}/oauth2/token",
                clientId = "teambeats", scope = "permits.write" },
        });
        var check = await (await t["admin"].Client.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Contains(check.GetProperty("checks").EnumerateArray(), c => c.GetProperty("resultKey").GetString() == "integrations.test.token_ok");

        Assert.Equal(ConnectorOutcomes.Succeeded, (await InvokeAsync(t, id, "create_permit", "o-1")).Outcome);
        Assert.Equal(ConnectorOutcomes.Succeeded, (await InvokeAsync(t, id, "create_permit", "o-2")).Outcome);
        var tokenRequest = Rest.SentTo(login)[0];
        Assert.Contains("grant_type=client_credentials", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains("scope=permits.write", tokenRequest.Body, StringComparison.Ordinal);
        // The test fetched a fresh token, and the calls reuse it.
        Assert.Single(Rest.SentTo(login));
        Assert.All(Rest.SentTo(host), s => Assert.Equal("Bearer tok-1", s.Headers["Authorization"]));

        Assert.Equal(ConnectorErrorClasses.CredentialRefused, (await InvokeAsync(t, id, "create_permit", "o-3")).ErrorClass);
        Assert.Equal(ConnectorOutcomes.Succeeded, (await InvokeAsync(t, id, "create_permit", "o-4")).Outcome);
        Assert.Equal(2, Rest.SentTo(login).Count);
        Assert.Equal("Bearer tok-2", Rest.SentTo(host)[^1].Headers["Authorization"]);

        Rest.On(login, _ => FakeRestApi.Json(HttpStatusCode.BadRequest, """{"error":"invalid_client"}"""));
        var refusedTest = await (await t["admin"].Client.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("failed", refusedTest.GetProperty("status").GetString());
        Assert.Contains(refusedTest.GetProperty("checks").EnumerateArray(), c => c.GetProperty("resultKey").GetString() == "integrations.test.token_refused");
    }

    [Fact]
    public async Task Calls_stop_before_leaving_when_the_host_leaves_the_allowlist_or_resolves_to_a_refused_address()
    {
        var t = await TenantAsync();
        var host = Host("gone");
        var id = await ReadyConnectionAsync(t, host);
        Rest.On(host, _ => Permit());

        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.DeleteAsync($"/api/v1/integrations/allowlist/{host}")).StatusCode);
        var gone = await InvokeAsync(t, id, "create_permit", "s-1");
        Assert.Equal(ConnectorErrorClasses.HostNotAllowed, gone.ErrorClass);
        Assert.Empty(Rest.SentTo(host));

        var blockedHost = "intranet.blocked.test";
        var blocked = await ReadyConnectionAsync(t, blockedHost, Bearer(t, blockedHost));
        var refused = await InvokeAsync(t, blocked, "create_permit", "s-2");
        Assert.Equal(ConnectorErrorClasses.BlockedAddress, refused.ErrorClass);
        Assert.Equal(ConnectorOutcomes.Failed, refused.Outcome);
    }

    [Fact]
    public async Task Package_limits_cap_connections_and_actions_and_other_tenants_and_employees_never_reach_them()
    {
        var t = await TenantAsync();
        var host = Host("limits");
        await AllowAsync(t, host);
        for (var i = 0; i < 10; i++)
        {
            await CreateAsync(t, new { baseUrl = $"https://{host}", auth = new { type = "none" } }, $"Permits {i}");
        }
        var eleventh = await t["admin"].Client.PostAsJsonAsync(Base, new { kind = "custom_rest", displayName = "One more",
            settings = new { baseUrl = $"https://{host}", auth = new { type = "none" } } });
        Assert.Equal(HttpStatusCode.Conflict, eleventh.StatusCode);
        Assert.Contains("conflict.integrations.connection_limit", await eleventh.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var list = await (await t["admin"].Client.GetAsync(Base)).ReadAsync<JsonElement>();
        Assert.Equal(10, list.GetArrayLength());
        var id = list[0].GetProperty("id").GetGuid();
        for (var i = 0; i < 100; i++)
        {
            await world.Database.OwnerExecInTenantAsync(t.Id,
                """
                insert into integrations.connector_action (tenant_id, connection_id, name, side_effect, definition, created_by, updated_by)
                values (@t, @c, @n, 'read', '{"method":"GET","path":"/x","sideEffect":"read"}'::jsonb, @u, @u)
                """, ("t", t.Id), ("c", id), ("n", $"action_{i:000}"), ("u", t["admin"].Id));
        }
        var tooMany = await t["admin"].Client.PutAsJsonAsync($"{Base}/{id}/actions/one_more", PermitAction());
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        Assert.Contains("conflict.integrations.action_limit", await tooMany.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var other = await TenantAsync();
        var outsider = other["admin"].Client;
        Assert.Empty((await (await outsider.GetAsync(Base)).ReadAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{Base}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync($"{Base}/{id}/actions/lookup", PermitAction())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"{Base}/{id}?confirm=true")).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from integrations.connection where id = @c", ("c", id)));

        await world.SeedUserAsync(t, "employee", "employee");
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync(Base)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync("/api/v1/integrations/packs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.PostAsync($"{Base}/{id}:test", null)).StatusCode);
    }

    [Fact]
    public async Task An_expiring_credential_marks_the_connection_and_alerts_the_integration_administrators_once()
    {
        var t = await TenantAsync();
        var host = Host("expiry");
        var soon = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10).ToString("yyyy-MM-dd");
        var id = await ReadyConnectionAsync(t, host, Bearer(t, host, expiresOn: soon));

        var view = await (await t["admin"].Client.GetAsync($"{Base}/{id}")).ReadAsync<JsonElement>();
        Assert.Equal("credential_expiring", view.GetProperty("status").GetString());
        Assert.Equal(soon, view.GetProperty("credentialExpiresOn").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from messaging.outbox where message_type = 'ConnectorUnavailable' and payload->>'connectionId' = @c and payload->>'status' = 'credential_expiring'",
            ("c", id.ToString())));

        async Task AlertAsync(string status) => await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await ActivatorUtilities.CreateInstance<ConnectionAlerts>(sp).HandleAsync(new ConnectorUnavailable(id, status), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        await AlertAsync(ConnectionStatuses.CredentialExpiring);
        await AlertAsync(ConnectionStatuses.CredentialExpiring);
        await AlertAsync(ConnectionStatuses.Disabled);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = 'integrations.connection.credential_expiring'", ("u", t["admin"].Id)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where template_key like 'integrations.connection.%'"));

        // Renewing the credential (a new expiry date) makes it healthy again.
        var later = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(200).ToString("yyyy-MM-dd");
        var renewed = await (await t["admin"].Client.PatchAsJsonAsync($"{Base}/{id}", new { settings = Bearer(t, host, expiresOn: later) })).ReadAsync<JsonElement>();
        Assert.Equal("healthy", renewed.GetProperty("status").GetString());

        // The daily check marks credentials that came within 14 days since.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update integrations.connection set credential_expires_at = current_date + 3 where id = @c", ("c", id));
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ConnectorJobs>().PurgeAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal("credential_expiring", await world.Database.TenantScalarAsync<string>(t.Id,
            "select status from integrations.connection where id = @c", ("c", id)));
    }
}
