using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// The tenant's effort estimates (spec 021 US2 scenario 2): authoring hours per item, the cost of an hour, and the cost
/// of one send per channel, in one currency. Used only to work out return on effort; never a figure about a person.
/// </summary>
public sealed record EffortEstimates(decimal AuthoringHoursPerItem, decimal CostPerHour, string Currency, IReadOnlyDictionary<string, decimal> CostPerSend);

public sealed record EffortEstimatesView(EffortEstimates? Estimates, int Version, DateTimeOffset? ChangedAt, bool CanChange);

public sealed record EffortEstimatesRequest(decimal? AuthoringHoursPerItem, decimal? CostPerHour, string? Currency, IReadOnlyDictionary<string, decimal>? CostPerSend,
    int? Version);

/// <summary>Versioned, schema-validated configuration (constitution XXIX): each change is a new version, audited.</summary>
public sealed partial class AnalyticsEstimates(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, IClock clock)
{
    public const string AuditTarget = "analytics_estimates";
    public const decimal MaxHours = 1000;
    public const decimal MaxCost = 1_000_000;
    public const decimal MaxSendCost = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    internal async Task<(EffortEstimates? Estimates, int Version, DateTimeOffset? ChangedAt)> CurrentAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync("select version, document::text, changed_at from analytics.effort_estimate order by version desc limit 1", null,
            r => (Version: r.GetInt32(0), Document: r.GetString(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        return rows.Count == 0 ? (null, 0, null) : (JsonSerializer.Deserialize<EffortEstimates>(rows[0].Document, Json), rows[0].Version, rows[0].At);
    }

    private async Task<bool> MayChangeAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct)).Allowed;

    public async Task<EffortEstimatesView> ViewAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsAdvanced, ct);
        context.RequireUser();
        var change = await MayChangeAsync(ct);
        if (!change && !(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var (estimates, version, at) = await CurrentAsync(ct);
        return new EffortEstimatesView(estimates, version, at, change);
    }

    public async Task<EffortEstimatesView> ChangeAsync(EffortEstimatesRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsAdvanced, ct);
        var user = context.RequireUser();
        if (!await MayChangeAsync(ct))
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        if (request.AuthoringHoursPerItem is not { } hours || hours < 0 || hours > MaxHours)
        {
            errors.Add("authoringHoursPerItem", "validation.analytics.estimate");
        }
        if (request.CostPerHour is not { } rate || rate < 0 || rate > MaxCost)
        {
            errors.Add("costPerHour", "validation.analytics.estimate");
        }
        if (request.Currency is not { } currency || !CurrencyPattern().IsMatch(currency))
        {
            errors.Add("currency", "validation.analytics.currency");
        }
        var sends = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (channel, cost) in request.CostPerSend ?? new Dictionary<string, decimal>())
        {
            if (!NotificationChannels.All.Contains(channel) || cost < 0 || cost > MaxSendCost)
            {
                errors.Add($"costPerSend.{channel}", "validation.analytics.estimate");
            }
            else
            {
                sends[channel] = cost;
            }
        }
        if (request.Version is null)
        {
            errors.Add("version", "validation.analytics.version");
        }
        errors.ThrowIfAny();
        var (before, version, _) = await CurrentAsync(ct);
        if (request.Version != version)
        {
            throw AppException.Conflict("conflict.analytics.estimates_version");
        }
        var after = new EffortEstimates(request.AuthoringHoursPerItem!.Value, request.CostPerHour!.Value, request.Currency!, sends);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into analytics.effort_estimate (tenant_id, version, schema_version, document, changed_at, changed_by)
            values (platform.current_tenant_id(), @version, 1, @document::jsonb, @now, @user)
            """, new { version = version + 1, document = JsonSerializer.Serialize(after, Json), now, user }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AnalyticsEstimatesChanged, AuditTarget, null,
            Changes: new Dictionary<string, object> { ["estimates"] = new { before, after }, ["version"] = new { before = version, after = version + 1 } }), ct);
        return new EffortEstimatesView(after, version + 1, now, true);
    }
}
