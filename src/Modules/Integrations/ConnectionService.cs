using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Microsoft365;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations;

public sealed record PermissionView(string Name, string Type, string ReasonKey);

public sealed record FeatureView(
    string Key,
    bool Enabled,
    bool InPackage,
    string ConsentStatus,
    IReadOnlyList<PermissionView> Permissions,
    IReadOnlyList<string> GrantedPermissions,
    JsonElement? LastTest,
    object? Options = null);

public sealed record ConnectionView(
    Guid Id,
    string Kind,
    string ExternalTenantId,
    string DisplayName,
    string Status,
    bool Enabled,
    Guid? OwnerUserId,
    DateTimeOffset? LastSuccessAt,
    IReadOnlyList<FeatureView> Features);

/// <summary>Admin options of a feature (spec 018): Mail <c>previewLine</c>, <c>maxMessages</c>; Calendar <c>maxDays</c>.</summary>
public sealed record FeatureOptionsRequest(bool? PreviewLine, int? MaxMessages, int? MaxDays);

public sealed record CreateConnectionRequest(string? Kind, string? ExternalTenantId, string? DisplayName, IReadOnlyList<string>? Features);

public sealed record FeatureTestResult(string Feature, bool Ok, string ResultKey, long? Users, long? Groups, IReadOnlyList<string> Missing, IReadOnlyList<string> Broader);

public sealed record ConnectionTestResult(string Status, IReadOnlyList<FeatureTestResult> Features);

public sealed record DeletePreview(IReadOnlyList<string> EnabledFeatures, IReadOnlyList<ConnectionDependent> Dependents);

/// <summary>
/// The Connections area for Microsoft 365 (spec 006 User Story 5). Access (<c>integration.manage</c>) is checked by the
/// endpoints; features are gated by their own capability keys. TeamBeats uses its own multi-tenant application with
/// admin consent, so no customer secret is stored.
/// </summary>
public sealed class ConnectionService(
    IDbSession session,
    TenantContextAccessor context,
    IEntitlementService entitlements,
    IMicrosoftGraph graph,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    IEnumerable<IConnectionDependents> dependents,
    DelegatedTokenStore delegatedTokens)
{
    public static readonly TimeSpan ConsentLifetime = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<FeatureView>> CatalogueAsync(CancellationToken ct)
    {
        var list = new List<FeatureView>();
        foreach (var f in FeatureCatalogue.All)
        {
            list.Add(new FeatureView(f.Key, false, await entitlements.IsEnabledAsync(f.CapabilityKey, ct), "pending", Permissions(f), [], null,
                FeatureOptions.Default.ToStored(f.Key)));
        }
        return list;
    }

    public async Task<IReadOnlyList<ConnectionView>> ListAsync(CancellationToken ct)
    {
        // Connections of other packs get their own views with the connection lifecycle of spec 017 (T007).
        var ids = await session.QueryAsync("select id from integrations.connection where kind = @kind order by created_at",
            new { kind = ConnectionKinds.Microsoft365 }, r => r.GetGuid(0), ct);
        var result = new List<ConnectionView>();
        foreach (var id in ids)
        {
            result.Add((await GetAsync(id, ct))!);
        }
        return result;
    }

    public async Task<ConnectionView?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select id, kind, external_tenant_id, display_name, status, enabled, owner_user_id, last_success_at
            from integrations.connection where id = @id and kind = @kind
            """, new { id, kind = ConnectionKinds.Microsoft365 },
            r => (Id: r.GetGuid(0), Kind: r.GetString(1), External: r.GetString(2), Name: r.GetString(3), Status: r.GetString(4),
                Enabled: r.GetBoolean(5), Owner: r.Get<Guid?>("owner_user_id"), LastSuccess: r.Get<DateTimeOffset?>("last_success_at")), ct);
        if (row == default)
        {
            return null;
        }
        var stored = (await session.QueryAsync(
            "select feature, enabled, consent_status, granted_permissions, last_test::text, options::text from integrations.connection_feature where connection_id = @id",
            new { id }, r => (Feature: r.GetString(0), Enabled: r.GetBoolean(1), Consent: r.GetString(2), Granted: r.GetFieldValue<string[]>(3),
                LastTest: r.Get<string>("last_test"), Options: FeatureOptions.FromStored(r.GetString(5))), ct)).ToDictionary(f => f.Feature);
        var features = new List<FeatureView>();
        foreach (var f in FeatureCatalogue.All)
        {
            var inPackage = await entitlements.IsEnabledAsync(f.CapabilityKey, ct);
            features.Add(stored.TryGetValue(f.Key, out var s)
                ? new FeatureView(f.Key, s.Enabled, inPackage, s.Consent, Permissions(f, s.Options), s.Granted,
                    s.LastTest is null ? null : JsonSerializer.Deserialize<JsonElement>(s.LastTest), s.Options.ToStored(f.Key))
                : new FeatureView(f.Key, false, inPackage, "pending", Permissions(f), [], null, FeatureOptions.Default.ToStored(f.Key)));
        }
        return new ConnectionView(row.Id, row.Kind, row.External, row.Name, row.Status, row.Enabled, row.Owner, row.LastSuccess, features);
    }

    public async Task<ConnectionView> CreateAsync(CreateConnectionRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (request.Kind != ConnectionKinds.Microsoft365)
        {
            errors.Add("kind", "validation.integrations.kind_unsupported");
        }
        if (!Guid.TryParse(request.ExternalTenantId, out var external))
        {
            errors.Add("externalTenantId", "validation.integrations.external_tenant_invalid");
        }
        var name = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            errors.Add("displayName", "validation.integrations.display_name_invalid");
        }
        var features = (request.Features ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (features.Any(f => FeatureCatalogue.Find(f) is null))
        {
            errors.Add("features", "validation.integrations.feature_unknown");
        }
        errors.ThrowIfAny();
        foreach (var f in features)
        {
            if (!await entitlements.IsEnabledAsync(FeatureCatalogue.Find(f)!.CapabilityKey, ct))
            {
                throw AppException.Validation("features", "validation.integrations.feature_not_in_package");
            }
        }

        var externalId = external.ToString("D");
        if (await session.ScalarAsync<bool>("select exists (select 1 from integrations.connection where kind = @k and external_tenant_id = @e)",
                new { k = ConnectionKinds.Microsoft365, e = externalId }, ct))
        {
            throw AppException.Conflict("conflict.integrations.connection_exists");
        }

        var tenantId = context.RequireTenant();
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into integrations.connection (tenant_id, id, kind, external_tenant_id, display_name, status, owner_user_id)
            values (@t, @id, @k, @e, @n, @s, @owner)
            """, new { t = tenantId, id, k = ConnectionKinds.Microsoft365, e = externalId, n = name, s = ConnectionStatuses.NeedsConsent, owner = context.ActorId }, ct);
        foreach (var f in features)
        {
            await session.ExecAsync(
                "insert into integrations.connection_feature (tenant_id, connection_id, feature, enabled) values (@t, @c, @f, true)",
                new { t = tenantId, c = id, f }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionCreated, "connection", id.ToString(),
            Changes: new { kind = ConnectionKinds.Microsoft365, externalTenant = externalId, features }), ct);
        await outbox.EnqueueAsync(new ConnectionCreated(id, ConnectionKinds.Microsoft365), ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task SetFeatureAsync(Guid id, string feature, bool enabled, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var definition = FeatureCatalogue.Find(feature) ?? throw AppException.NotFound();
        if (enabled && !await entitlements.IsEnabledAsync(definition.CapabilityKey, ct))
        {
            throw AppException.Validation("feature", "validation.integrations.feature_not_in_package");
        }
        var current = connection.Features.First(f => f.Key == feature);
        if (current.Enabled == enabled)
        {
            return;
        }
        await session.ExecAsync(
            """
            insert into integrations.connection_feature (tenant_id, connection_id, feature, enabled) values (@t, @c, @f, @e)
            on conflict (tenant_id, connection_id, feature) do update set enabled = @e, updated_at = now()
            """, new { t = context.RequireTenant(), c = id, f = feature, e = enabled }, ct);
        await audit.WriteAsync(new AuditEntry(enabled ? AuditActions.ConnectionFeatureEnabled : AuditActions.ConnectionFeatureDisabled,
            "connection", id.ToString(), Changes: new { feature }), ct);
        await outbox.EnqueueAsync(new ConnectionFeatureChanged(id, feature, enabled), ct);
        await RecomputeStatusAsync(id, ct);
        if (!enabled)
        {
            await delegatedTokens.PruneAsync(id, "feature_off", ct);
        }
    }

    /// <summary>
    /// Sets a feature's options (spec 018 plan "Data model"). Turning the Mail preview line on needs <c>Mail.Read</c>, so
    /// the feature goes back to pending consent; turning it off drops tokens that still hold <c>Mail.Read</c>.
    /// </summary>
    public async Task<ConnectionView> SetOptionsAsync(Guid id, string feature, FeatureOptionsRequest request, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var definition = FeatureCatalogue.Find(feature) ?? throw AppException.NotFound();
        if (!await entitlements.IsEnabledAsync(definition.CapabilityKey, ct))
        {
            throw AppException.Validation("feature", "validation.integrations.feature_not_in_package");
        }
        var before = await OptionsAsync(id, feature, ct);
        var errors = new ValidationErrors();
        var isMail = feature == Microsoft365Features.Mail;
        var isCalendar = feature == Microsoft365Features.Calendar;
        if ((!isMail && (request.PreviewLine is not null || request.MaxMessages is not null)) || (!isCalendar && request.MaxDays is not null))
        {
            errors.Add("options", "validation.integrations.option_not_for_feature");
        }
        if (request.MaxMessages is { } m && m is < 1 or > 10)
        {
            errors.Add("maxMessages", "validation.integrations.max_messages_range");
        }
        if (request.MaxDays is { } d && d is < 1 or > 14)
        {
            errors.Add("maxDays", "validation.integrations.max_days_range");
        }
        errors.ThrowIfAny();

        var after = new FeatureOptions(request.PreviewLine ?? before.PreviewLine, request.MaxMessages ?? before.MaxMessages, request.MaxDays ?? before.MaxDays);
        if (after == before)
        {
            return connection;
        }
        var widened = FeatureCatalogue.PermissionsFor(definition, after).Select(p => p.Name)
            .Except(FeatureCatalogue.PermissionsFor(definition, before).Select(p => p.Name), StringComparer.Ordinal).Any();
        await session.ExecAsync(
            """
            insert into integrations.connection_feature (tenant_id, connection_id, feature, enabled, options) values (@t, @c, @f, false, @o)
            on conflict (tenant_id, connection_id, feature) do update set options = @o,
              consent_status = case when @widened then 'pending' else integrations.connection_feature.consent_status end, updated_at = now()
            """, new { t = context.RequireTenant(), c = id, f = feature, o = Jsonb.From(after.ToStored(feature)), widened }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionFeatureOptionsChanged, "connection", id.ToString(),
            Changes: new { feature, from = before.ToStored(feature), to = after.ToStored(feature) }), ct);
        await RecomputeStatusAsync(id, ct);
        await delegatedTokens.PruneAsync(id, "feature_options", ct);
        return (await GetAsync(id, ct))!;
    }

    private async Task<FeatureOptions> OptionsAsync(Guid id, string feature, CancellationToken ct) =>
        FeatureOptions.FromStored(await session.QuerySingleOrDefaultAsync(
            "select options::text from integrations.connection_feature where connection_id = @c and feature = @f",
            new { c = id, f = feature }, r => r.GetString(0), ct));

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        _ = await GetAsync(id, ct) ?? throw AppException.NotFound();
        await session.ExecAsync("update integrations.connection set enabled = @e, updated_at = now() where id = @id", new { id, e = enabled }, ct);
        await RecomputeStatusAsync(id, ct);
        if (!enabled)
        {
            await delegatedTokens.PruneAsync(id, "connection_disabled", ct);
        }
    }

    public async Task<DeletePreview> PreviewDeleteAsync(Guid id, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var found = new List<ConnectionDependent>();
        foreach (var d in dependents)
        {
            found.AddRange(await d.DescribeAsync(id, ct));
        }
        return new DeletePreview(connection.Features.Where(f => f.Enabled).Select(f => f.Key).ToList(), found);
    }

    public async Task DeleteAsync(Guid id, bool confirmed, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct) ?? throw AppException.NotFound();
        if (!confirmed)
        {
            throw AppException.Validation("confirm", "validation.integrations.delete_needs_confirmation");
        }
        await session.ExecAsync("delete from integrations.connection where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionDeleted, "connection", id.ToString(),
            Changes: new { connection.Kind, externalTenant = connection.ExternalTenantId }), ct);
        await outbox.EnqueueAsync(new ConnectionDeleted(id, connection.Kind), ct);
    }

    /// <summary>Starts admin consent for one feature. Returns Microsoft's URL and the browser binding for the cookie.</summary>
    public async Task<(string Url, string Binding)> StartConsentAsync(Guid id, string feature, string tenantKey, string redirectUri, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var definition = FeatureCatalogue.Find(feature) ?? throw AppException.NotFound();
        if (!connection.Features.First(f => f.Key == feature).Enabled)
        {
            throw AppException.Validation("feature", "validation.integrations.feature_not_enabled");
        }
        // The tenant key travels in the state so the shared platform callback can route the browser back (it is not secret).
        var state = tenantKey + "." + Random();
        var binding = Random();
        var now = clock.UtcNow;
        await session.ExecAsync("delete from integrations.consent_request where expires_at < @cutoff", new { cutoff = now - TimeSpan.FromHours(1) }, ct);
        await session.ExecAsync(
            """
            insert into integrations.consent_request (tenant_id, id, connection_id, feature, state_hash, browser_binding_hash, expires_at)
            values (@t, @id, @c, @f, @s, @b, @exp)
            """, new { t = context.RequireTenant(), id = Ids.New(), c = id, f = feature, s = Hash(state), b = Hash(binding), exp = now + ConsentLifetime }, ct);
        var options = await OptionsAsync(id, feature, ct);
        var scopes = definition.UsesApplicationPermissions
            ? ["https://graph.microsoft.com/.default"]
            : FeatureCatalogue.PermissionsFor(definition, options).Select(p => "https://graph.microsoft.com/" + p.Name).ToList();
        return (graph.AdminConsentUrl(connection.ExternalTenantId, scopes, redirectUri, state), binding);
    }

    /// <summary>Completes admin consent on the tenant host. Returns the connection ID and whether consent was granted.</summary>
    public async Task<(Guid? ConnectionId, bool Granted)> CompleteConsentAsync(string? state, string? binding, string? adminConsent, string? externalTenant,
        string? error, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state))
        {
            return (null, false);
        }
        var pending = await session.QuerySingleOrDefaultAsync(
            """
            update integrations.consent_request set consumed_at = now()
            where state_hash = @h and consumed_at is null and expires_at > @now
            returning connection_id, feature, browser_binding_hash
            """, new { h = Hash(state), now = clock.UtcNow }, r => (Connection: r.GetGuid(0), Feature: r.GetString(1), Binding: r.GetString(2)), ct);
        if (pending == default || string.IsNullOrEmpty(binding) || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Hash(binding)), Encoding.ASCII.GetBytes(pending.Binding)))
        {
            return (null, false);
        }
        var connection = await GetAsync(pending.Connection, ct);
        if (connection is null)
        {
            return (null, false);
        }
        var granted = string.IsNullOrEmpty(error) && string.Equals(adminConsent, "true", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(externalTenant, out var tid) && tid.ToString("D") == connection.ExternalTenantId;
        if (!granted)
        {
            return (connection.Id, false);
        }
        var options = await OptionsAsync(connection.Id, pending.Feature, ct);
        var permissions = FeatureCatalogue.PermissionsFor(FeatureCatalogue.Find(pending.Feature)!, options).Select(p => p.Name).ToArray();
        await session.ExecAsync(
            """
            update integrations.connection_feature set consent_status = 'granted', granted_permissions = @p, consent_granted_at = now(), updated_at = now()
            where connection_id = @c and feature = @f
            """, new { c = connection.Id, f = pending.Feature, p = permissions }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionConsentGranted, "connection", connection.Id.ToString(),
            Changes: new { feature = pending.Feature, permissions }), ct);
        await RecomputeStatusAsync(connection.Id, ct);
        return (connection.Id, true);
    }

    /// <summary>
    /// Checks each turned-on feature separately (spec 006 FR-013): reachable, consented, counts found, and permissions
    /// broader than needed. Nothing from the directory is stored, only counts.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(Guid id, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var results = new List<FeatureTestResult>();
        AppToken? token = null;
        string? tokenProblem = null;
        try
        {
            token = await graph.AcquireAppTokenAsync(connection.ExternalTenantId, ct);
        }
        catch (GraphConsentMissingException)
        {
            tokenProblem = "integrations.test.consent_missing";
        }
        catch (GraphUnavailableException)
        {
            tokenProblem = "integrations.test.unreachable";
        }

        var broader = token?.Roles.Where(FeatureCatalogue.IsBroaderThanNeeded).Order(StringComparer.Ordinal).ToList() ?? [];
        foreach (var feature in connection.Features.Where(f => f.Enabled))
        {
            var definition = FeatureCatalogue.Find(feature.Key)!;
            FeatureTestResult result;
            if (definition.UsesApplicationPermissions)
            {
                result = await TestApplicationFeatureAsync(definition, token, tokenProblem, broader, ct);
            }
            else
            {
                // Delegated features run as each employee; the test checks consent and that no application mail or
                // calendar permissions are held (spec 018 FR-003).
                var appMailOrCalendar = broader.Where(r => r.StartsWith("Mail.", StringComparison.Ordinal) || r.StartsWith("Calendars.", StringComparison.Ordinal)).ToList();
                result = feature.ConsentStatus != "granted"
                    ? new FeatureTestResult(feature.Key, false, "integrations.test.consent_missing", null, null, definition.Permissions.Select(p => p.Name).ToList(), broader)
                    : appMailOrCalendar.Count > 0
                        ? new FeatureTestResult(feature.Key, false, "integrations.test.application_mail_permissions", null, null, [], appMailOrCalendar)
                        : new FeatureTestResult(feature.Key, true, "integrations.test.ok", null, null, [], broader);
            }
            results.Add(result);
            await session.ExecAsync("update integrations.connection_feature set last_test = @r::jsonb where connection_id = @c and feature = @f",
                new { c = id, f = feature.Key, r = JsonSerializer.Serialize(result, JsonDefaults.Options) }, ct);
        }

        var status = !connection.Enabled ? ConnectionStatuses.Disabled
            : results.Any(r => r.ResultKey == "integrations.test.consent_missing") ? ConnectionStatuses.NeedsConsent
            : results.Any(r => !r.Ok) ? ConnectionStatuses.Degraded
            : ConnectionStatuses.Healthy;
        await session.ExecAsync(
            "update integrations.connection set last_checked_at = now(), last_success_at = case when @ok then now() else last_success_at end where id = @id",
            new { id, ok = status == ConnectionStatuses.Healthy }, ct);
        if (tokenProblem == "integrations.test.consent_missing" && connection.Features.Any(f => f.Enabled && f.ConsentStatus == "granted"))
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionConsentRevokedDetected, "connection", id.ToString()), ct);
        }
        await SetStatusAsync(id, connection.Status, status, ct);
        return new ConnectionTestResult(status, results);
    }

    private async Task<FeatureTestResult> TestApplicationFeatureAsync(FeatureDefinition definition, AppToken? token, string? problem,
        IReadOnlyList<string> broader, CancellationToken ct)
    {
        if (token is null)
        {
            return new FeatureTestResult(definition.Key, false, problem!, null, null, definition.Permissions.Select(p => p.Name).ToList(), []);
        }
        var missing = definition.Permissions.Where(p => !token.Roles.Contains(p.Name)).Select(p => p.Name).ToList();
        if (missing.Count > 0)
        {
            return new FeatureTestResult(definition.Key, false, "integrations.test.consent_missing", null, null, missing, broader);
        }
        try
        {
            var users = await graph.CountAsync(token, "users", ct);
            var groups = await graph.CountAsync(token, "groups", ct);
            return new FeatureTestResult(definition.Key, true, broader.Count > 0 ? "integrations.test.ok_broader" : "integrations.test.ok", users, groups, [], broader);
        }
        catch (GraphConsentMissingException)
        {
            return new FeatureTestResult(definition.Key, false, "integrations.test.consent_missing", null, null, [], broader);
        }
        catch (GraphUnavailableException)
        {
            return new FeatureTestResult(definition.Key, false, "integrations.test.unreachable", null, null, [], broader);
        }
    }

    private async Task RecomputeStatusAsync(Guid id, CancellationToken ct)
    {
        var connection = (await GetAsync(id, ct))!;
        var status = !connection.Enabled ? ConnectionStatuses.Disabled
            : connection.Features.Any(f => f.Enabled && f.ConsentStatus != "granted") ? ConnectionStatuses.NeedsConsent
            : connection.Status is ConnectionStatuses.Disabled or ConnectionStatuses.NeedsConsent ? ConnectionStatuses.Healthy
            : connection.Status;
        await SetStatusAsync(id, connection.Status, status, ct);
    }

    private async Task SetStatusAsync(Guid id, string from, string to, CancellationToken ct)
    {
        if (from == to)
        {
            return;
        }
        await session.ExecAsync("update integrations.connection set status = @s, updated_at = now() where id = @id", new { id, s = to }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ConnectionStatusChanged, "connection", id.ToString(), Changes: new { from, to }), ct);
        if (to == ConnectionStatuses.Healthy)
        {
            await outbox.EnqueueAsync(new ConnectorRestored(id), ct);
        }
        else if (from == ConnectionStatuses.Healthy)
        {
            await outbox.EnqueueAsync(new ConnectorUnavailable(id, to), ct);
        }
    }

    private static IReadOnlyList<PermissionView> Permissions(FeatureDefinition f, FeatureOptions? options = null) =>
        FeatureCatalogue.PermissionsFor(f, options ?? FeatureOptions.Default).Select(p => new PermissionView(p.Name, p.Type == PermissionType.Application ? "application" : "delegated", p.ReasonKey)).ToList();

    private static string Random() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Read side for feature owners (<see cref="IConnectionFeatures"/>).</summary>
public sealed class ConnectionFeatures(IDbSession session) : IConnectionFeatures
{
    public Task<ConnectionFeatureInfo?> FindAsync(string kind, string feature, CancellationToken ct = default) =>
        session.QuerySingleOrDefaultAsync(
            """
            select c.id, c.kind, c.external_tenant_id, f.feature, c.enabled and f.consent_status = 'granted' and c.status <> 'disabled', c.status
            from integrations.connection c join integrations.connection_feature f on f.connection_id = c.id
            where c.kind = @k and f.feature = @f and f.enabled
            order by c.created_at limit 1
            """, new { k = kind, f = feature },
            r => new ConnectionFeatureInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetString(5)), ct);
}

/// <summary>Enabled connections that are not disabled, by name, for module checklists (spec 022 FR-017).</summary>
public sealed class ConnectedSystems(IDbSession session) : IConnectedSystems
{
    public async Task<IReadOnlyList<ConnectedSystem>> ListAsync(CancellationToken ct = default) =>
        await session.QueryAsync(
            "select id, kind, display_name from integrations.connection where enabled and status <> 'disabled' order by lower(display_name), id limit 100",
            null, r => new ConnectedSystem(r.GetGuid(0), r.GetString(1), r.GetString(2)), ct);
}
