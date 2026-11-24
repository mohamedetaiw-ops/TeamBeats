using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Themes;

public sealed record TokenExtensionRequest(string? Scope, string? Key, string? Type, TokenValue? Value, string? Description);

public sealed record TokenExtensionReview(bool Approve, string? Note);

/// <param name="Available">Whether the package lets this tenant propose extensions; listed ones stay visible either way.</param>
public sealed record TokenExtensionList(bool Available, IReadOnlyList<TokenExtensionView> Items);

/// <param name="Inherited">Set by a parent scope: it applies here but is changed where it was made.</param>
public sealed record TokenExtensionView(Guid Id, string Scope, string Key, string Token, string CssName, string Type, TokenValue Value,
    string? Description, string State, Guid ProposedBy, DateTimeOffset ProposedAt, Guid? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNote,
    bool Inherited);

/// <summary>
/// Reviewed token extensions (spec 033 R2 T022, FR-013, US8 scenario 3). An extension adds a token in the <c>ext.</c>
/// namespace, such as <c>ext.campaign_accent</c> served as <c>--tb-ext-campaign-accent</c>, for the tenant or one
/// organization and those below it. It cannot replace a platform token or a protected behavior, because it has its own
/// namespace, and its value is one of the token types (an alias may point at a platform token), so free CSS never enters.
/// A theme editor proposes it; another person who may publish themes at that scope approves it, and only then is it served.
/// </summary>
public sealed partial class ThemeService
{
    public const int MaxExtensionsPerScope = 20;
    private const int MaxExtensionDescription = 200;

    public static readonly IReadOnlyList<string> ExtensionTypes =
        [TokenTypes.Color, TokenTypes.Dimension, TokenTypes.Number, TokenTypes.Duration, TokenTypes.Shadow];

    private sealed record ExtensionRow(Guid Id, Guid? Scope, string Key, string Type, TokenValue Value, string? Description, string State,
        Guid ProposedBy, DateTimeOffset ProposedAt, Guid? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNote);

    private const string ExtensionSelect =
        "select id, scope_org_unit_id, key, type, value::text, description, state, proposed_by, proposed_at, reviewed_by, reviewed_at, review_note from experience.token_extension";

    public static string ExtensionToken(string key) => "ext." + key;

    public static string ExtensionCssName(string key) => "--tb-ext-" + key.Replace('_', '-');

    /// <summary>The extensions made at a scope, and the approved ones it inherits from the scopes above.</summary>
    public async Task<TokenExtensionList> ListExtensionsAsync(string scope, CancellationToken ct)
    {
        var unit = await ScopeAsync(scope, Permissions.ThemeEdit, ct, alternative: Permissions.ThemePublish);
        var chain = await ChainAsync(unit, ct);
        var rows = await ExtensionRowsAsync(ct);
        var items = rows.Where(r => r.Scope == unit || (r.State == "approved" && chain.Contains(r.Scope)))
            .OrderBy(r => r.Scope != unit).ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => ExtensionView(r, r.Scope != unit)).ToList();
        return new TokenExtensionList(await entitlements.IsEnabledAsync(Capabilities.ExperienceTokenExtensions, ct), items);
    }

    public async Task<TokenExtensionView> ProposeExtensionAsync(TokenExtensionRequest request, CancellationToken ct)
    {
        var unit = await ScopeAsync(request.Scope ?? TenantScope, Permissions.ThemeEdit, ct);
        await entitlements.DemandAsync(Capabilities.ExperienceTokenExtensions, ct);
        var errors = new ValidationErrors();
        var key = request.Key?.Trim() ?? "";
        if (!ExtensionKey().IsMatch(key))
        {
            errors.Add("key", "validation.theme.extension_key");
        }
        var (type, value, description) = ValidateExtension(request.Type, request.Value, request.Description, errors);
        errors.ThrowIfAny();

        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('experience.token_extension:' || @t::text))", new { t = context.RequireTenant() }, ct);
        var rows = await ExtensionRowsAsync(ct);
        if (rows.Any(r => r.Scope == unit && r.Key == key))
        {
            throw AppException.Conflict("conflict.theme.extension_exists");
        }
        if (rows.Count(r => r.Scope == unit) >= MaxExtensionsPerScope)
        {
            throw AppException.Validation("key", "validation.theme.extension_limit");
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into experience.token_extension (tenant_id, id, scope_org_unit_id, key, type, value, description, state, proposed_by, proposed_at)
            values (platform.current_tenant_id(), @id, @unit, @key, @type, @value, @description, 'proposed', @user, @now)
            """, new { id, unit, key, type, value = Jsonb.From(value), description, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeExtensionProposed, "token_extension", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { scope = ScopeName(unit), key, type }), ct);
        return ExtensionView((await ExtensionRowsAsync(ct)).Single(r => r.Id == id), false);
    }

    /// <summary>Changes a proposal, or a rejected one, which goes back for review. An approved extension is removed and proposed again instead.</summary>
    public async Task<TokenExtensionView> UpdateExtensionAsync(Guid id, TokenExtensionRequest request, CancellationToken ct)
    {
        var row = await ExtensionForAsync(id, Permissions.ThemeEdit, ct);
        await entitlements.DemandAsync(Capabilities.ExperienceTokenExtensions, ct);
        if (row.State == "approved")
        {
            throw AppException.Conflict("conflict.theme.extension_approved");
        }
        var errors = new ValidationErrors();
        var (type, value, description) = ValidateExtension(request.Type ?? row.Type, request.Value, request.Description, errors);
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            update experience.token_extension set type = @type, value = @value, description = @description, state = 'proposed',
              proposed_by = @user, proposed_at = @now, reviewed_by = null, reviewed_at = null, review_note = null
            where id = @id
            """, new { id, type, value = Jsonb.From(value), description, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeExtensionProposed, "token_extension", id.ToString(), TargetOrgUnitId: row.Scope,
            Changes: new { scope = ScopeName(row.Scope), key = row.Key, type }), ct);
        return ExtensionView((await ExtensionRowsAsync(ct)).Single(r => r.Id == id), false);
    }

    /// <summary>Approves or rejects a proposal. The reviewer publishes themes at the scope and is not the person who proposed it.</summary>
    public async Task<TokenExtensionView> ReviewExtensionAsync(Guid id, TokenExtensionReview review, CancellationToken ct)
    {
        var row = await ExtensionForAsync(id, Permissions.ThemePublish, ct);
        await entitlements.DemandAsync(Capabilities.ExperienceTokenExtensions, ct);
        if (row.State != "proposed")
        {
            throw AppException.Conflict("conflict.theme.extension_reviewed");
        }
        if (row.ProposedBy == context.RequireUser())
        {
            throw AppException.Denied("authz.denied.extension_own_proposal");
        }
        var note = string.IsNullOrWhiteSpace(review.Note) ? null : review.Note.Trim();
        if (note is { Length: > MaxReasonLength })
        {
            throw AppException.Validation("note", "validation.theme.reason_too_long");
        }
        var state = review.Approve ? "approved" : "rejected";
        await session.ExecAsync(
            "update experience.token_extension set state = @state, reviewed_by = @user, reviewed_at = @now, review_note = @note where id = @id",
            new { id, state, user = context.RequireUser(), now = clock.UtcNow, note }, ct);
        await audit.WriteAsync(new AuditEntry(review.Approve ? AuditActions.ThemeExtensionApproved : AuditActions.ThemeExtensionRejected,
            "token_extension", id.ToString(), TargetOrgUnitId: row.Scope, Changes: new { scope = ScopeName(row.Scope), key = row.Key, note }), ct);
        if (review.Approve)
        {
            await BumpAsync(ct);
        }
        return ExtensionView((await ExtensionRowsAsync(ct)).Single(r => r.Id == id), false);
    }

    /// <summary>Removes an extension. One that is served needs the right to publish themes there; it stops at once.</summary>
    public async Task RemoveExtensionAsync(Guid id, CancellationToken ct)
    {
        var row = await ExtensionForAsync(id, Permissions.ThemeEdit, ct);
        if (row.State == "approved")
        {
            await authz.DemandAsync(Permissions.ThemePublish, row.Scope is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct);
        }
        await session.ExecAsync("delete from experience.token_extension where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeExtensionRemoved, "token_extension", id.ToString(), TargetOrgUnitId: row.Scope,
            Changes: new { scope = ScopeName(row.Scope), key = row.Key }), ct);
        if (row.State == "approved")
        {
            await BumpAsync(ct);
        }
    }

    /// <summary>
    /// The CSS custom properties of the approved extensions that apply at a scope, per variant. A deeper scope's extension
    /// with the same key wins; an alias takes the platform token's value in that variant.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ExtensionPropertiesAsync(Guid? unit, ResolvedTheme theme,
        CancellationToken ct)
    {
        var chain = await ChainAsync(unit, ct);
        var applying = (await ExtensionRowsAsync(ct))
            .Where(r => r.State == "approved" && chain.Contains(r.Scope))
            .OrderBy(r => chain.IndexOf(r.Scope))
            .GroupBy(r => r.Key, StringComparer.Ordinal)
            .Select(g => g.Last())
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (key, mode, _) in ThemeEngine.VariantKeys())
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in applying)
            {
                var raw = row.Value.Modes is { } modes && modes.TryGetValue(mode, out var modeValue) ? modeValue : row.Value.Value!;
                var value = TokenSchema.IsAlias(raw, out var target) ? theme.Variants[key].GetValueOrDefault(target) : raw;
                if (value is not null)
                {
                    properties[ExtensionCssName(row.Key)] = value;
                }
            }
            result[key] = properties;
        }
        return result;
    }

    private (string Type, TokenValue Value, string? Description) ValidateExtension(string? type, TokenValue? value, string? description,
        ValidationErrors errors)
    {
        var t = type ?? "";
        if (!ExtensionTypes.Contains(t))
        {
            errors.Add("type", "validation.theme.extension_type");
            return (t, value ?? new TokenValue(null), null);
        }
        // Validated as a token of that type under a name no platform token has.
        var definition = new TokenDefinition("ext.candidate", t, "");
        if ((value?.Value is { } v ? TokenSchema.Check(definition, v) : "validation.theme.token_value_invalid") is { } error)
        {
            errors.Add("value", error);
        }
        foreach (var (mode, modeValue) in value?.Modes ?? new Dictionary<string, string>())
        {
            var allowed = (mode == ThemeModes.Dark && definition.AllowsDark) || (mode == ThemeModes.HighContrast && definition.AllowsHighContrast);
            if (!allowed)
            {
                errors.Add($"value.{mode}", "validation.theme.mode_not_allowed");
            }
            else if (TokenSchema.Check(definition, modeValue) is { } modeError)
            {
                errors.Add($"value.{mode}", modeError);
            }
        }
        var d = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (d is { Length: > MaxExtensionDescription })
        {
            errors.Add("description", "validation.theme.extension_description");
        }
        return (t, value ?? new TokenValue(null), d);
    }

    private async Task<ExtensionRow> ExtensionForAsync(Guid id, string permission, CancellationToken ct)
    {
        var row = (await ExtensionRowsAsync(ct)).SingleOrDefault(r => r.Id == id) ?? throw AppException.NotFound();
        await ScopeAsync(ScopeName(row.Scope), permission, ct);
        return row;
    }

    private async Task<IReadOnlyList<ExtensionRow>> ExtensionRowsAsync(CancellationToken ct) =>
        await session.QueryAsync(ExtensionSelect, null,
            r => new ExtensionRow(r.GetGuid(0), r.Get<Guid?>("scope_org_unit_id"), r.GetString(2), r.GetString(3),
                JsonSerializer.Deserialize<TokenValue>(r.GetString(4), JsonDefaults.Options)!, r.Get<string>("description"), r.GetString(6),
                r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8), r.Get<Guid?>("reviewed_by"),
                r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.Get<string>("review_note")), ct);

    private static TokenExtensionView ExtensionView(ExtensionRow r, bool inherited) =>
        new(r.Id, ScopeName(r.Scope), r.Key, ExtensionToken(r.Key), ExtensionCssName(r.Key), r.Type, r.Value, r.Description, r.State,
            r.ProposedBy, r.ProposedAt, r.ReviewedBy, r.ReviewedAt, r.ReviewNote, inherited);

    [GeneratedRegex("^[a-z][a-z0-9_]{1,31}$")]
    private static partial Regex ExtensionKey();
}
