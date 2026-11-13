using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

public static class DetectionModes
{
    public const string Off = "off";
    public const string Warn = "warn";
    public const string Block = "block";

    public static readonly IReadOnlyList<string> All = [Off, Warn, Block];
}

public sealed record DetectionPatternView(Guid Id, string Name, string Pattern, bool Enabled, DateTimeOffset? TestedAt, DateTimeOffset UpdatedAt);

public sealed record DetectionView(bool Enabled, IReadOnlyDictionary<string, string> Modes, IReadOnlyList<string> BuiltIn, IReadOnlyList<DetectionPatternView> Patterns,
    bool CanManage, bool CanEditPatterns);

public sealed record DetectionSettingsRequest(bool? Enabled, IReadOnlyDictionary<string, string>? Modes);

public sealed record DetectionPatternRequest(string? Name, string? Pattern, string? Sample, bool? Enabled);

public sealed record PatternTestRequest(string? Pattern, string? Sample);

public sealed record PatternTestView(bool Valid, int Matches);

/// <summary>
/// Sensitive data detection settings and tenant patterns (spec 026 US6, FR-015, FR-018). Security officers and
/// administrators read them. Turning detection on and setting the mode per location type needs <c>security.manage</c>
/// and step-up. Tenant patterns are managed by security officers (<c>security.respond</c>) or managers, and a pattern is
/// enabled only after its sample text matched. Every change is audited with before and after values; samples are never
/// kept or logged.
/// </summary>
public sealed class DetectionService(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, IClock clock) : ISensitiveDataScanner
{
    public const int MaxPatterns = 50;
    public const int MaxSampleChars = 10_000;
    public const int MaxReason = 500;

    public static readonly IReadOnlyDictionary<string, string> DefaultModes = SensitiveTargets.All.ToDictionary(t => t, _ => DetectionModes.Warn);

    private async Task<bool> HoldsAsync(string permission, CancellationToken ct) => (await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed;

    private async Task DemandReadAsync(CancellationToken ct)
    {
        foreach (var permission in new[] { Permissions.SecurityManage, Permissions.SecurityView, Permissions.SecurityRespond })
        {
            if (await HoldsAsync(permission, ct))
            {
                await entitlements.DemandAsync(Capabilities.SecurityDataDetection, ct);
                return;
            }
        }
        throw AppException.Denied();
    }

    private async Task DemandPatternsAsync(CancellationToken ct)
    {
        if (!await HoldsAsync(Permissions.SecurityManage, ct) && !await HoldsAsync(Permissions.SecurityRespond, ct))
        {
            throw AppException.Denied();
        }
        await entitlements.DemandAsync(Capabilities.SecurityDataDetection, ct);
    }

    private async Task<(bool Enabled, Dictionary<string, string> Modes)> SettingsAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select detection_enabled, detection_modes::text from security.policy", null,
            r => (Enabled: r.GetBoolean(0), Modes: r.GetString(1)), ct);
        var modes = new Dictionary<string, string>(DefaultModes, StringComparer.Ordinal);
        if (row.Modes is { } json)
        {
            foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
            {
                if (SensitiveTargets.All.Contains(key) && DetectionModes.All.Contains(value))
                {
                    modes[key] = value;
                }
            }
        }
        return (row.Enabled, modes);
    }

    private async Task<IReadOnlyList<DetectionPatternView>> PatternsAsync(CancellationToken ct) =>
        await session.QueryAsync("select id, name, pattern, enabled, tested_at, updated_at from security.detection_pattern order by lower(name)", null, ReadPattern, ct);

    private static DetectionPatternView ReadPattern(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3),
        r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5));

    public async Task<DetectionView> GetAsync(CancellationToken ct)
    {
        await DemandReadAsync(ct);
        var (enabled, modes) = await SettingsAsync(ct);
        return new DetectionView(enabled, modes, SensitiveTypes.BuiltIn, await PatternsAsync(ct), await HoldsAsync(Permissions.SecurityManage, ct),
            await HoldsAsync(Permissions.SecurityManage, ct) || await HoldsAsync(Permissions.SecurityRespond, ct));
    }

    public async Task<DetectionView> UpdateAsync(DetectionSettingsRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.SecurityDataDetection, ct);
        var (enabled, modes) = await SettingsAsync(ct);
        var next = new Dictionary<string, string>(modes, StringComparer.Ordinal);
        var errors = new ValidationErrors();
        foreach (var (key, value) in request.Modes ?? new Dictionary<string, string>())
        {
            if (!SensitiveTargets.All.Contains(key) || !DetectionModes.All.Contains(value))
            {
                errors.Add("modes", "validation.security.detection_mode");
                continue;
            }
            next[key] = value;
        }
        errors.ThrowIfAny();
        var nextEnabled = request.Enabled ?? enabled;
        if (nextEnabled != enabled || !next.OrderBy(m => m.Key).SequenceEqual(modes.OrderBy(m => m.Key)))
        {
            await session.ExecAsync("insert into security.policy (tenant_id) values (platform.current_tenant_id()) on conflict do nothing", null, ct);
            await session.ExecAsync("update security.policy set detection_enabled = @enabled, detection_modes = @modes, updated_at = now()",
                new { enabled = nextEnabled, modes = Jsonb.From(next) }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.SecurityDetectionChanged, "security_policy", null,
                Changes: new { before = new { enabled, modes }, after = new { enabled = nextEnabled, modes = next } }), ct);
        }
        return await GetAsync(ct);
    }

    /// <summary>Tries a pattern on sample text (US6 scenario 3). Nothing is stored or audited.</summary>
    public async Task<PatternTestView> TestAsync(PatternTestRequest request, CancellationToken ct)
    {
        await DemandPatternsAsync(ct);
        var regex = string.IsNullOrWhiteSpace(request.Pattern) || request.Pattern.Length > 500 ? null : SensitivePatterns.Compile(request.Pattern);
        if (regex is null)
        {
            return new PatternTestView(false, 0);
        }
        var sample = request.Sample ?? "";
        return new PatternTestView(true, SensitivePatterns.Count(regex, sample.Length > MaxSampleChars ? sample[..MaxSampleChars] : sample));
    }

    public async Task<DetectionPatternView> CreatePatternAsync(DetectionPatternRequest request, CancellationToken ct)
    {
        await DemandPatternsAsync(ct);
        if (await session.ScalarAsync<long>("select count(*) from security.detection_pattern", null, ct) >= MaxPatterns)
        {
            throw AppException.Conflict("conflict.security.too_many_patterns");
        }
        var (name, pattern, enabled, tested) = Validate(request, null);
        var id = Ids.New();
        var now = clock.UtcNow;
        await NameFreeAsync(name, null, ct);
        await session.ExecAsync(
            """
            insert into security.detection_pattern (tenant_id, id, name, pattern, enabled, tested_at, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @name, @pattern, @enabled, @tested, @user, @now, @now)
            """, new { id, name, pattern, enabled, tested = tested ? now : (DateTimeOffset?)null, user = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityDetectionPatternChanged, "detection_pattern", id.ToString(),
            Changes: new { change = "created", after = new { name, pattern, enabled } }), ct);
        return (await PatternAsync(id, ct))!;
    }

    public async Task<DetectionPatternView> UpdatePatternAsync(Guid id, DetectionPatternRequest request, CancellationToken ct)
    {
        await DemandPatternsAsync(ct);
        var current = await PatternAsync(id, ct) ?? throw AppException.NotFound();
        var (name, pattern, enabled, tested) = Validate(request, current);
        await NameFreeAsync(name, id, ct);
        var now = clock.UtcNow;
        // A changed expression must be tested again before it may stay enabled.
        var testedAt = tested ? now : pattern == current.Pattern ? current.TestedAt : null;
        await session.ExecAsync(
            "update security.detection_pattern set name = @name, pattern = @pattern, enabled = @enabled, tested_at = @testedAt, updated_at = @now where id = @id",
            new { id, name, pattern, enabled, testedAt, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityDetectionPatternChanged, "detection_pattern", id.ToString(),
            Changes: new { change = "updated", before = new { current.Name, current.Pattern, current.Enabled }, after = new { name, pattern, enabled } }), ct);
        return (await PatternAsync(id, ct))!;
    }

    public async Task DeletePatternAsync(Guid id, CancellationToken ct)
    {
        await DemandPatternsAsync(ct);
        var current = await PatternAsync(id, ct) ?? throw AppException.NotFound();
        await session.ExecAsync("delete from security.detection_pattern where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityDetectionPatternChanged, "detection_pattern", id.ToString(),
            Changes: new { change = "deleted", before = new { current.Name, current.Pattern, current.Enabled } }), ct);
    }

    private Task<DetectionPatternView?> PatternAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync("select id, name, pattern, enabled, tested_at, updated_at from security.detection_pattern where id = @id", new { id }, ReadPattern, ct);

    private async Task NameFreeAsync(string name, Guid? self, CancellationToken ct)
    {
        if (await session.ScalarAsync<bool>("select exists (select 1 from security.detection_pattern where lower(name) = lower(@name) and id is distinct from @self)",
            new { name, self }, ct))
        {
            throw AppException.Validation("name", "validation.security.pattern_name_taken");
        }
    }

    /// <summary>
    /// Name and expression rules, and the test: a pattern is enabled only when its sample matched, or when it was
    /// tested before and the expression did not change.
    /// </summary>
    private static (string Name, string Pattern, bool Enabled, bool Tested) Validate(DetectionPatternRequest request, DetectionPatternView? current)
    {
        var errors = new ValidationErrors();
        var name = request.Name?.Trim() ?? current?.Name ?? "";
        if (name.Length is < 1 or > 80)
        {
            errors.Add("name", "validation.security.pattern_name");
        }
        var pattern = request.Pattern ?? current?.Pattern ?? "";
        var regex = pattern.Length is < 1 or > 500 ? null : SensitivePatterns.Compile(pattern);
        if (regex is null)
        {
            errors.Add("pattern", "validation.security.pattern");
        }
        var sample = request.Sample ?? "";
        var tested = regex is not null && sample.Length > 0
            && SensitivePatterns.Count(regex, sample.Length > MaxSampleChars ? sample[..MaxSampleChars] : sample) > 0;
        if (sample.Length > 0 && regex is not null && !tested)
        {
            errors.Add("sample", "validation.security.pattern_no_match");
        }
        var enabled = request.Enabled ?? current?.Enabled ?? false;
        var stillTested = current is not null && current.TestedAt is not null && pattern == current.Pattern;
        if (enabled && !tested && !stillTested)
        {
            errors.Add("enabled", "validation.security.pattern_untested");
        }
        errors.ThrowIfAny();
        return (name, pattern, enabled, tested);
    }

    // ---- Scanning (ISensitiveDataScanner) ----

    public async Task GuardAsync(string target, IReadOnlyList<SensitiveText> parts, string? classification, string? confirmReason, string resourceType,
        string resourceId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SecurityDataDetection, ct))
        {
            return;
        }
        var (enabled, modes) = await SettingsAsync(ct);
        var mode = modes.GetValueOrDefault(target, DetectionModes.Warn);
        if (!enabled || mode == DetectionModes.Off)
        {
            return;
        }
        var tenant = new List<TenantPattern>();
        foreach (var p in await session.QueryAsync("select name, pattern from security.detection_pattern where enabled order by lower(name)", null,
            r => (Name: r.GetString(0), Pattern: r.GetString(1)), ct))
        {
            if (SensitivePatterns.Compile(p.Pattern) is { } regex)
            {
                tenant.Add(new TenantPattern(p.Name, regex));
            }
        }
        var findings = SensitivePatterns.Scan(parts, tenant);
        if (findings.Count == 0)
        {
            return;
        }
        var extensions = new Dictionary<string, object?> { ["findings"] = findings, ["target"] = target };
        if (mode == DetectionModes.Block && findings.Any(f => f.Blocking))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.security.sensitive_data_blocked", null, extensions);
        }
        // Raising the classification answers the warning (US6 scenario 1).
        if (classification is "confidential" or "restricted")
        {
            return;
        }
        var reason = confirmReason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.security.sensitive_data_found", null, extensions);
        }
        if (reason.Length > MaxReason)
        {
            throw AppException.Validation("sensitiveReason", "validation.security.sensitive_reason");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SecuritySensitiveDataConfirmed, resourceType, resourceId,
            Changes: new { target, reason, findings = findings.Select(f => new { f.Type, f.Name, f.Location, f.Count }) }), ct);
    }
}
