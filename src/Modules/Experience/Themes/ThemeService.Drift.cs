using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Themes;

/// <summary>
/// One drift finding (spec 033 R2 T022, FR-015, US8 scenario 1). <paramref name="Kind"/> names the problem and its
/// remediation; <paramref name="Scope"/> is <c>tenant</c> or an organization id where the theme applies, and a finding
/// in a brand itself carries the brand instead.
/// </summary>
public sealed record DriftFinding(
    string Kind,
    string Severity,
    string? Scope,
    IReadOnlyDictionary<string, string>? ScopeNames,
    Guid? BrandId,
    string? BrandName,
    string? Token,
    string? Slot,
    string? Mode,
    string? Against,
    double? Ratio,
    double? Required);

public sealed record DriftReport(DateTimeOffset GeneratedAt, int Scopes, IReadOnlyList<DriftFinding> Findings);

public static class DriftKinds
{
    /// <summary>A brand slot points at an asset that no longer exists.</summary>
    public const string AssetMissing = "asset_missing";

    /// <summary>A brand slot holds an asset outside today's rules (format or size), kept from before the rule.</summary>
    public const string AssetNonCompliant = "asset_noncompliant";

    /// <summary>An override or brand value names a token the schema no longer has; the scope falls back to the value above it.</summary>
    public const string UnknownToken = "token_unknown";

    /// <summary>An override or brand value the token's type no longer accepts (an unapproved font, say); it is ignored.</summary>
    public const string InvalidValue = "token_invalid";

    /// <summary>An organization override that a parent's lock overrules, so it has no effect.</summary>
    public const string LockedOverride = "override_locked";

    /// <summary>An alias that resolves to nothing at this scope; the platform value is used.</summary>
    public const string Unresolved = "token_unresolved";

    /// <summary>Text or controls that miss the contrast or size minimum at this scope.</summary>
    public const string Inaccessible = "inaccessible";
}

public sealed partial class ThemeService
{
    /// <summary>
    /// What has drifted from the rules in the scopes the caller edits themes for: brand assets that are gone or out of
    /// policy, overrides that name removed tokens, carry values no longer accepted or are overruled by a parent's lock, and
    /// theme values that fail contrast or size at the scope where they apply. A tenant theme editor sees every scope and the
    /// brands; an organization's editor sees their organizations only.
    /// </summary>
    public async Task<DriftReport> DriftAsync(CancellationToken ct)
    {
        await DemandBrandReaderAsync(ct);
        await EnsureSeededAsync(ct);
        var tenantWide = (await authz.CheckAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct)).Allowed;
        var assignments = await AssignmentsAsync(ct);
        var scopes = new List<Guid?>();
        foreach (var scope in assignments.Select(a => a.Scope).Distinct())
        {
            if (scope is null ? tenantWide : (await authz.CheckAsync(Permissions.ThemeEdit, AuthzTarget.OrgUnit(scope.Value), ct)).Allowed)
            {
                scopes.Add(scope);
            }
        }
        var names = await hierarchy.GetNamesAsync(scopes.OfType<Guid>(), ct);
        IReadOnlyDictionary<string, string>? NamesOf(Guid? unit) => unit is { } u && names.TryGetValue(u, out var n) ? n : null;
        var findings = new List<DriftFinding>();

        if (tenantWide)
        {
            var assets = await session.QueryAsync("select id, content_type, bytes from experience.brand_asset", null,
                r => (Id: r.GetGuid(0), Type: r.GetString(1), Bytes: r.GetInt32(2)), ct);
            var byId = assets.ToDictionary(a => a.Id);
            var brands = await session.QueryAsync("select id from experience.brand order by name", null, r => r.GetGuid(0), ct);
            foreach (var id in brands)
            {
                var brand = (await BrandAsync(id, null, ct))!;
                foreach (var (slot, reference) in brand.Assets.Where(a => BrandSlots.IsImage(a.Key)).OrderBy(a => a.Key, StringComparer.Ordinal))
                {
                    if (!reference.StartsWith("asset:", StringComparison.Ordinal) || !Guid.TryParse(reference["asset:".Length..], out var assetId))
                    {
                        continue;
                    }
                    if (!byId.TryGetValue(assetId, out var asset))
                    {
                        findings.Add(new(DriftKinds.AssetMissing, "error", null, null, brand.Id, brand.Name, null, slot, null, null, null, null));
                    }
                    else if (asset.Type is not ("image/png" or "image/jpeg" or "image/svg+xml") || asset.Bytes > ImageValidator.MaxBytes)
                    {
                        findings.Add(new(DriftKinds.AssetNonCompliant, "warning", null, null, brand.Id, brand.Name, null, slot, null, null, null, null));
                    }
                }
                foreach (var (token, kind) in Unaccepted(brand.Tokens))
                {
                    findings.Add(new(kind, "warning", null, null, brand.Id, brand.Name, token, null, null, null, null, null));
                }
            }
        }

        foreach (var scope in scopes.OrderBy(s => s.HasValue).ThenBy(s => s))
        {
            var assignment = assignments.First(a => a.Scope == scope);
            var scopeName = ScopeName(scope);
            foreach (var (token, kind) in Unaccepted(assignment.Overrides))
            {
                findings.Add(new(kind, "warning", scopeName, NamesOf(scope), null, null, token, null, null, null, null, null));
            }
            var locks = await LocksForAsync(scope, ct);
            foreach (var token in assignment.Overrides.Keys.Where(k => locks.TryGetValue(k, out var l) && l.Scope != scope).Order(StringComparer.Ordinal))
            {
                findings.Add(new(DriftKinds.LockedOverride, "warning", scopeName, NamesOf(scope), null, null, token, null, null, null, null, null));
            }
            var resolved = await ResolveAsync(scope, ct);
            // Values refused where they are stored are reported there; what is left is an alias that leads nowhere.
            var (layers, _) = await LayersAsync(scope, ct, null, null);
            var stored = layers.SelectMany(l => Unaccepted(l.Tokens)).Select(u => u.Token).ToHashSet(StringComparer.Ordinal);
            foreach (var token in resolved.InvalidTokens.Where(t => TokenSchema.ByName.ContainsKey(t) && !stored.Contains(t)).Distinct().Order(StringComparer.Ordinal))
            {
                findings.Add(new(DriftKinds.Unresolved, "warning", scopeName, NamesOf(scope), null, null, token, null, null, null, null, null));
            }
            var strict = assignment.BrandId is { } b && (await BrandAsync(b, null, ct))?.Strict == true;
            foreach (var f in ThemeEngine.Check(resolved, strict))
            {
                findings.Add(new(DriftKinds.Inaccessible, f.Severity == "blocking" ? "error" : "warning", scopeName, NamesOf(scope), null, null,
                    f.Token, null, f.Mode, f.Against, f.Ratio, f.Required));
            }
        }
        return new DriftReport(clock.UtcNow, scopes.Count, findings);
    }

    /// <summary>Stored values the schema no longer accepts: a removed token, or a value its type now refuses.</summary>
    private static IEnumerable<(string Token, string Kind)> Unaccepted(IReadOnlyDictionary<string, TokenValue> tokens)
    {
        foreach (var (name, value) in tokens.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!TokenSchema.ByName.TryGetValue(name, out var definition))
            {
                yield return (name, DriftKinds.UnknownToken);
            }
            else if (TokenSchema.Check(definition, value?.Value) is not null
                || (value?.Modes ?? new Dictionary<string, string>()).Values.Any(m => TokenSchema.Check(definition, m) is not null))
            {
                yield return (name, DriftKinds.InvalidValue);
            }
        }
    }
}
