using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>One app of the platform's public app gallery (spec 035 R3, T025).</summary>
public sealed record GalleryApp(string ClientId, string Name, string Publisher, string Description, string Homepage, string[] RedirectUris, string[] Scopes,
    string Status, Guid ReviewedBy, DateTimeOffset ReviewedAt)
{
    /// <summary>Gallery apps sign people in with PKCE and keep no secret, so no tenant ever hands a credential to a publisher.</summary>
    public const string Kind = AppKinds.DelegatedPublic;

    public bool Published => Status == "published";
}

/// <summary>A gallery app as a tenant's API administrator sees it: whether it is added, and whether the package covers its scopes.</summary>
public sealed record GalleryAppView(string ClientId, string Name, string Publisher, string Description, string Homepage, IReadOnlyList<string> Scopes,
    Guid? AppId, bool InPackage);

/// <summary>What the platform operator records after the security review of a gallery app.</summary>
public sealed record GalleryAppRequest(string? ClientId, string? Name, string? Publisher, string? Description, string? Homepage,
    IReadOnlyList<string>? RedirectUris, IReadOnlyList<string>? Scopes, string? Status);

public sealed record GalleryAddRequest(string? ClientId);

/// <summary>
/// The public app gallery (spec 035 R3, T025). The platform operator publishes apps after reviewing each publisher's
/// client ID metadata document, recording what the app is and the scopes it may ask for. A tenant's API administrator
/// adds a published app: the tenant's registration is created from the entry and the administrator's consent covers its
/// scopes, so employees connect it without asking an administrator again. The gallery owns the app's name, publisher,
/// scopes and redirect URIs; the tenant keeps its switches and limits and can disable or revoke as for any app.
/// Withdrawing an entry disables every tenant's registration of it at once.
/// </summary>
public sealed class AppGallery(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IClock clock,
    Apps apps,
    Consents consents,
    VerifiedMcpClients verified)
{
    private const string Columns = "client_id, name, publisher, description, homepage, redirect_uris, scopes, status, reviewed_by, reviewed_at";

    private static GalleryApp Read(Npgsql.NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetFieldValue<string[]>(5), r.GetFieldValue<string[]>(6), r.GetString(7), r.GetGuid(8), r.GetFieldValue<DateTimeOffset>(9));

    private Task<GalleryApp?> FindAsync(string clientId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from publicapi.gallery_app where client_id = @clientId", new { clientId }, Read, ct);

    /// <summary>The whole gallery, for the platform operator.</summary>
    public Task<List<GalleryApp>> ListAsync(CancellationToken ct) =>
        session.QueryAsync($"select {Columns} from publicapi.gallery_app order by name, client_id", null, Read, ct);

    /// <summary>The published apps, for the tenant's API page, with the tenant's registration of each if it has one.</summary>
    public async Task<IReadOnlyList<GalleryAppView>> PublishedAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        await authz.DemandAsync(Permissions.ApiView, AuthzTarget.Tenant, ct);
        var entries = await session.QueryAsync($"select {Columns} from publicapi.gallery_app where status = 'published' order by name, client_id", null, Read, ct);
        var added = (await session.QueryAsync("select client_id, id from publicapi.app where gallery", null, r => (Client: r.GetString(0), Id: r.GetGuid(1)), ct))
            .ToDictionary(x => x.Client, x => x.Id, StringComparer.Ordinal);
        var views = new List<GalleryAppView>(entries.Count);
        foreach (var e in entries)
        {
            views.Add(new GalleryAppView(e.ClientId, e.Name, e.Publisher, e.Description, e.Homepage, e.Scopes,
                added.TryGetValue(e.ClientId, out var id) ? id : null, await InPackageAsync(e.Scopes, ct)));
        }
        return views;
    }

    /// <summary>An API administrator adds a published app: the registration, then the admin consent for all its scopes.</summary>
    public async Task<AppDetail> AddAsync(GalleryAddRequest request, CancellationToken ct)
    {
        var clientId = request.ClientId?.Trim() ?? "";
        var entry = VerifiedMcpClients.IsMetadataUrl(clientId) ? await FindAsync(clientId, ct) : null;
        if (entry is not { Published: true })
        {
            await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
            await authz.DemandAsync(Permissions.ApiManage, AuthzTarget.Tenant, ct);
            throw AppException.NotFound();
        }
        var (app, added) = await apps.AddFromGalleryAsync(entry, ct);
        if (!added)
        {
            // Adding an app the tenant already has changes nothing, so an administrator's later consent choices stay.
            return await apps.DetailOfAsync(app, ct);
        }
        var detail = await consents.GrantAdminAsync(app.Id, new AdminConsentRequest(entry.Scopes), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiGalleryAppAdded, "api_app", app.Id.ToString(),
            Changes: new { entry.ClientId, entry.Name, entry.Scopes }), ct);
        return detail;
    }

    /// <summary>Publishes, changes or withdraws a gallery app after review; the platform audit records who did it.</summary>
    public async Task<GalleryApp> PutAsync(GalleryAppRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var clientId = request.ClientId?.Trim() ?? "";
        if (!VerifiedMcpClients.IsMetadataUrl(clientId))
        {
            errors.Add("clientId", "validation.api.verified_client_id");
        }
        else if (await verified.FindAsync(clientId, ct) is not null)
        {
            // One client ID is either an AI client the AI policy allows or a gallery app an API administrator adds, never both.
            errors.Add("clientId", "validation.api.gallery_client_listed");
        }
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 80)
        {
            errors.Add("name", "validation.api.name");
        }
        var publisher = request.Publisher?.Trim() ?? "";
        if (publisher.Length is < 1 or > 80)
        {
            errors.Add("publisher", "validation.api.publisher");
        }
        var description = request.Description?.Trim() ?? "";
        if (description.Length is < 1 or > 500)
        {
            errors.Add("description", "validation.api.gallery_description");
        }
        var homepage = request.Homepage?.Trim() ?? "";
        if (homepage.Length > 300 || !Uri.TryCreate(homepage, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(page.UserInfo))
        {
            errors.Add("homepage", "validation.api.gallery_homepage");
        }
        ApiRules.CheckRedirectUris(request.RedirectUris, GalleryApp.Kind, errors);
        ApiRules.CheckScopes(request.Scopes, GalleryApp.Kind, errors);
        var status = request.Status ?? "published";
        if (status is not ("published" or "withdrawn"))
        {
            errors.Add("status", "validation.api.gallery_status");
        }
        errors.ThrowIfAny();

        var by = context.ActorId ?? throw AppException.Unauthenticated();
        var entry = new GalleryApp(clientId, name, publisher, description, homepage, [.. request.RedirectUris!.Distinct()], [.. request.Scopes!.Distinct()], status,
            by, clock.UtcNow);
        await session.ExecAsync(
            """
            insert into publicapi.gallery_app (client_id, name, publisher, description, homepage, redirect_uris, scopes, status, reviewed_by, reviewed_at)
            values (@ClientId, @Name, @Publisher, @Description, @Homepage, @RedirectUris, @Scopes, @Status, @ReviewedBy, @ReviewedAt)
            on conflict (client_id) do update set name = excluded.name, publisher = excluded.publisher, description = excluded.description,
              homepage = excluded.homepage, redirect_uris = excluded.redirect_uris, scopes = excluded.scopes, status = excluded.status,
              reviewed_by = excluded.reviewed_by, reviewed_at = excluded.reviewed_at
            """, entry, ct);
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.ApiGalleryAppChanged, "gallery_app", clientId,
            Changes: new { entry.Name, entry.Publisher, entry.Homepage, entry.RedirectUris, entry.Scopes, entry.Status }), null, ct);
        return entry;
    }

    private async Task<bool> InPackageAsync(IReadOnlyList<string> scopes, CancellationToken ct)
    {
        foreach (var capability in scopes.Select(ScopeCatalogue.Find).OfType<ScopeDefinition>().SelectMany(s => s.Capabilities).Distinct())
        {
            if (!await entitlements.IsEnabledAsync(capability, ct))
            {
                return false;
            }
        }
        return true;
    }
}
