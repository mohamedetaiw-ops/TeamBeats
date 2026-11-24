using System.Globalization;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Entitlements;

/// <summary>How a quota is enforced for a tenant (spec 005 US3): set by the platform, <c>deny</c> by default.</summary>
public sealed record EnforcementView(string CapabilityKey, string Mode, int GracePct);

public sealed record EnforcementRequest(string? Mode, int? GracePct);

/// <summary>This period's usage of one limit capability, for the plan page.</summary>
public sealed record QuotaUsageView(string CapabilityKey, string Period, long Used, long? Limit, string Mode, int GracePct, DateTimeOffset? ExhaustedAt);

/// <summary>A quota reached its limit this period; published once per tenant, capability and period (section 12).</summary>
public sealed record QuotaExhausted(Guid TenantId, string CapabilityKey, string Period, long? Limit) : IIntegrationEvent;

/// <summary>
/// Quota metering and decisions (spec 005 US3, FR-006, FR-007). Usage is counted per tenant, capability and calendar
/// month in UTC with one atomic statement, so concurrent requests never pass the ceiling: the limit for <c>deny</c>,
/// the limit plus <c>grace_pct</c> for <c>grace</c>, none for <c>warn</c>. That is inside the decided tolerance of
/// max(1%, 10). Reaching the limit is audited and announced once per period.
/// </summary>
public sealed class QuotaMeter(IDbSession session, ITenantContext context, IAuditWriter audit, IOutbox outbox, IClock clock)
{
    public const string Deny = "deny";
    public const string Grace = "grace";
    public const string Warn = "warn";
    public static readonly IReadOnlyList<string> Modes = [Deny, Grace, Warn];

    public string Period => clock.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public async Task<(string Mode, int GracePct)> EnforcementAsync(string key, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select mode, grace_pct from entitlements.enforcement where capability_key = @k", new { k = key },
            r => (Mode: r.GetString(0), GracePct: r.GetInt32(1)), ct);
        return row == default ? (Deny, 0) : row;
    }

    public static long? Ceiling(long? limit, string mode, int gracePct) => limit is not { } l ? null : mode switch
    {
        Grace => l + (long)Math.Ceiling(l * gracePct / 100.0),
        Warn => null,
        _ => l,
    };

    public async Task<QuotaDecision> ConsumeAsync(string key, long amount, long? limit, CancellationToken ct)
    {
        var (mode, pct) = await EnforcementAsync(key, ct);
        var ceiling = Ceiling(limit, mode, pct);
        long? used = null;
        if (ceiling is null || amount <= ceiling)
        {
            // One statement: the row lock serializes concurrent consumers, and a refused update counts nothing.
            used = (await session.QueryAsync(
                """
                insert into entitlements.usage_counter (tenant_id, capability_key, period, used) values (@t, @k, @p, @n)
                on conflict (tenant_id, capability_key, period) do update set used = usage_counter.used + excluded.used, updated_at = now()
                where @c::bigint is null or usage_counter.used + excluded.used <= @c
                returning used
                """, new { t = context.RequireTenant(), k = key, p = Period, n = amount, c = ceiling }, r => (long?)r.GetInt64(0), ct)).FirstOrDefault();
        }
        if (used is { } now)
        {
            if (limit is { } l && now >= l)
            {
                await ExhaustedAsync(key, limit, ct);
            }
            return new QuotaDecision(true, now, limit, limit is { } over && now > over);
        }
        await ExhaustedAsync(key, limit, ct);
        return new QuotaDecision(false, await UsedAsync(key, ct), limit, Reason: QuotaDecision.QuotaExhausted);
    }

    public async Task ReleaseAsync(string key, long amount, CancellationToken ct) =>
        await session.ExecAsync(
            "update entitlements.usage_counter set used = greatest(used - @n, 0), updated_at = now() where capability_key = @k and period = @p",
            new { k = key, p = Period, n = amount }, ct);

    public async Task<QuotaDecision> CheckUsageAsync(string key, long used, long amount, long? limit, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            insert into entitlements.usage_counter (tenant_id, capability_key, period, used) values (@t, @k, @p, @u)
            on conflict (tenant_id, capability_key, period) do update set used = greatest(usage_counter.used, excluded.used), updated_at = now()
            """, new { t = context.RequireTenant(), k = key, p = Period, u = used }, ct);
        var (mode, pct) = await EnforcementAsync(key, ct);
        var ceiling = Ceiling(limit, mode, pct);
        var allowed = ceiling is null || used + amount <= ceiling;
        if (limit is { } l && (used >= l || !allowed))
        {
            await ExhaustedAsync(key, limit, ct);
        }
        return allowed
            ? new QuotaDecision(true, used, limit, limit is { } over && used + amount > over)
            : new QuotaDecision(false, used, limit, Reason: QuotaDecision.QuotaExhausted);
    }

    private Task<long> UsedAsync(string key, CancellationToken ct) =>
        session.ScalarAsync<long>("select coalesce(max(used), 0) from entitlements.usage_counter where capability_key = @k and period = @p", new { k = key, p = Period }, ct);

    /// <summary>Marks the period's quota as reached, once: the audit entry and the notice to administrators follow only the first time.</summary>
    private async Task ExhaustedAsync(string key, long? limit, CancellationToken ct)
    {
        var first = await session.ExecAsync(
            """
            insert into entitlements.usage_counter (tenant_id, capability_key, period, used, exhausted_at) values (@t, @k, @p, 0, now())
            on conflict (tenant_id, capability_key, period) do update set exhausted_at = now() where usage_counter.exhausted_at is null
            """, new { t = context.RequireTenant(), k = key, p = Period }, ct);
        if (first == 0)
        {
            return;
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.QuotaExhausted, "capability", key, Changes: new { period = Period, limit }), ct);
        await outbox.EnqueueAsync(new QuotaExhausted(context.RequireTenant(), key, Period, limit), ct);
    }

    public async Task<IReadOnlyList<QuotaUsageView>> UsageAsync(IReadOnlyDictionary<string, CapabilityValue> resolved, CancellationToken ct)
    {
        var limits = (await session.QueryAsync("select key from entitlements.capability where type = 'limit'", null, r => r.GetString(0), ct)).ToHashSet();
        var counters = (await session.QueryAsync("select capability_key, used, exhausted_at from entitlements.usage_counter where period = @p", new { p = Period },
            r => (Key: r.GetString(0), Used: r.GetInt64(1), At: r.Get<DateTimeOffset?>("exhausted_at")), ct)).ToDictionary(c => c.Key);
        var enforcement = (await session.QueryAsync("select capability_key, mode, grace_pct from entitlements.enforcement", null,
            r => (Key: r.GetString(0), Mode: r.GetString(1), Pct: r.GetInt32(2)), ct)).ToDictionary(e => e.Key);
        var result = new List<QuotaUsageView>();
        foreach (var (key, value) in resolved.Where(r => limits.Contains(r.Key) && r.Value.Enabled).OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            // Only quotas that are metered this period, or carry a limit, are shown.
            if (!counters.TryGetValue(key, out var c) && value.Limit is null)
            {
                continue;
            }
            var e = enforcement.TryGetValue(key, out var found) ? found : (Key: key, Mode: Deny, Pct: 0);
            result.Add(new QuotaUsageView(key, Period, c.Used, value.Limit, e.Mode, e.Pct, c.At));
        }
        return result;
    }
}

/// <summary>Enforcement per quota, set by platform operators only; audited like add-ons and overrides (ENT-008).</summary>
public sealed class QuotaEnforcement(IDbSession session, ITenantContext context, IAuditWriter audit)
{
    public async Task<EnforcementView> SetAsync(string key, EnforcementRequest request, CancellationToken ct)
    {
        var type = await session.ScalarAsync<string>("select type from entitlements.capability where key = @k", new { k = key }, ct);
        var errors = new ValidationErrors();
        if (type != "limit")
        {
            errors.Add("capabilityKey", "validation.quota.not_a_limit");
        }
        var mode = request.Mode?.Trim().ToLowerInvariant() ?? QuotaMeter.Deny;
        if (!QuotaMeter.Modes.Contains(mode))
        {
            errors.Add("mode", "validation.quota.mode_invalid");
        }
        var pct = mode == QuotaMeter.Grace ? request.GracePct ?? 10 : 0;
        if (pct is < 1 or > 100 && mode == QuotaMeter.Grace)
        {
            errors.Add("gracePct", "validation.quota.grace_invalid");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            insert into entitlements.enforcement (tenant_id, capability_key, mode, grace_pct) values (@t, @k, @m, @g)
            on conflict (tenant_id, capability_key) do update set mode = excluded.mode, grace_pct = excluded.grace_pct, updated_at = now()
            """, new { t = context.RequireTenant(), k = key, m = mode, g = pct }, ct);
        var changes = new { enforcement = mode, gracePct = pct };
        await audit.WriteAsync(new AuditEntry(AuditActions.OverrideChanged, "capability", key, Changes: changes), ct);
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.OverrideChanged, "capability", key, Changes: changes), context.TenantId, ct);
        return new EnforcementView(key, mode, pct);
    }
}

/// <summary>Tells the tenant's plan viewers once per period that a quota was reached (spec 005 US3 scenario 2).</summary>
public sealed class QuotaExhaustedNotifier(IAuthorizationProvisioning roles, INotificationSender notifications) : IMessageHandler<QuotaExhausted>
{
    public const string Template = "entitlements.quota_exhausted";

    public async Task HandleAsync(QuotaExhausted message, CancellationToken ct)
    {
        var recipients = await roles.TenantHoldersAsync(Permissions.EntitlementsView, ct);
        if (recipients.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(recipients.ToHashSet(), NotificationCategories.System, Template, "/admin/plan",
            new Dictionary<string, string>(),
            DedupeKey: $"{Template}:{message.CapabilityKey}:{message.Period}"), ct);
    }
}
