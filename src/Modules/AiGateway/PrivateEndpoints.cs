using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>A model served by a private endpoint, with what it can do and its context size.</summary>
public sealed record PrivateModel(string Name, IReadOnlyList<string> Capabilities, int ContextTokens);

public sealed record PrivateEndpointRequest(string? Name, string? BaseUrl, string? SecretRef, List<PrivateModel>? Models, string? Region, string? MaxClassification,
    int? Version = null);

/// <summary>
/// A registered private endpoint. <paramref name="SecretRef"/> is the secret manager reference, never the key itself.
/// <paramref name="TestCurrent"/> says whether a passed test is less than 24 hours old, which enabling needs.
/// </summary>
public sealed record PrivateEndpointView(Guid Id, string Name, string BaseUrl, string SecretRef, IReadOnlyList<PrivateModel> Models, string Region,
    string MaxClassification, string Status, int Version, DateTimeOffset? LastTestAt, bool? LastTestPassed, IReadOnlyList<EndpointCheck> Checks, bool TestCurrent,
    IReadOnlyList<string> CatalogueModels, DateTimeOffset UpdatedAt);

public sealed record PrivateEndpointsView(bool Editable, string? ReadOnlyReason, IReadOnlyList<PrivateEndpointView> Endpoints);

/// <summary>
/// Private model endpoints (spec 013 US6, FR-004): OpenAI-compatible servers a tenant runs itself (vLLM, Azure OpenAI in
/// its own subscription). The AI Policy Administrator registers one with its address, a secret manager reference for the
/// key, its models, region and highest classification (up to Restricted). It starts disabled and can be enabled only
/// after a connection and capability test passed in the last 24 hours (section 6). Enabled endpoints add their models
/// to the tenant's catalogue as <c>private/{endpoint}/{model}</c>, for that tenant only.
/// </summary>
public sealed class PrivateEndpoints(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    ISecretStore secrets,
    IEnumerable<IPrivateEndpointProbe> probes,
    IAuditWriter audit,
    IClock clock)
{
    public const string ProviderKey = "private";
    public const int MaxEndpoints = 20;
    public const int MaxModels = 20;
    public static readonly TimeSpan TestValidFor = TimeSpan.FromHours(24);

    private static readonly string[] Levels = ["public", "internal", "confidential", "restricted"];

    /// <summary>The catalogue name of a private model: the endpoint and the server's own model name.</summary>
    public static string ModelName(Guid endpoint, string model) => $"{ProviderKey}/{endpoint:N}/{model}";

    /// <summary>The endpoint and server model name of a catalogue name, or null for any other model.</summary>
    public static (Guid Endpoint, string Model)? Parse(string model)
    {
        var parts = model.Split('/', 3);
        return parts.Length == 3 && parts[0] == ProviderKey && Guid.TryParseExact(parts[1], "N", out var id) ? (id, parts[2]) : null;
    }

    private const string Columns = "id, name, base_url, secret_ref, models::text, region, max_classification, status, version, last_test_at, last_test_passed, last_test::text, updated_at";

    private sealed record Row(Guid Id, string Name, string BaseUrl, string SecretRef, List<PrivateModel> Models, string Region, string MaxClassification, string Status,
        int Version, DateTimeOffset? LastTestAt, bool? LastTestPassed, List<EndpointCheck> Checks, DateTimeOffset UpdatedAt);

    private static Row Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
        JsonSerializer.Deserialize<List<PrivateModel>>(r.GetString(4), JsonDefaults.Options) ?? [], r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8),
        r.Get<DateTimeOffset?>("last_test_at"), r.Get<bool?>("last_test_passed"),
        r.Get<string?>("last_test") is { } checks ? JsonSerializer.Deserialize<List<EndpointCheck>>(checks, JsonDefaults.Options) ?? [] : [], r.GetFieldValue<DateTimeOffset>(12));

    private bool Current(Row r) => r.LastTestPassed == true && r.LastTestAt is { } at && clock.UtcNow - at < TestValidFor;

    private PrivateEndpointView View(Row r) => new(r.Id, r.Name, r.BaseUrl, r.SecretRef, r.Models, r.Region, r.MaxClassification, r.Status, r.Version, r.LastTestAt,
        r.LastTestPassed, r.Checks, Current(r), [.. r.Models.Select(m => ModelName(r.Id, m.Name))], r.UpdatedAt);

    private async Task<bool> CanManageAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed;

    private async Task DemandViewAsync(CancellationToken ct)
    {
        if (!await CanManageAsync(ct) && !(await authz.CheckAsync(Permissions.AiPolicyView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
    }

    private async Task DemandManageAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.AiModelsPrivateEndpoint, ct);
    }

    public async Task<PrivateEndpointsView> ListAsync(CancellationToken ct)
    {
        await DemandViewAsync(ct);
        var rows = await session.QueryAsync($"select {Columns} from ai.private_endpoint order by created_at, id", null, Read, ct);
        var manage = await CanManageAsync(ct);
        var enabled = await entitlements.IsEnabledAsync(Capabilities.AiModelsPrivateEndpoint, ct);
        return new PrivateEndpointsView(manage && enabled, !manage ? "ai.endpoints.read_only_permission" : !enabled ? "ai.endpoints.read_only_package" : null,
            [.. rows.Select(View)]);
    }

    private async Task<Row> LoadAsync(Guid id, CancellationToken ct, bool forUpdate = false)
    {
        var rows = await session.QueryAsync($"select {Columns} from ai.private_endpoint where id = @id{(forUpdate ? " for update" : string.Empty)}", new { id }, Read, ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }

    public async Task<PrivateEndpointView> GetAsync(Guid id, CancellationToken ct)
    {
        await DemandViewAsync(ct);
        return View(await LoadAsync(id, ct));
    }

    private PrivateEndpointRequest Validate(PrivateEndpointRequest request)
    {
        var errors = new ValidationErrors();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 80)
        {
            errors.Add("name", "validation.ai.endpoint_name");
        }
        var url = request.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 || url.Length > 500)
        {
            errors.Add("baseUrl", "validation.ai.endpoint_url");
        }
        if (SecretReferences.NameFor(context.RequireTenant(), request.SecretRef) is null)
        {
            errors.Add("secretRef", "validation.ai.endpoint_secret");
        }
        var models = request.Models ?? [];
        if (models.Count is 0 or > MaxModels || models.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != models.Count ||
            models.Any(m => string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 120 || m.Name.Any(char.IsWhiteSpace) || m.Capabilities is not { Count: > 0 } ||
                m.Capabilities.Any(c => !ModelCapabilities.All.Contains(c)) || m.ContextTokens is <= 0 or > 10_000_000))
        {
            errors.Add("models", "validation.ai.endpoint_models");
        }
        if (request.Region is not { Length: >= 2 and <= 20 })
        {
            errors.Add("region", "validation.ai.region");
        }
        if (request.MaxClassification is null || !Levels.Contains(request.MaxClassification))
        {
            errors.Add("maxClassification", "validation.ai.classification");
        }
        errors.ThrowIfAny();
        return request with
        {
            Name = name, BaseUrl = url,
            Models = [.. models.Select(m => m with { Capabilities = [.. m.Capabilities.Distinct(StringComparer.Ordinal)] })],
        };
    }

    public async Task<PrivateEndpointView> CreateAsync(PrivateEndpointRequest request, CancellationToken ct)
    {
        await DemandManageAsync(ct);
        var r = Validate(request);
        if (await session.ScalarAsync<long>("select count(*) from ai.private_endpoint", null, ct) >= MaxEndpoints)
        {
            throw AppException.Conflict("conflict.ai.endpoint_limit");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into ai.private_endpoint (tenant_id, id, name, base_url, secret_ref, models, region, max_classification, status, version, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @name, @url, @secret, @models, @region, @level, 'disabled', 1, @me, @now, @now)
            """,
            new { id, name = r.Name, url = r.BaseUrl, secret = r.SecretRef, models = Jsonb.From(r.Models), region = r.Region, level = r.MaxClassification, me = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiEndpointRegistered, "ai_private_endpoint", id.ToString(),
            Changes: new { change = "created", r.Name, r.BaseUrl, models = r.Models!.Select(m => m.Name), r.Region, r.MaxClassification }), ct);
        return View(await LoadAsync(id, ct));
    }

    /// <summary>Changes an endpoint. Any change disables it and clears its test, so it must pass a new test before it serves calls again.</summary>
    public async Task<PrivateEndpointView> UpdateAsync(Guid id, PrivateEndpointRequest request, CancellationToken ct)
    {
        var current = await LoadAsync(id, ct, forUpdate: true);
        await DemandManageAsync(ct);
        if (request.Version != current.Version)
        {
            throw AppException.Conflict("conflict.ai.endpoint_changed");
        }
        var r = Validate(request);
        await session.ExecAsync(
            """
            update ai.private_endpoint set name = @name, base_url = @url, secret_ref = @secret, models = @models, region = @region, max_classification = @level,
              status = 'disabled', last_test_at = null, last_test_passed = null, last_test = null, version = version + 1, updated_at = @now
            where id = @id
            """, new { id, name = r.Name, url = r.BaseUrl, secret = r.SecretRef, models = Jsonb.From(r.Models), region = r.Region, level = r.MaxClassification, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiEndpointRegistered, "ai_private_endpoint", id.ToString(),
            Changes: new { change = "updated", r.Name, r.BaseUrl, models = r.Models!.Select(m => m.Name), r.Region, r.MaxClassification }), ct);
        Changed();
        return View(await LoadAsync(id, ct));
    }

    /// <summary>The connection and capability test (US6 scenario 1): the key is found, the server answers, and each model does what it claims.</summary>
    public async Task<PrivateEndpointView> TestAsync(Guid id, CancellationToken ct)
    {
        var current = await LoadAsync(id, ct, forUpdate: true);
        await DemandManageAsync(ct);
        List<EndpointCheck> checks;
        var key = await secrets.GetTenantSecretAsync(context.RequireTenant(), current.SecretRef, ct);
        if (key is null)
        {
            checks = [new EndpointCheck("secret", null, false, "ai.endpoints.checks.secret_missing")];
        }
        else if (probes.FirstOrDefault() is not { } probe)
        {
            checks = [new EndpointCheck("connection", null, false, "ai.endpoints.checks.no_adapter")];
        }
        else
        {
            checks = [new EndpointCheck("secret", null, true, null), .. await probe.TestAsync(current.BaseUrl, key, current.Models, ct)];
        }
        var passed = checks.All(c => c.Passed);
        await session.ExecAsync(
            "update ai.private_endpoint set last_test_at = @now, last_test_passed = @passed, last_test = @checks, status = case when @passed then status else 'disabled' end where id = @id",
            new { id, now = clock.UtcNow, passed, checks = Jsonb.From(checks) }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiEndpointTested, "ai_private_endpoint", id.ToString(),
            Changes: new { passed, failed = checks.Where(c => !c.Passed).Select(c => new { c.Check, c.Model }) }), ct);
        if (!passed && current.Status == "enabled")
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AiEndpointDisabled, "ai_private_endpoint", id.ToString(), Changes: new { reason = "test_failed" }), ct);
            Changed();
        }
        return View(await LoadAsync(id, ct));
    }

    public async Task<PrivateEndpointView> EnableAsync(Guid id, CancellationToken ct)
    {
        var current = await LoadAsync(id, ct, forUpdate: true);
        await DemandManageAsync(ct);
        if (!Current(current))
        {
            throw AppException.Conflict("conflict.ai.endpoint_untested");
        }
        await session.ExecAsync("update ai.private_endpoint set status = 'enabled', updated_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiEndpointEnabled, "ai_private_endpoint", id.ToString(), Changes: new { current.Name }), ct);
        Changed();
        return View(await LoadAsync(id, ct));
    }

    public async Task<PrivateEndpointView> DisableAsync(Guid id, CancellationToken ct)
    {
        var current = await LoadAsync(id, ct, forUpdate: true);
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        await session.ExecAsync("update ai.private_endpoint set status = 'disabled', updated_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiEndpointDisabled, "ai_private_endpoint", id.ToString(), Changes: new { current.Name }), ct);
        Changed();
        return View(await LoadAsync(id, ct));
    }

    /// <summary>Removes a disabled endpoint. Its models leave the catalogue; the tenant policy should drop them first.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var current = await LoadAsync(id, ct, forUpdate: true);
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        if (current.Status == "enabled")
        {
            throw AppException.Conflict("conflict.ai.endpoint_enabled");
        }
        await session.ExecAsync("delete from ai.private_endpoint where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiEndpointDisabled, "ai_private_endpoint", id.ToString(), Changes: new { change = "deleted", current.Name }), ct);
        Changed();
    }

    private void Changed()
    {
        var tenant = context.RequireTenant();
        session.OnCommitted(() => ModelCatalogue.InvalidateTenant(tenant));
    }

    /// <summary>
    /// Where to send a call to a private model, with the key read from the secret manager now. Null when the endpoint
    /// is gone, disabled or its key is missing, which the gateway treats as that model failing.
    /// </summary>
    internal async Task<ProviderTarget?> TargetAsync(string model, CancellationToken ct)
    {
        if (Parse(model) is not { } parsed)
        {
            return null;
        }
        var rows = await session.QueryAsync("select base_url, secret_ref from ai.private_endpoint where id = @id and status = 'enabled'", new { id = parsed.Endpoint },
            r => (Url: r.GetString(0), Secret: r.GetString(1)), ct);
        if (rows.Count == 0 || await secrets.GetTenantSecretAsync(context.RequireTenant(), rows[0].Secret, ct) is not { } key)
        {
            return null;
        }
        return new ProviderTarget(rows[0].Url, key, parsed.Model);
    }

    /// <summary>Whether every classification kept on private endpoints has an enabled endpoint, tested in the last 24 hours, that may take it (section 6).</summary>
    internal async Task<bool> CoversAsync(IEnumerable<string> classifications, CancellationToken ct)
    {
        var rows = await session.QueryAsync($"select {Columns} from ai.private_endpoint where status = 'enabled'", null, Read, ct);
        return classifications.All(level => rows.Any(r => Current(r) && Array.IndexOf(Levels, r.MaxClassification) >= Array.IndexOf(Levels, level)));
    }

    /// <summary>The catalogue entries of the tenant's enabled endpoints.</summary>
    internal static async Task<IReadOnlyList<CatalogueEntry>> EntriesAsync(IDbSession db, CancellationToken ct)
    {
        var rows = await db.QueryAsync("select id, name, models::text, region, max_classification from ai.private_endpoint where status = 'enabled'", null,
            r => (Id: r.GetGuid(0), Name: r.GetString(1), Models: JsonSerializer.Deserialize<List<PrivateModel>>(r.GetString(2), JsonDefaults.Options) ?? [],
                Region: r.GetString(3), Level: r.GetString(4)), ct);
        return [.. rows.SelectMany(e => e.Models.Select(m => new CatalogueEntry(ModelName(e.Id, m.Name), ProviderKey,
            new Dictionary<string, string> { ["default"] = $"{e.Name}: {m.Name}" }, m.Capabilities, m.ContextTokens, [e.Region], ZeroRetention: true, NoTraining: true,
            e.Level, 0, 0, "active", null)))];
    }
}
