using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Sandbox;

/// <summary>A new version of a tenant component: its manifest subset and its one HTML document.</summary>
public sealed record TenantComponentRequest(string? Id, IReadOnlyDictionary<string, string>? Names, IReadOnlyList<string>? Hosts,
    IReadOnlyList<string>? Context, IReadOnlyList<string>? Events, int? Height, string? Document);

public sealed record TenantComponentVersionView(int Version, string Status, string Sha256, int Size, IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Context, IReadOnlyList<string> Events, int Height, DateTimeOffset SubmittedAt, DateTimeOffset? ReviewedAt, string? ReviewNote);

/// <summary>A tenant component with its versions, newest first. <paramref name="Live"/> is the newest approved version.</summary>
public sealed record TenantComponentView(string Id, IReadOnlyDictionary<string, string> Names, bool Enabled, int? Live,
    IReadOnlyList<TenantComponentVersionView> Versions);

public sealed record TenantComponentsView(bool Available, bool CanManage, IReadOnlyList<TenantComponentView> Components);

public sealed record TenantComponentEnablement(bool? Enabled);

public sealed record ComponentReviewRequest(bool? Approve, string? Note);

/// <summary>One version waiting for the platform's security review.</summary>
public sealed record ComponentReviewItem(Guid TenantId, string TenantKey, string ComponentId, int Version, string Sha256, int Size,
    IReadOnlyList<string> Hosts, IReadOnlyList<string> Context, IReadOnlyList<string> Events, DateTimeOffset SubmittedAt);

/// <summary>What a page needs to frame a tenant component: the live version and its declared contract.</summary>
public sealed record SandboxRuntime(string Id, int Version, IReadOnlyDictionary<string, string> Names, IReadOnlyList<string> Context,
    IReadOnlyList<string> Events, int Height);

public static class TenantComponentStatus
{
    public const string InReview = "in_review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>
/// Tenant-built components (spec 032 R3 FR-031, ADR-0012 point 6) under <c>experience.custom_components</c>. A tenant
/// administrator with <c>component.enable</c> for the whole tenant submits immutable versions: a manifest subset (names,
/// declared HTTPS hosts, context keys from the approved list, published events, height) and one HTML document. A platform
/// operator reviews each version; only an approved one can be enabled, and only an enabled component with an approved
/// version is framed on pages, through the platform component <see cref="HostComponent"/>. The code never runs in the
/// page: it is served with its own sandbox policy (<see cref="SandboxPolicy"/>).
/// </summary>
public sealed partial class TenantComponents(IDbSession session, TenantContextAccessor context, IAuthorizationService authz,
    IEntitlementService entitlements, IAuditWriter audit, IClock clock)
{
    /// <summary>The platform component that frames a tenant component; its <c>component</c> property names which one.</summary>
    public const string HostComponent = "sandbox.tenant_component";
    public const string HostReference = HostComponent + "@1";
    public const int MaxDocumentBytes = 256 * 1024;
    public const int MaxHosts = 5;
    public const int MaxEvents = 10;
    public const int MaxPerPage = 3;
    public const int DefaultHeight = 320;
    public const int MaxNameLength = 80;

    [GeneratedRegex("^[a-z][a-z0-9-]{2,39}$")]
    private static partial Regex Slug();

    /// <summary>An HTTPS origin with a host name, no path, no wildcard, no credentials.</summary>
    [GeneratedRegex(@"^https://([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}(:\d{1,5})?$")]
    private static partial Regex HttpsOrigin();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,39}$")]
    private static partial Regex EventName();

    private async Task<bool> MayManageAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.ComponentEnable, AuthzTarget.Tenant, ct)).Allowed;

    private async Task DemandManagerAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceCustomComponents, ct);
        context.RequireUser();
        if (!await MayManageAsync(ct))
        {
            throw AppException.Denied();
        }
    }

    private static IReadOnlyDictionary<string, string> Names(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    private async Task<List<(TenantComponentVersionView View, string ComponentId)>> VersionsAsync(string? componentId, CancellationToken ct) =>
        await session.QueryAsync(
            """
            select component_id, version, status, sha256, octet_length(document), hosts, context, events, height, submitted_at, reviewed_at, review_note
            from experience.tenant_component_version where (@id::text is null or component_id = @id) order by component_id, version desc
            """, new { id = componentId },
            r => (new TenantComponentVersionView(r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetFieldValue<string[]>(5),
                r.GetFieldValue<string[]>(6), r.GetFieldValue<string[]>(7), r.GetInt32(8), r.GetFieldValue<DateTimeOffset>(9),
                r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetString(11)), r.GetString(0)), ct);

    public async Task<TenantComponentsView> ListAsync(CancellationToken ct)
    {
        context.RequireUser();
        var available = await entitlements.IsEnabledAsync(Capabilities.ExperienceCustomComponents, ct);
        var canManage = await MayManageAsync(ct);
        if (!canManage)
        {
            throw AppException.Denied();
        }
        if (!available)
        {
            return new TenantComponentsView(false, false, []);
        }
        var versions = (await VersionsAsync(null, ct)).ToLookup(v => v.ComponentId, v => v.View);
        var components = await session.QueryAsync("select id, names::text, enabled from experience.tenant_component order by id", null,
            r => (Id: r.GetString(0), Names: Names(r.GetString(1)), Enabled: r.GetBoolean(2)), ct);
        return new TenantComponentsView(true, true, [.. components.Select(c =>
        {
            var list = versions[c.Id].ToList();
            return new TenantComponentView(c.Id, c.Names, c.Enabled, list.FirstOrDefault(v => v.Status == TenantComponentStatus.Approved)?.Version, list);
        })]);
    }

    /// <summary>Submits a new version for review, creating the component on its first version. Audited as <c>component.submitted</c>.</summary>
    public async Task<TenantComponentView> SubmitAsync(TenantComponentRequest request, CancellationToken ct)
    {
        await DemandManagerAsync(ct);
        var errors = new ValidationErrors();
        var id = request.Id?.Trim() ?? "";
        if (!Slug().IsMatch(id))
        {
            errors.Add("id", "validation.experience.sandbox_id");
        }
        var names = (request.Names ?? new Dictionary<string, string>()).ToDictionary(n => n.Key, n => n.Value?.Trim() ?? "", StringComparer.Ordinal);
        if (names.Keys.Any(k => k is not ("en" or "ar")) || !names.TryGetValue("en", out var en) || !names.TryGetValue("ar", out var ar)
            || en.Length is 0 or > MaxNameLength || ar.Length is 0 or > MaxNameLength)
        {
            errors.Add("names", "validation.experience.sandbox_names");
        }
        var hosts = (request.Hosts ?? []).Select(h => h.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (hosts.Count > MaxHosts || hosts.Any(h => !HttpsOrigin().IsMatch(h)))
        {
            errors.Add("hosts", "validation.experience.sandbox_hosts");
        }
        var keys = (request.Context ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Any(k => !ComponentContext.Approved.Contains(k)))
        {
            errors.Add("context", "validation.experience.sandbox_context");
        }
        var events = (request.Events ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (events.Count > MaxEvents || events.Any(e => !EventName().IsMatch(e)))
        {
            errors.Add("events", "validation.experience.sandbox_events");
        }
        var height = request.Height ?? DefaultHeight;
        if (height is < 80 or > 1200)
        {
            errors.Add("height", "validation.experience.sandbox_height");
        }
        var document = request.Document ?? "";
        var bytes = Encoding.UTF8.GetByteCount(document);
        if (bytes == 0 || bytes > MaxDocumentBytes)
        {
            errors.Add("document", "validation.experience.sandbox_document");
        }
        errors.ThrowIfAny();
        var user = context.RequireUser();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into experience.tenant_component (tenant_id, id, names, enabled, created_by, created_at, changed_at)
            values (platform.current_tenant_id(), @id, @names::jsonb, false, @user, @now, @now)
            on conflict (tenant_id, id) do update set names = excluded.names, changed_at = excluded.changed_at
            """, new { id, names = JsonSerializer.Serialize(names), user, now }, ct);
        var version = await session.ScalarAsync<int>(
            "select coalesce(max(version), 0) + 1 from experience.tenant_component_version where component_id = @id", new { id }, ct);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
        await session.ExecAsync(
            """
            insert into experience.tenant_component_version (tenant_id, component_id, version, hosts, context, events, height, document, sha256, status,
              submitted_by, submitted_at)
            values (platform.current_tenant_id(), @id, @version, @hosts, @keys, @events, @height, @document, @sha, 'in_review', @user, @now)
            """, new { id, version, hosts = hosts.ToArray(), keys = keys.ToArray(), events = events.ToArray(), height, document, sha, user, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ComponentSubmitted, "tenant_component", $"{id}@{version}",
            Changes: new { sha256 = sha, size = bytes, hosts, context = keys, events }), ct);
        return await ViewAsync(id, ct);
    }

    private async Task<TenantComponentView> ViewAsync(string id, CancellationToken ct) =>
        (await ListAsync(ct)).Components.FirstOrDefault(c => c.Id == id) ?? throw AppException.NotFound();

    /// <summary>Turns the component on or off for pages. On needs an approved version. Audited as <c>component.enabled</c> or <c>component.disabled</c>.</summary>
    public async Task<TenantComponentView> EnableAsync(string id, TenantComponentEnablement request, CancellationToken ct)
    {
        await DemandManagerAsync(ct);
        if (request.Enabled is not { } enabled)
        {
            throw AppException.Validation("enabled", "validation.experience.sandbox_enabled");
        }
        var current = await ViewAsync(id, ct);
        if (enabled && current.Live is null)
        {
            throw AppException.Validation("enabled", "validation.experience.sandbox_not_approved");
        }
        await session.ExecAsync("update experience.tenant_component set enabled = @enabled, changed_at = @now where id = @id",
            new { id, enabled, now = clock.UtcNow }, ct);
        if (current.Enabled != enabled)
        {
            await audit.WriteAsync(new AuditEntry(enabled ? AuditActions.ComponentEnabled : AuditActions.ComponentDisabled, "tenant_component", id,
                Changes: new { enabled = new { before = current.Enabled, after = enabled } }), ct);
        }
        return await ViewAsync(id, ct);
    }

    /// <summary>The live version of an enabled tenant component, for a page in this tenant, or null.</summary>
    public async Task<SandboxRuntime?> RuntimeAsync(string id, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.ExperienceCustomComponents, ct))
        {
            return null;
        }
        var rows = await session.QueryAsync(
            """
            select c.id, v.version, c.names::text, v.context, v.events, v.height
            from experience.tenant_component c
            join experience.tenant_component_version v on v.tenant_id = c.tenant_id and v.component_id = c.id
            where c.id = @id and c.enabled and v.status = 'approved'
            order by v.version desc limit 1
            """, new { id },
            r => new SandboxRuntime(r.GetString(0), r.GetInt32(1), Names(r.GetString(2)), r.GetFieldValue<string[]>(3), r.GetFieldValue<string[]>(4),
                r.GetInt32(5)), ct);
        return rows.FirstOrDefault();
    }

    /// <summary>
    /// The document of a version with its declared hosts: the live version of an enabled component for any member, or any
    /// version for a manager (to try it before review). Anything else is not found, so nothing shows that it exists.
    /// </summary>
    public async Task<(string Document, IReadOnlyList<string> Hosts)> DocumentAsync(string id, int version, CancellationToken ct)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.ExperienceCustomComponents, ct);
        var live = await RuntimeAsync(id, ct);
        if (live?.Version != version && !await MayManageAsync(ct))
        {
            throw AppException.NotFound();
        }
        var rows = await session.QueryAsync("select document, hosts from experience.tenant_component_version where component_id = @id and version = @version",
            new { id, version }, r => (r.GetString(0), (IReadOnlyList<string>)r.GetFieldValue<string[]>(1)), ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }

    /// <summary>Versions of this tenant waiting for review, for the platform queue (run in the tenant's context).</summary>
    public async Task<IReadOnlyList<ComponentReviewItem>> PendingAsync(TenantInfo tenant, CancellationToken ct) =>
        (await VersionsAsync(null, ct)).Where(v => v.View.Status == TenantComponentStatus.InReview)
            .Select(v => new ComponentReviewItem(tenant.Id, tenant.Key, v.ComponentId, v.View.Version, v.View.Sha256, v.View.Size, v.View.Hosts,
                v.View.Context, v.View.Events, v.View.SubmittedAt)).ToList();

    /// <summary>The document of a version under review, for the operator who reviews it (run in the tenant's context).</summary>
    public async Task<string> ReviewDocumentAsync(string id, int version, CancellationToken ct)
    {
        RequireOperator();
        var rows = await session.QueryAsync("select document from experience.tenant_component_version where component_id = @id and version = @version",
            new { id, version }, r => r.GetString(0), ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }

    private Guid RequireOperator() => context.ActorType == ActorType.PlatformOperator && context.ActorId is { } op ? op : throw AppException.Unauthenticated();

    /// <summary>
    /// A platform operator approves or rejects a version under review (run in the tenant's context), audited in the
    /// tenant and on the platform as <c>component.reviewed</c>. A rejection needs a reason; a reviewed version is final.
    /// </summary>
    public async Task ReviewAsync(Guid tenantId, string id, int version, ComponentReviewRequest request, CancellationToken ct)
    {
        var reviewer = RequireOperator();
        var note = request.Note?.Trim();
        var errors = new ValidationErrors();
        if (request.Approve is null)
        {
            errors.Add("approve", "validation.experience.sandbox_review_choice");
        }
        if (request.Approve == false && note is not { Length: >= 10 and <= 500 } || note is { Length: > 500 })
        {
            errors.Add("note", "validation.experience.sandbox_review_note");
        }
        errors.ThrowIfAny();
        var status = request.Approve == true ? TenantComponentStatus.Approved : TenantComponentStatus.Rejected;
        var changed = await session.ExecAsync(
            """
            update experience.tenant_component_version set status = @status, reviewed_by = @reviewer, reviewed_at = @now, review_note = @note
            where component_id = @id and version = @version and status = 'in_review'
            """, new { id, version, status, reviewer, now = clock.UtcNow, note }, ct);
        if (changed == 0)
        {
            var exists = await session.ScalarAsync<bool>("select exists (select 1 from experience.tenant_component_version where component_id = @id and version = @version)",
                new { id, version }, ct);
            throw exists ? AppException.Conflict("conflict.experience.sandbox_reviewed") : AppException.NotFound();
        }
        var entry = new AuditEntry(AuditActions.ComponentReviewed, "tenant_component", $"{id}@{version}", Changes: new { status, note });
        await audit.WriteAsync(entry, ct);
        await audit.WritePlatformAsync(entry, tenantId, ct);
    }

    /// <summary>
    /// Page checks for framed tenant components: at most <see cref="MaxPerPage"/> per page, and each must name an enabled
    /// component with an approved version (blocking at publish, a warning while saving).
    /// </summary>
    public async Task<List<Finding>> FindingsAsync(PageDefinition page, ValidationMode mode, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var framed = page.Regions.SelectMany(r => r.Placements.Select(p => (Region: r.Id, Placement: p)))
            .Where(x => x.Placement.ComponentRef == HostReference).ToList();
        if (framed.Count > MaxPerPage)
        {
            foreach (var extra in framed.Skip(MaxPerPage))
            {
                findings.Add(new Finding("validation.page.sandbox_limit", Severities.Blocking, extra.Region, extra.Placement.Id));
            }
        }
        foreach (var (region, placement) in framed)
        {
            var id = placement.Props is { ValueKind: JsonValueKind.Object } props && props.TryGetProperty("component", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            if (id is null || await RuntimeAsync(id, ct) is null)
            {
                findings.Add(new Finding("validation.page.sandbox_unavailable", mode == ValidationMode.Publish ? Severities.Blocking : Severities.Warning,
                    region, placement.Id, "component"));
            }
        }
        return findings;
    }
}
