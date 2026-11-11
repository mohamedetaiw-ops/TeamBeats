using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>An action as people see it. <paramref name="Inputs"/> is null for an action that declares none, which can be neither a route step nor an agent tool.</summary>
public sealed record PackActionView(string Name, string SideEffect, bool Idempotent, IReadOnlyList<string> BusinessErrors, IReadOnlyList<ConnectorInput>? Inputs = null);

/// <summary>A pack the tenant can add (spec 017 US1 scenario 1): its actions are listed before any credential is entered.</summary>
public sealed record PackView(string Kind, int Version, bool InPackage, IReadOnlyList<PackActionView> Actions);

public sealed record PackConnectionView(
    Guid Id,
    string Kind,
    string DisplayName,
    string Status,
    bool Enabled,
    Guid? OwnerUserId,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastCheckedAt,
    DateOnly? CredentialExpiresOn,
    bool CircuitOpen,
    int RateLimitPerMinute,
    JsonElement Settings,
    JsonElement? LastTest,
    IReadOnlyList<PackActionView> Actions);

public sealed record CreatePackConnectionRequest(string? Kind, string? DisplayName, JsonElement? Settings, int? RateLimitPerMinute);

public sealed record UpdatePackConnectionRequest(string? DisplayName, JsonElement? Settings, int? RateLimitPerMinute);

public sealed record PackTestResult(string Status, IReadOnlyList<PackCheck> Checks);

public sealed record RestActionRequest(RestActionDefinition? Definition);

public sealed record RestActionView(string Name, RestActionDefinition Definition, DateTimeOffset UpdatedAt);

public sealed record RestActionTestRequest(RestActionDefinition? Definition, JsonElement? Input);

public sealed record RestActionTestResult(string Outcome, int? HttpStatus, JsonElement? Output, string? ExternalReference, string? ErrorClass, string? ErrorCode);

/// <summary>
/// The connection lifecycle for every pack (spec 017 US1, FR-002 to FR-004, FR-023; spec 006 FR-010 to FR-015), and
/// the actions of custom REST connections (US4). A connection starts disabled: the administrator saves settings with
/// secret manager references, tests it, then enables it. Disabling takes effect on the next attempt, because the runtime
/// reads the connection before every call. Settings and credentials need step-up (checked by the endpoints). Access
/// (<c>integration.manage</c>) is checked by the endpoints too.
/// </summary>
public sealed class PackConnections(
    IDbSession session,
    ITenantContext context,
    IEnumerable<IConnectorPack> packs,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    IEnumerable<IConnectionDependents> dependents)
{
    public const int ExpiryWarningDays = 14;

    public async Task<IReadOnlyList<PackView>> CatalogueAsync(CancellationToken ct)
    {
        var connectors = await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct);
        var list = new List<PackView>();
        foreach (var p in packs.OrderBy(p => p.Kind, StringComparer.Ordinal))
        {
            list.Add(new PackView(p.Kind, p.Version, connectors && await entitlements.IsEnabledAsync(p.CapabilityKey, ct), p.Actions.Select(View).ToList()));
        }
        return list;
    }

    public async Task<IReadOnlyList<PackConnectionView>> ListAsync(CancellationToken ct)
    {
        var ids = await session.QueryAsync("select id from integrations.connection where kind <> @m order by created_at, id",
            new { m = ConnectionKinds.Microsoft365 }, r => r.GetGuid(0), ct);
        var result = new List<PackConnectionView>();
        foreach (var id in ids)
        {
            if (await GetAsync(id, ct) is { } view)
            {
                result.Add(view);
            }
        }
        return result;
    }

    public async Task<PackConnectionView?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select id, kind, display_name, status, enabled, owner_user_id, last_success_at, last_checked_at, credential_expires_at,
              circuit_opened_at is not null, rate_limit_per_minute, settings::text, last_test::text
            from integrations.connection where id = @id and kind <> @m
            """, new { id, m = ConnectionKinds.Microsoft365 },
            r => new
            {
                Id = r.GetGuid(0), Kind = r.GetString(1), Name = r.GetString(2), Status = r.GetString(3), Enabled = r.GetBoolean(4),
                Owner = r.Get<Guid?>("owner_user_id"), LastSuccess = r.Get<DateTimeOffset?>("last_success_at"),
                LastChecked = r.Get<DateTimeOffset?>("last_checked_at"), Expires = r.IsDBNull(8) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(8)),
                Circuit = r.GetBoolean(9), Rate = r.GetInt32(10), Settings = r.GetString(11), LastTest = r.IsDBNull(12) ? null : r.GetString(12),
            }, ct);
        if (row is null)
        {
            return null;
        }
        var pack = packs.FirstOrDefault(p => p.Kind == row.Kind);
        var settings = JsonDocument.Parse(row.Settings).RootElement.Clone();
        var actions = pack is null ? [] : (await pack.ActionsForAsync(new PackConnection(context.RequireTenant(), row.Id, row.Kind, 1, null, settings), ct))
            .Select(View).ToList();
        return new PackConnectionView(row.Id, row.Kind, row.Name, row.Status, row.Enabled, row.Owner, row.LastSuccess, row.LastChecked, row.Expires,
            row.Circuit, row.Rate, settings, row.LastTest is null ? null : JsonDocument.Parse(row.LastTest).RootElement.Clone(), actions);
    }

    public async Task<PackConnectionView> CreateAsync(CreatePackConnectionRequest request, CancellationToken ct)
    {
        var pack = packs.FirstOrDefault(p => p.Kind == request.Kind) ?? throw AppException.Validation("kind", "validation.integrations.kind_unsupported");
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            throw AppException.NotInPackage(Capabilities.IntegrationConnectors);
        }
        if (!await entitlements.IsEnabledAsync(pack.CapabilityKey, ct))
        {
            throw AppException.NotInPackage(pack.CapabilityKey);
        }
        var name = ValidName(request.DisplayName);
        var rate = ValidRate(request.RateLimitPerMinute ?? 600);
        var limit = await entitlements.GetLimitAsync(Capabilities.IntegrationMaxConnections, ct);
        var count = await session.ScalarAsync<long>("select count(*) from integrations.connection where kind <> @m", new { m = ConnectionKinds.Microsoft365 }, ct);
        if (limit is { } max && count >= max)
        {
            throw AppException.Conflict("conflict.integrations.connection_limit");
        }
        // One HR system is the source of truth for a tenant (spec 019): a second HR connection would fight the first.
        if (Hr.HrSync.PackKinds.Contains(pack.Kind)
            && await session.ScalarAsync<long>("select count(*) from integrations.connection where kind = any(@kinds)", new { kinds = Hr.HrSync.PackKinds.ToArray() }, ct) > 0)
        {
            throw AppException.Conflict("conflict.hr.one_connection");
        }
        var empty = JsonDocument.Parse("{}").RootElement;
        var settings = pack.CarryOver(empty, await pack.ValidateSettingsAsync(request.Settings ?? empty, ct), clock.UtcNow);
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into integrations.connection (tenant_id, id, kind, pack_version, display_name, status, enabled, owner_user_id, settings,
              rate_limit_per_minute, credential_expires_at)
            values (platform.current_tenant_id(), @id, @kind, @version, @name, 'disabled', false, @owner, @settings, @rate, @expires)
            """, new { id, kind = pack.Kind, version = pack.Version, name, owner = context.ActorId, settings = new Jsonb(settings.GetRawText()), rate,
                expires = pack.CredentialExpiry(settings)?.ToDateTime(TimeOnly.MinValue) }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionCreated, "connection", id.ToString(), Changes: new { kind = pack.Kind, name }), ct);
        if (HasCredential(settings))
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.CredentialChanged, "connection", id.ToString(), Changes: new { credential = CredentialName(settings) }), ct);
        }
        await outbox.EnqueueAsync(new ConnectionCreated(id, pack.Kind), ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<PackConnectionView> UpdateAsync(Guid id, UpdatePackConnectionRequest request, CancellationToken ct)
    {
        var current = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var pack = Pack(current.Kind);
        var name = request.DisplayName is null ? current.DisplayName : ValidName(request.DisplayName);
        var rate = request.RateLimitPerMinute is { } r ? ValidRate(r) : current.RateLimitPerMinute;
        var settings = request.Settings is { } s ? pack.CarryOver(current.Settings, await pack.ValidateSettingsAsync(s, ct), clock.UtcNow) : current.Settings;
        await session.ExecAsync(
            """
            update integrations.connection set display_name = @name, settings = @settings, rate_limit_per_minute = @rate,
              credential_expires_at = @expires, updated_at = @now
            where id = @id
            """, new { id, name, settings = new Jsonb(settings.GetRawText()), rate, expires = pack.CredentialExpiry(settings)?.ToDateTime(TimeOnly.MinValue),
                now = clock.UtcNow }, ct);
        var credentialChanged = Credential(current.Settings) != Credential(settings);
        var otherChanged = name != current.DisplayName || rate != current.RateLimitPerMinute || Without(current.Settings) != Without(settings);
        if (credentialChanged)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.CredentialChanged, "connection", id.ToString(), Changes: new { credential = CredentialName(settings) }), ct);
        }
        if (pack is Webhooks.WebhookPack && Webhooks.WebhookPack.Rotation(current.Settings, settings, clock.UtcNow) is { } rotation)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.WebhookSecretRotated, "connection", id.ToString(),
                Changes: new { overlapHours = rotation.OverlapHours, previousValidUntil = rotation.PreviousValidUntil }), ct);
        }
        if (otherChanged)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionChanged, "connection", id.ToString(),
                Changes: new { name, rateLimitPerMinute = rate, settings = JsonSerializer.Deserialize<object>(Without(settings)) }), ct);
        }
        await RefreshExpiryAsync(id, ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<PackTestResult> TestAsync(Guid id, CancellationToken ct)
    {
        var current = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var pack = Pack(current.Kind);
        var checks = await pack.TestAsync(new PackConnection(context.RequireTenant(), id, current.Kind, 1, null, current.Settings), ct);
        var ok = checks.All(c => c.Ok);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update integrations.connection set last_test = @test, last_checked_at = @now,
              last_success_at = case when @ok then @now else last_success_at end
            where id = @id
            """, new { id, test = Jsonb.From(new { at = now, ok, checks }), now, ok }, ct);
        return new PackTestResult(ok ? "ok" : "failed", checks);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var current = await GetAsync(id, ct) ?? throw AppException.NotFound();
        if (current.Enabled == enabled)
        {
            return;
        }
        await session.ExecAsync(
            "update integrations.connection set enabled = @enabled, status = case when @enabled then 'healthy' else 'disabled' end, updated_at = @now where id = @id",
            new { id, enabled, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(enabled ? AuditActions.ConnectionEnabled : AuditActions.ConnectionDisabled, "connection", id.ToString()), ct);
        if (enabled)
        {
            await outbox.EnqueueAsync(new ConnectorRestored(id), ct);
        }
        else
        {
            await outbox.EnqueueAsync(new ConnectorUnavailable(id, ConnectionStatuses.Disabled), ct);
        }
        if (enabled)
        {
            await RefreshExpiryAsync(id, ct);
        }
    }

    public async Task<DeletePreview> PreviewDeleteAsync(Guid id, CancellationToken ct)
    {
        var current = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var found = new List<ConnectionDependent>();
        foreach (var d in dependents)
        {
            found.AddRange(await d.DescribeAsync(id, ct));
        }
        return new DeletePreview(current.Actions.Select(a => a.Name).ToList(), found);
    }

    public async Task DeleteAsync(Guid id, bool confirm, CancellationToken ct)
    {
        var current = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var preview = await PreviewDeleteAsync(id, ct);
        if (!confirm && (current.Enabled || preview.Dependents.Count > 0))
        {
            throw AppException.Validation("confirm", "validation.integrations.delete_needs_confirmation");
        }
        await session.ExecAsync("delete from integrations.connection where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionDeleted, "connection", id.ToString(), Changes: new { kind = current.Kind, name = current.DisplayName }), ct);
        await outbox.EnqueueAsync(new ConnectionDeleted(id, current.Kind), ct);
    }

    /// <summary>
    /// Credential expiry (spec 017 US1 scenario 5): an enabled healthy connection whose credential expires within 14 days
    /// becomes "credential expiring", which alerts the integration administrators; a renewed one goes back to healthy.
    /// </summary>
    public async Task RefreshExpiryAsync(Guid? only, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var horizon = today.AddDays(ExpiryWarningDays).ToDateTime(TimeOnly.MinValue);
        var expiring = await session.QueryAsync(
            """
            update integrations.connection set status = 'credential_expiring', updated_at = now()
            where enabled and status = 'healthy' and credential_expires_at is not null and credential_expires_at <= @horizon
              and kind <> @m and (@only::uuid is null or id = @only)
            returning id
            """, new { horizon, only, m = ConnectionKinds.Microsoft365 }, r => r.GetGuid(0), ct);
        foreach (var id in expiring)
        {
            await outbox.EnqueueAsync(new ConnectorUnavailable(id, ConnectionStatuses.CredentialExpiring), ct);
        }
        var renewed = await session.QueryAsync(
            """
            update integrations.connection set status = 'healthy', updated_at = now()
            where enabled and status = 'credential_expiring' and (credential_expires_at is null or credential_expires_at > @horizon)
              and kind <> @m and (@only::uuid is null or id = @only)
            returning id
            """, new { horizon, only, m = ConnectionKinds.Microsoft365 }, r => r.GetGuid(0), ct);
        foreach (var id in renewed)
        {
            await outbox.EnqueueAsync(new ConnectorRestored(id), ct);
        }
    }

    // Custom REST actions (US4)

    public async Task<IReadOnlyList<RestActionView>> ActionsAsync(Guid id, CancellationToken ct)
    {
        await RequireRestAsync(id, ct);
        return await session.QueryAsync(
            "select name, definition::text, updated_at from integrations.connector_action where connection_id = @id order by name", new { id },
            r => new RestActionView(r.GetString(0), JsonSerializer.Deserialize<RestActionDefinition>(r.GetString(1), JsonDefaults.Options)!,
                r.GetFieldValue<DateTimeOffset>(2)), ct);
    }

    public async Task<RestActionView> PutActionAsync(Guid id, string name, RestActionRequest request, CancellationToken ct)
    {
        await RequireRestAsync(id, ct);
        if (!RestDefinitions.ActionName().IsMatch(name))
        {
            throw AppException.Validation("name", "validation.custom_rest.action_name");
        }
        var d = RestDefinitions.Validate(request.Definition);
        var existed = await session.ScalarAsync<bool>("select exists (select 1 from integrations.connector_action where connection_id = @id and name = @name)",
            new { id, name }, ct);
        if (!existed && await session.ScalarAsync<long>("select count(*) from integrations.connector_action where connection_id = @id", new { id }, ct) >= 100)
        {
            throw AppException.Conflict("conflict.integrations.action_limit");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into integrations.connector_action (tenant_id, connection_id, name, side_effect, definition, created_by, created_at, updated_by, updated_at)
            values (platform.current_tenant_id(), @id, @name, @side, @definition, @actor, @now, @actor, @now)
            on conflict (tenant_id, connection_id, name) do update set side_effect = @side, definition = @definition, updated_by = @actor, updated_at = @now
            """, new { id, name, side = d.SideEffect, definition = Jsonb.From(d), actor = context.ActorId, now }, ct);
        await audit.WriteAsync(new AuditEntry(existed ? AuditActions.ActionChanged : AuditActions.ActionDefined, "connection", id.ToString(),
            Changes: new { action = name, sideEffect = d.SideEffect, d.Method, d.Path, inputs = d.Inputs!.Select(i => i.Name), output = d.Output!.Keys }), ct);
        return new RestActionView(name, d, now);
    }

    public async Task DeleteActionAsync(Guid id, string name, CancellationToken ct)
    {
        await RequireRestAsync(id, ct);
        if (await session.ExecAsync("delete from integrations.connector_action where connection_id = @id and name = @name", new { id, name }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ActionChanged, "connection", id.ToString(), Changes: new { action = name, removed = true }), ct);
    }

    /// <summary>Runs a definition once with sample input, before it is saved (US4 scenario 1). Not a run: nothing is recorded.</summary>
    public async Task<RestActionTestResult> TestActionAsync(Guid id, RestActionTestRequest request, CancellationToken ct)
    {
        var current = await RequireRestAsync(id, ct);
        var d = RestDefinitions.Validate(request.Definition);
        var input = request.Input ?? JsonDocument.Parse("{}").RootElement;
        if (RestDefinitions.CheckInput(d, input) is { } field)
        {
            throw AppException.Validation("input." + field, "validation.custom_rest.sample_input");
        }
        var pack = (CustomRestPack)Pack(CustomRestPack.KindName);
        RestExecution execution;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RuntimePolicy.InlineBudget);
        try
        {
            execution = await pack.ExecuteAsync(new PackConnection(context.RequireTenant(), id, current.Kind, 1, null, current.Settings), d, input,
                "test-" + Ids.New().ToString("N"), cts.Token);
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedAddressException)
        {
            execution = new(PackCallResult.Fatal(ConnectorErrorClasses.BlockedAddress), null);
        }
        catch (HttpRequestException)
        {
            execution = new(PackCallResult.Technical(ConnectorErrorClasses.NotResponding), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            execution = new(PackCallResult.Technical(ConnectorErrorClasses.NotResponding), null);
        }
        var r = execution.Result;
        var outcome = r.Kind switch
        {
            PackResultKind.Ok => ConnectorOutcomes.Succeeded,
            PackResultKind.BusinessError => ConnectorOutcomes.BusinessError,
            _ => ConnectorOutcomes.Failed,
        };
        return new RestActionTestResult(outcome, execution.Status, r.Output, r.ExternalReference, r.ErrorClass, r.ErrorCode);
    }

    private async Task<PackConnectionView> RequireRestAsync(Guid id, CancellationToken ct)
    {
        var current = await GetAsync(id, ct);
        if (current is null || current.Kind != CustomRestPack.KindName)
        {
            throw AppException.NotFound();
        }
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationCustomRest, ct))
        {
            throw AppException.NotInPackage(Capabilities.IntegrationCustomRest);
        }
        return current;
    }

    private IConnectorPack Pack(string kind) => packs.FirstOrDefault(p => p.Kind == kind) ?? throw AppException.NotFound();

    private static PackActionView View(ConnectorActionDefinition a) => new(a.Name, a.SideEffect, a.Idempotent, a.BusinessErrors, a.Inputs);

    private static string ValidName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n) || n.Length > 100)
        {
            throw AppException.Validation("displayName", "validation.integrations.display_name_invalid");
        }
        return n;
    }

    private static int ValidRate(int rate) =>
        rate is >= 1 and <= 100000 ? rate : throw AppException.Validation("rateLimitPerMinute", "validation.integrations.rate_limit");

    private static bool HasCredential(JsonElement settings) => Credential(settings) is not null;

    /// <summary>The sign-in part of the settings, compared to tell a credential change from other changes. References only, never values.</summary>
    private static string? Credential(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("auth", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : null;

    private static string? CredentialName(JsonElement settings) =>
        settings.TryGetProperty("auth", out var a) && a.TryGetProperty("type", out var t) ? t.GetString() : null;

    private static string Without(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
        {
            return settings.GetRawText();
        }
        var copy = settings.EnumerateObject().Where(p => p.Name != "auth").ToDictionary(p => p.Name, p => p.Value);
        return JsonSerializer.Serialize(copy);
    }
}
