using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Experience.Templates;

/// <summary>Pages per template and template version, and how many of them are published (spec 021 T007, spec 032).</summary>
internal sealed class TemplateUsageSource(IDbSession session) : ITemplateUsage
{
    public async Task<IReadOnlyList<TemplateUsage>> UsageAsync(CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select template_id, template_version, count(*)::int, count(*) filter (where current_release is not null)::int
            from experience.page group by template_id, template_version order by template_id, template_version
            """, null, r => new TemplateUsage(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)), ct);

    public async Task<IReadOnlyList<PageInventoryItem>> PagesAsync(CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select p.id, p.template_id, p.template_version, coalesce(r.definition->'layout'->>'id', d.definition->'layout'->>'id'), p.org_unit_id, p.current_release
            from experience.page p
            left join experience.page_release r on r.tenant_id = p.tenant_id and r.page_id = p.id and r.release = p.current_release
            left join experience.page_draft d on d.tenant_id = p.tenant_id and d.page_id = p.id
            order by p.template_id, p.id
            """, null, r => new PageInventoryItem(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetInt32(5)), ct);
}
