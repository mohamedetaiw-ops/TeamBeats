using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Themes;

public sealed record TenantDesignRequest(string? Key, string? Base, IReadOnlyDictionary<string, string>? Names, string? Description,
    IReadOnlyDictionary<string, TokenValue>? Tokens);

public sealed record TenantDesignReview(bool Approve, string? Note);

public sealed record TenantDesignApply(Guid? BrandId);

/// <param name="Available">Whether the package lets this tenant make designs; the list stays readable either way.</param>
public sealed record TenantDesignList(bool Available, int Limit, IReadOnlyList<TenantDesignView> Items);

/// <param name="Findings">The contrast findings of the base design with these values, in every mode.</param>
public sealed record TenantDesignView(Guid Id, string Key, string Base, IReadOnlyDictionary<string, string> Names, string? Description,
    IReadOnlyDictionary<string, TokenValue> Tokens, string State, Guid SavedBy, DateTimeOffset SavedAt, Guid? ProposedBy, DateTimeOffset? ProposedAt,
    Guid? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNote, IReadOnlyList<ContrastFinding> Findings);

/// <summary>
/// Tenant designs (spec 036 R3, DLS-120 to DLS-122). A theme editor derives a named design from one of the four designs:
/// the base design and typed token values on top of it. Proposing runs the contrast checks of spec 033 on the result; a
/// person who may publish themes, other than the proposer, approves it. An approved design is applied to a brand by
/// writing the base design and its values into the brand's draft, so it goes live through the theme lifecycle like any
/// brand change and the theme engine needs no layer of its own. Free CSS never enters: every value is a schema token.
/// </summary>
public sealed partial class ThemeService
{
    public const int MaxDesigns = 10;
    private const int MaxDesignName = 60;
    private const int MaxDesignDescription = 200;

    public static readonly IReadOnlyList<string> DesignLanguages = ["en", "ar"];

    private sealed record DesignRow(Guid Id, string Key, string Base, Dictionary<string, string> Names, string? Description,
        Dictionary<string, TokenValue> Tokens, string State, Guid SavedBy, DateTimeOffset SavedAt, Guid? ProposedBy, DateTimeOffset? ProposedAt,
        Guid? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNote);

    [GeneratedRegex("^[a-z][a-z0-9_]{1,23}$")]
    private static partial Regex DesignKey();

    public async Task<TenantDesignList> ListDesignsAsync(CancellationToken ct)
    {
        await ScopeAsync(TenantScope, Permissions.ThemeEdit, ct, alternative: Permissions.ThemePublish);
        var rows = await DesignRowsAsync(null, ct);
        return new TenantDesignList(await entitlements.IsEnabledAsync(Capabilities.ExperienceTokenExtensions, ct), MaxDesigns,
            rows.Select(DesignView).ToList());
    }

    public async Task<TenantDesignView> GetDesignAsync(Guid id, CancellationToken ct)
    {
        await ScopeAsync(TenantScope, Permissions.ThemeEdit, ct, alternative: Permissions.ThemePublish);
        return DesignView(await DesignAsync(id, false, ct));
    }

    public async Task<TenantDesignView> CreateDesignAsync(TenantDesignRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.ExperienceTokenExtensions, ct);
        var errors = new ValidationErrors();
        var key = request.Key?.Trim() ?? "";
        if (!DesignKey().IsMatch(key))
        {
            errors.Add("key", "validation.theme.design_key");
        }
        var (@base, names, description, tokens) = ValidateDesign(request, errors);
        errors.ThrowIfAny();
        ThrowIfCycles(tokens);

        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('experience.tenant_design:' || @t::text))", new { t = context.RequireTenant() }, ct);
        var rows = await DesignRowsAsync(null, ct);
        if (rows.Any(r => r.Key == key))
        {
            throw AppException.Conflict("conflict.theme.design_exists");
        }
        if (rows.Count(r => r.State != "retired") >= MaxDesigns)
        {
            throw AppException.Validation("key", "validation.theme.design_limit");
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into experience.tenant_design (tenant_id, id, key, base, names, description, tokens, state, saved_by, saved_at)
            values (platform.current_tenant_id(), @id, @key, @base, @names, @description, @tokens, 'draft', @user, @now)
            """, new { id, key, @base, names = Jsonb.From(names), description, tokens = Jsonb.From(tokens), user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeDesignSaved, "tenant_design", id.ToString(),
            Changes: new { key, @base, tokens = tokens.Keys.Order(StringComparer.Ordinal).ToList() }), ct);
        return DesignView(await DesignAsync(id, false, ct));
    }

    /// <summary>Changes a design. A proposed, approved or rejected one goes back to draft; brands that applied it keep their values.</summary>
    public async Task<TenantDesignView> UpdateDesignAsync(Guid id, TenantDesignRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.ExperienceTokenExtensions, ct);
        var row = await DesignAsync(id, true, ct);
        if (row.State == "retired")
        {
            throw AppException.Conflict("conflict.theme.design_retired");
        }
        var errors = new ValidationErrors();
        var (@base, names, description, tokens) = ValidateDesign(request, errors);
        errors.ThrowIfAny();
        ThrowIfCycles(tokens);
        await session.ExecAsync(
            """
            update experience.tenant_design set base = @base, names = @names, description = @description, tokens = @tokens, state = 'draft',
              saved_by = @user, saved_at = @now, proposed_by = null, proposed_at = null, reviewed_by = null, reviewed_at = null, review_note = null
            where id = @id
            """, new { id, @base, names = Jsonb.From(names), description, tokens = Jsonb.From(tokens), user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeDesignSaved, "tenant_design", id.ToString(),
            Changes: new { key = row.Key, @base, from = row.State, tokens = tokens.Keys.Order(StringComparer.Ordinal).ToList() }), ct);
        return DesignView(await DesignAsync(id, false, ct));
    }

    /// <summary>Puts a draft up for review. A blocking contrast or text size finding in any mode refuses it.</summary>
    public async Task<TenantDesignView> ProposeDesignAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.ExperienceTokenExtensions, ct);
        var row = await DesignAsync(id, true, ct);
        if (row.State is not ("draft" or "rejected"))
        {
            throw AppException.Conflict("conflict.theme.design_not_draft");
        }
        var findings = DesignFindings(row.Base, row.Tokens);
        if (findings.Any(f => f.Severity == ThemeEngine.Blocking))
        {
            throw new AppException(ProblemCodes.ValidationFailed, 422, "validation.theme.design_contrast", null,
                new Dictionary<string, object?> { ["findings"] = findings, ["field"] = "tokens" });
        }
        await session.ExecAsync(
            """
            update experience.tenant_design set state = 'proposed', proposed_by = @user, proposed_at = @now,
              reviewed_by = null, reviewed_at = null, review_note = null
            where id = @id
            """, new { id, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeDesignProposed, "tenant_design", id.ToString(), Changes: new { key = row.Key, @base = row.Base }), ct);
        return DesignView(await DesignAsync(id, false, ct));
    }

    /// <summary>Approves or rejects a proposal. The reviewer publishes themes for the tenant and did not propose it.</summary>
    public async Task<TenantDesignView> ReviewDesignAsync(Guid id, TenantDesignReview review, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemePublish, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.ExperienceTokenExtensions, ct);
        var row = await DesignAsync(id, true, ct);
        if (row.State != "proposed")
        {
            throw AppException.Conflict("conflict.theme.design_not_proposed");
        }
        if (row.ProposedBy == context.RequireUser())
        {
            throw AppException.Denied("authz.denied.design_own_proposal");
        }
        var note = string.IsNullOrWhiteSpace(review.Note) ? null : review.Note.Trim();
        if (note is { Length: > MaxReasonLength })
        {
            throw AppException.Validation("note", "validation.theme.reason_too_long");
        }
        var state = review.Approve ? "approved" : "rejected";
        await session.ExecAsync(
            "update experience.tenant_design set state = @state, reviewed_by = @user, reviewed_at = @now, review_note = @note where id = @id",
            new { id, state, user = context.RequireUser(), now = clock.UtcNow, note }, ct);
        await audit.WriteAsync(new AuditEntry(review.Approve ? AuditActions.ThemeDesignApproved : AuditActions.ThemeDesignRejected,
            "tenant_design", id.ToString(), Changes: new { key = row.Key, note }), ct);
        return DesignView(await DesignAsync(id, false, ct));
    }

    /// <summary>Stops a design being applied; brands that applied it keep their values. Needs the right to publish themes.</summary>
    public async Task<TenantDesignView> RetireDesignAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemePublish, AuthzTarget.Tenant, ct);
        var row = await DesignAsync(id, true, ct);
        if (row.State == "retired")
        {
            return DesignView(row);
        }
        await session.ExecAsync("update experience.tenant_design set state = 'retired' where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeDesignRetired, "tenant_design", id.ToString(), Changes: new { key = row.Key, from = row.State }), ct);
        return DesignView(await DesignAsync(id, false, ct));
    }

    /// <summary>
    /// Writes an approved design into a brand's draft: the base design and the design's values replace the brand's tokens
    /// (spec 036 section 6), the name, assets and contrast policy stay. The draft then publishes through the lifecycle.
    /// </summary>
    public async Task<BrandDraftView> ApplyDesignAsync(Guid id, TenantDesignApply request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.ExperienceTokenExtensions, ct);
        if (request.BrandId is not { } brandId)
        {
            throw AppException.Validation("brandId", "validation.theme.design_brand");
        }
        var row = await DesignAsync(id, false, ct);
        if (row.State != "approved")
        {
            throw AppException.Conflict("conflict.theme.design_not_approved");
        }
        var draft = await GetDraftAsync(brandId, ct);
        var tokens = new Dictionary<string, TokenValue>(row.Tokens) { [DesignPresets.PresetToken] = new(row.Base) };
        var saved = await SaveDraftAsync(brandId, new BrandRequest(draft.Name, tokens, draft.Assets, draft.StrictContrast), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeDesignApplied, "tenant_design", id.ToString(),
            Changes: new { key = row.Key, brand = brandId }), ct);
        return saved;
    }

    private (string Base, Dictionary<string, string> Names, string? Description, Dictionary<string, TokenValue> Tokens) ValidateDesign(
        TenantDesignRequest request, ValidationErrors errors)
    {
        var @base = request.Base?.Trim() ?? "";
        if (!DesignPresets.Keys.Contains(@base))
        {
            errors.Add("base", "validation.theme.design_base");
        }
        var names = new Dictionary<string, string>();
        foreach (var language in DesignLanguages)
        {
            var name = request.Names?.GetValueOrDefault(language)?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxDesignName)
            {
                errors.Add("names." + language, "validation.theme.design_name");
            }
            else
            {
                names[language] = name;
            }
        }
        if (request.Names?.Keys.Any(k => !DesignLanguages.Contains(k)) == true)
        {
            errors.Add("names", "validation.theme.design_name");
        }
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > MaxDesignDescription })
        {
            errors.Add("description", "validation.theme.design_description");
        }
        var tokens = new Dictionary<string, TokenValue>(request.Tokens ?? new Dictionary<string, TokenValue>());
        if (tokens.ContainsKey(DesignPresets.PresetToken))
        {
            errors.Add("tokens." + DesignPresets.PresetToken, "validation.theme.design_token");
            tokens.Remove(DesignPresets.PresetToken);
        }
        TokenSchema.Validate(tokens, errors);
        return (@base, names, description, tokens);
    }

    /// <summary>The base design with the values as one brand layer, checked in every mode and density.</summary>
    private static IReadOnlyList<ContrastFinding> DesignFindings(string @base, Dictionary<string, TokenValue> tokens)
    {
        var layer = new Dictionary<string, TokenValue>(tokens) { [DesignPresets.PresetToken] = new(@base) };
        return ThemeEngine.Check(ThemeEngine.Resolve([new ThemeLayer(0, null, "brand", null, null, layer)], new Dictionary<string, (int, Guid?)>()), false);
    }

    private async Task<DesignRow> DesignAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        (await DesignRowsAsync(id, ct, forUpdate)).SingleOrDefault() ?? throw AppException.NotFound();

    private Task<List<DesignRow>> DesignRowsAsync(Guid? id, CancellationToken ct, bool forUpdate = false) =>
        session.QueryAsync(
            "select id, key, base, names::text, description, tokens::text, state, saved_by, saved_at, proposed_by, proposed_at, reviewed_by, reviewed_at, review_note "
            + "from experience.tenant_design" + (id is null ? " order by saved_at, key" : " where id = @id") + (forUpdate ? " for update" : ""),
            new { id },
            r => new DesignRow(r.GetGuid(0), r.GetString(1), r.GetString(2),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3), JsonDefaults.Options)!, r.Get<string>("description"),
                Tokens(r.GetString(5)), r.GetString(6), r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8), r.Get<Guid?>("proposed_by"),
                r.Get<DateTimeOffset?>("proposed_at"), r.Get<Guid?>("reviewed_by"), r.Get<DateTimeOffset?>("reviewed_at"), r.Get<string>("review_note")), ct);

    private static TenantDesignView DesignView(DesignRow r) =>
        new(r.Id, r.Key, r.Base, r.Names, r.Description, r.Tokens, r.State, r.SavedBy, r.SavedAt, r.ProposedBy, r.ProposedAt, r.ReviewedBy,
            r.ReviewedAt, r.ReviewNote, DesignFindings(r.Base, r.Tokens));
}
