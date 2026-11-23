using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// The tenant analytics policy (spec 021 FR-004, FR-014, FR-018): nonessential events on or off, raw event retention in
/// months (3 to 25, 13 by default), the minimum group size (5 by default, never lower) and individual analytics, which
/// stay off in R1 (open question default). Sentiment (spec 021 US8, FR-013) is off until the tenant turns it on, and can
/// only be turned on with <c>analytics.sentiment</c> in the package.
/// </summary>
public sealed record AnalyticsPolicyView(bool Nonessential, int RawRetentionMonths, int MinGroupSize, bool Individual, int Version,
    DateTimeOffset? UpdatedAt, bool CanChange, bool Sentiment = false, bool SentimentAvailable = false);

public sealed record AnalyticsPolicyRequest(bool? Nonessential, int? RawRetentionMonths, int? MinGroupSize, bool? Individual, int? Version, bool? Sentiment = null);

internal sealed record AnalyticsPolicy(bool Nonessential, int RawRetentionMonths, int MinGroupSize, bool Individual, int Version, DateTimeOffset? UpdatedAt,
    bool Sentiment = false)
{
    public static readonly AnalyticsPolicy Default = new(true, 13, 5, false, 0, null);
}

public sealed class AnalyticsPolicies(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, IClock clock) : IAnalyticsPrivacy
{
    public const string AuditTarget = "analytics_policy";
    public const int MinRetentionMonths = 3;
    public const int MaxRetentionMonths = 25;
    public const int SmallestGroup = 5;

    private AnalyticsPolicy? _cached;

    internal async Task<AnalyticsPolicy> GetAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached;
        }
        var rows = await session.QueryAsync(
            "select nonessential, raw_retention_months, min_group_size, individual, version, updated_at, sentiment from analytics.policy",
            null, r => new AnalyticsPolicy(r.GetBoolean(0), r.GetInt32(1), r.GetInt32(2), r.GetBoolean(3), r.GetInt32(4), r.GetFieldValue<DateTimeOffset>(5),
                r.GetBoolean(6)), ct);
        _cached = rows.Count == 0 ? AnalyticsPolicy.Default : rows[0];
        return _cached;
    }

    public async Task<int> MinGroupSizeAsync(CancellationToken ct = default) => (await GetAsync(ct)).MinGroupSize;

    private async Task<bool> MayReadAsync(CancellationToken ct) =>
        (await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed || await MayChangeAsync(ct);

    private async Task<bool> MayChangeAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct)).Allowed;

    public async Task<AnalyticsPolicyView> ViewAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsCore, ct);
        context.RequireUser();
        if (!await MayReadAsync(ct))
        {
            throw AppException.Denied();
        }
        var p = await GetAsync(ct);
        return new AnalyticsPolicyView(p.Nonessential, p.RawRetentionMonths, p.MinGroupSize, p.Individual, p.Version, p.UpdatedAt, await MayChangeAsync(ct),
            p.Sentiment, await entitlements.IsEnabledAsync(Capabilities.AnalyticsSentiment, ct));
    }

    /// <summary>Changes the policy with its version as an optimistic lock, audited as <c>analytics.privacy_setting_changed</c>.</summary>
    public async Task<AnalyticsPolicyView> ChangeAsync(AnalyticsPolicyRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsCore, ct);
        var user = context.RequireUser();
        if (!await MayChangeAsync(ct))
        {
            throw AppException.Denied();
        }
        var before = await GetAsync(ct);
        var errors = new ValidationErrors();
        if (request.RawRetentionMonths is < MinRetentionMonths or > MaxRetentionMonths)
        {
            errors.Add("rawRetentionMonths", "validation.analytics.retention");
        }
        if (request.MinGroupSize is < SmallestGroup or > 1000)
        {
            errors.Add("minGroupSize", "validation.analytics.min_group");
        }
        if (request.Individual == true)
        {
            errors.Add("individual", "validation.analytics.individual");
        }
        var sentimentAvailable = await entitlements.IsEnabledAsync(Capabilities.AnalyticsSentiment, ct);
        if (request.Sentiment == true && !sentimentAvailable)
        {
            errors.Add("sentiment", "validation.analytics.sentiment_package");
        }
        if (request.Version is null)
        {
            errors.Add("version", "validation.analytics.version");
        }
        errors.ThrowIfAny();
        if (request.Version != before.Version)
        {
            throw AppException.Conflict("conflict.analytics.policy_version");
        }
        var after = new AnalyticsPolicy(request.Nonessential ?? before.Nonessential, request.RawRetentionMonths ?? before.RawRetentionMonths,
            request.MinGroupSize ?? before.MinGroupSize, false, before.Version + 1, clock.UtcNow, request.Sentiment ?? before.Sentiment);
        await session.ExecAsync(
            """
            insert into analytics.policy (tenant_id, nonessential, raw_retention_months, min_group_size, individual, version, updated_at, updated_by, sentiment)
            values (platform.current_tenant_id(), @nonessential, @months, @group, false, @version, @now, @user, @sentiment)
            on conflict (tenant_id) do update set nonessential = excluded.nonessential, raw_retention_months = excluded.raw_retention_months,
              min_group_size = excluded.min_group_size, version = excluded.version, updated_at = excluded.updated_at, updated_by = excluded.updated_by,
              sentiment = excluded.sentiment
            """,
            new { nonessential = after.Nonessential, months = after.RawRetentionMonths, group = after.MinGroupSize, version = after.Version, now = after.UpdatedAt, user,
                sentiment = after.Sentiment }, ct);
        var changes = new Dictionary<string, object>();
        if (after.Nonessential != before.Nonessential)
        {
            changes["nonessential"] = new { before = before.Nonessential, after = after.Nonessential };
        }
        if (after.RawRetentionMonths != before.RawRetentionMonths)
        {
            changes["rawRetentionMonths"] = new { before = before.RawRetentionMonths, after = after.RawRetentionMonths };
        }
        if (after.MinGroupSize != before.MinGroupSize)
        {
            changes["minGroupSize"] = new { before = before.MinGroupSize, after = after.MinGroupSize };
        }
        if (after.Sentiment != before.Sentiment)
        {
            changes["sentiment"] = new { before = before.Sentiment, after = after.Sentiment };
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AnalyticsPrivacySettingChanged, AuditTarget, null, Changes: changes), ct);
        _cached = after;
        return new AnalyticsPolicyView(after.Nonessential, after.RawRetentionMonths, after.MinGroupSize, false, after.Version, after.UpdatedAt, true, after.Sentiment,
            sentimentAvailable);
    }
}
