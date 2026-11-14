using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Experience.Pages;

/// <summary>The site home page contract of spec 009 US6 over the page builder.</summary>
public sealed class SitePages(IDbSession session, PageService pages) : ISitePages
{
    public Task<SitePageInfo?> FindAsync(Guid pageId, CancellationToken ct = default) =>
        session.QuerySingleOrDefaultAsync("select id, slug, org_unit_id, current_release is not null from experience.page where id = @pageId", new { pageId },
            r => new SitePageInfo(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetBoolean(3)), ct);

    public async Task<SitePageInfo> CreateAsync(string templateId, string slug, Guid? orgUnitId, CancellationToken ct = default)
    {
        var page = await pages.CreateAsync(new CreatePageRequest(templateId, slug, orgUnitId, null), ct);
        return new SitePageInfo(page.Id, page.Slug, page.OrgUnitId, page.CurrentRelease is not null);
    }
}
