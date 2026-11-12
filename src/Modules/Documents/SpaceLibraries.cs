using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Libraries of project and team spaces (spec 029 FR-006). Spaces has already decided the caller may create or change
/// the space, so no <c>doc.manage</c> is asked for here; who uses the library afterwards is decided by the space roles
/// through the library's container. The name is stored for every enabled language, as the space has one name.
/// </summary>
public sealed class SpaceLibraries(IDbSession session, ITenantContext context, IEntitlementService entitlements, ITenantSettingsReader tenantSettings,
    IAuditWriter audit, IClock clock) : ISpaceLibraries
{
    private async Task<Jsonb> NamesAsync(string name, CancellationToken ct)
    {
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        var names = locales.EnabledLocales.Append(locales.DefaultLocale).Distinct().ToDictionary(l => l, _ => name.Trim());
        return new Jsonb(DocJson.Write(names));
    }

    public async Task<Guid?> CreateAsync(Guid spaceId, string name, string classification, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.DocsCore, ct))
        {
            return null;
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into documents.library (tenant_id, id, names, scope, scope_id, org_unit_id, classification_default, created_by)
            values (platform.current_tenant_id(), @id, @names, 'space', @spaceId, null, @classification, @by)
            """, new { id, names = await NamesAsync(name, ct), spaceId, classification, by = context.ActorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocLibraryCreated, Securables.Library, id.ToString(),
            Changes: new { scope = LibraryScopes.Space, scopeId = spaceId, classification }), ct);
        return id;
    }

    public async Task UpdateAsync(Guid libraryId, string name, string classification, CancellationToken ct = default) =>
        await session.ExecAsync(
            """
            update documents.library set names = @names, classification_default = @classification, updated_at = @now, row_version = row_version + 1
            where id = @libraryId and scope = 'space'
            """, new { libraryId, names = await NamesAsync(name, ct), classification, now = clock.UtcNow }, ct);

    public async Task SetArchivedAsync(Guid libraryId, bool archived, CancellationToken ct = default) =>
        await session.ExecAsync(
            "update documents.library set status = @status, updated_at = @now, row_version = row_version + 1 where id = @libraryId and scope = 'space'",
            new { libraryId, status = archived ? "archived" : "active", now = clock.UtcNow }, ct);

    public async Task SetGuestConfidentialAsync(Guid libraryId, bool allowed, CancellationToken ct = default) =>
        await session.ExecAsync(
            "update documents.library set guest_confidential = @allowed, updated_at = @now where id = @libraryId and scope = 'space' and guest_confidential <> @allowed",
            new { libraryId, allowed, now = clock.UtcNow }, ct);
}
