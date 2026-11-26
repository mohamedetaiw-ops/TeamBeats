using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Personalization;

public static class BehaviorConsentModes
{
    public const string OptIn = "opt_in";
    public const string OptOut = "opt_out";
}

public sealed record BehaviorPolicyView(bool Available, bool Enabled, string ConsentMode, DateTimeOffset? UpdatedAt);

public sealed record BehaviorPolicyRequest(bool? Enabled, string? ConsentMode);

/// <summary>A person's view: whether the tenant offers it, the consent mode, their own choice (null when not made) and whether it applies now.</summary>
public sealed record BehaviorChoiceView(bool Available, string ConsentMode, bool? Consented, bool Active);

public sealed record BehaviorChoiceRequest(bool? Consented);

public sealed record ExportedSignal(string Kind, string Key, DateOnly Day, int Count);

/// <summary>
/// Behavioral personalization (spec 008 R3, US6, FR-033). Off unless the package has
/// <c>experience.behavioral_personalization</c> and the tenant turns it on; under opt-in it applies only to people who
/// agreed, under opt-out to everyone who has not declined. Declining deletes the person's signals and keeps every other
/// setting. Signals are the person's own daily counts per page, kept <see cref="KeepDays"/> days, and are read only for
/// that person.
/// </summary>
public sealed class BehaviorPersonalization(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IClock clock) : IBehaviorSignals
{
    public const int KeepDays = 90;
    public const string PageKind = "page";
    private static readonly string[] Kinds = [PageKind];

    private Task<bool> AvailableAsync(CancellationToken ct) => entitlements.IsEnabledAsync(Capabilities.ExperienceBehavioralPersonalization, ct);

    private async Task<(bool Enabled, string Mode, DateTimeOffset? UpdatedAt)> PolicyRowAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select enabled, consent_mode, updated_at from experience.behavior_policy", null,
            r => (Found: true, Enabled: r.GetBoolean(0), Mode: r.GetString(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        return row.Found ? (row.Enabled, row.Mode, row.At) : (false, BehaviorConsentModes.OptIn, null);
    }

    private async Task DemandAdminAsync(CancellationToken ct)
    {
        context.RequireUser();
        if (!(await authz.CheckAsync(Permissions.PersonalizationPolicy, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
    }

    public async Task<BehaviorPolicyView> PolicyAsync(CancellationToken ct)
    {
        await DemandAdminAsync(ct);
        var (enabled, mode, at) = await PolicyRowAsync(ct);
        return new BehaviorPolicyView(await AvailableAsync(ct), enabled, mode, at);
    }

    /// <summary>Turning it on needs the package feature; every change is audited with the consent mode.</summary>
    public async Task<BehaviorPolicyView> SetPolicyAsync(BehaviorPolicyRequest request, CancellationToken ct)
    {
        await DemandAdminAsync(ct);
        var (enabled, mode, _) = await PolicyRowAsync(ct);
        var nextEnabled = request.Enabled ?? enabled;
        var nextMode = request.ConsentMode ?? mode;
        if (nextMode is not (BehaviorConsentModes.OptIn or BehaviorConsentModes.OptOut))
        {
            throw AppException.Validation("consentMode", "validation.experience.behavior_consent_mode");
        }
        if (nextEnabled && !await AvailableAsync(ct))
        {
            throw AppException.Validation("enabled", "validation.experience.behavior_unavailable");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into experience.behavior_policy (tenant_id, enabled, consent_mode, updated_by, updated_at)
            values (platform.current_tenant_id(), @enabled, @mode, @by, @now)
            on conflict (tenant_id) do update set enabled = excluded.enabled, consent_mode = excluded.consent_mode, updated_by = excluded.updated_by,
              updated_at = excluded.updated_at
            """, new { enabled = nextEnabled, mode = nextMode, by = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ExperienceBehavioralPersonalizationChanged, "experience_behavior_policy", "tenant",
            Changes: new { enabled = nextEnabled, consentMode = nextMode, before = new { enabled, consentMode = mode } }), ct);
        return new BehaviorPolicyView(await AvailableAsync(ct), nextEnabled, nextMode, now);
    }

    private async Task<bool?> ConsentAsync(Guid user, CancellationToken ct) =>
        (await session.QueryAsync("select consented from experience.behavior_consent where user_id = @user", new { user }, r => (bool?)r.GetBoolean(0), ct))
        .FirstOrDefault();

    private static bool Applies(bool enabled, string mode, bool? consented) =>
        enabled && (mode == BehaviorConsentModes.OptOut ? consented != false : consented == true);

    public async Task<BehaviorChoiceView> ChoiceAsync(CancellationToken ct)
    {
        var user = context.RequireUser();
        var (enabled, mode, _) = await PolicyRowAsync(ct);
        var available = enabled && await AvailableAsync(ct);
        var consented = await ConsentAsync(user, ct);
        return new BehaviorChoiceView(available, mode, consented, available && Applies(enabled, mode, consented));
    }

    /// <summary>The person agrees or declines. Declining deletes their signals at once; nothing else they set changes.</summary>
    public async Task<BehaviorChoiceView> ChooseAsync(BehaviorChoiceRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (request.Consented is not { } consented)
        {
            throw AppException.Validation("consented", "validation.experience.behavior_choice");
        }
        await session.ExecAsync(
            """
            insert into experience.behavior_consent (tenant_id, user_id, consented, decided_at) values (platform.current_tenant_id(), @user, @consented, @now)
            on conflict (tenant_id, user_id) do update set consented = excluded.consented, decided_at = excluded.decided_at
            """, new { user, consented, now = clock.UtcNow }, ct);
        if (!consented)
        {
            await session.ExecAsync("delete from experience.behavior_signal where user_id = @user", new { user }, ct);
        }
        return await ChoiceAsync(ct);
    }

    public async Task<bool> ActiveAsync(CancellationToken ct)
    {
        if (context.ActorType != ActorType.User || context.ActorId is not { } user)
        {
            return false;
        }
        var (enabled, mode, _) = await PolicyRowAsync(ct);
        return enabled && Applies(enabled, mode, await ConsentAsync(user, ct)) && await AvailableAsync(ct);
    }

    /// <summary>Counts one use of a key for the signed-in person today, only while personalization applies to them.</summary>
    internal async Task RecordAsync(string kind, string key, CancellationToken ct)
    {
        if (!Kinds.Contains(kind) || key.Length is 0 or > 300 || !await ActiveAsync(ct))
        {
            return;
        }
        await session.ExecAsync(
            """
            insert into experience.behavior_signal (tenant_id, user_id, kind, key, day, count) values (platform.current_tenant_id(), @user, @kind, @key, @day, 1)
            on conflict (tenant_id, user_id, kind, key, day) do update set count = least(experience.behavior_signal.count + 1, 100000)
            """, new { user = context.RequireUser(), kind, key, day = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime) }, ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> UsageAsync(string kind, CancellationToken ct)
    {
        if (!await ActiveAsync(ct))
        {
            return new Dictionary<string, int>();
        }
        var since = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-KeepDays);
        var rows = await session.QueryAsync(
            "select key, sum(count)::int from experience.behavior_signal where user_id = @user and kind = @kind and day > @since group by key",
            new { user = context.RequireUser(), kind, since }, r => (Key: r.GetString(0), Count: r.GetInt32(1)), ct);
        return rows.ToDictionary(r => r.Key, r => r.Count, StringComparer.Ordinal);
    }

    /// <summary>The person's own signals for the preference export (FR-034).</summary>
    internal Task<List<ExportedSignal>> ExportAsync(Guid user, CancellationToken ct) =>
        session.QueryAsync("select kind, key, day, count from experience.behavior_signal where user_id = @user order by day desc, kind, key", new { user },
            r => new ExportedSignal(r.GetString(0), r.GetString(1), DateOnly.FromDateTime(r.GetDateTime(2)), r.GetInt32(3)), ct);

    /// <summary>Deletes signals older than <see cref="KeepDays"/> days in the current tenant. Returns how many rows went.</summary>
    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync("delete from experience.behavior_signal where day <= @before", new { before = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-KeepDays) }, ct);
}
