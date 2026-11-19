using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Tenancy;

/// <summary>Active guest memberships and the guests added per week, from the tenant memberships (spec 021 security trends).</summary>
public sealed class GuestMembershipFigures(IDbSession session) : IGuestFigures
{
    public async Task<GuestFigures> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var args = new { from, to };
        var active = await session.ScalarAsync<long>(
            "select count(*) from tenancy.tenant_membership where membership_type = 'guest' and status = 'active'", args, ct);
        var weeks = await session.QueryAsync(
            """
            select to_char(date_trunc('week', created_at at time zone 'UTC'), 'YYYY-MM-DD'), count(*), count(*)
            from tenancy.tenant_membership where membership_type = 'guest' and created_at >= @from and created_at < @to
            group by 1 order by 1
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
        return new GuestFigures(active, weeks);
    }
}
