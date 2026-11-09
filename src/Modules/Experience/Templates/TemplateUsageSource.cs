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
}
