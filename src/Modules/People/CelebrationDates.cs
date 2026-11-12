using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.People;

/// <summary>Joiners and hire dates for spec 023 celebrations. Engagement reads them here and never queries people tables.</summary>
public sealed class CelebrationDates(ProfileStore store) : ICelebrationDates
{
    public async Task<IReadOnlyList<WorkDate>> JoinersAsync(CancellationToken ct = default)
    {
        var settings = await store.SettingsAsync(ct);
        var rows = await store.QueryAsync(
            """
            active and membership_type <> 'guest' and not (newcomer_opt_out and @optOut)
            and coalesce(hire_date, first_seen) >= current_date - @days and coalesce(hire_date, first_seen) <= current_date
            order by coalesce(hire_date, first_seen) desc, user_id limit 200
            """, new { optOut = settings.AllowNewcomerOptOut, days = settings.NewcomerDays }, ct);
        return rows.Select(r => new WorkDate(r.UserId, r.HireDate ?? r.FirstSeen)).ToList();
    }

    public async Task<IReadOnlyList<WorkDate>> HireDatesOnAsync(IReadOnlyCollection<(int Month, int Day)> days, CancellationToken ct = default)
    {
        if (days.Count == 0)
        {
            return [];
        }
        var keys = days.Select(d => d.Month * 100 + d.Day).Distinct().ToArray();
        var rows = await store.QueryAsync(
            """
            active and membership_type <> 'guest' and hire_date is not null
            and (extract(month from hire_date)::int * 100 + extract(day from hire_date)::int) = any(@keys)
            order by hire_date, user_id limit 500
            """, new { keys }, ct);
        return rows.Select(r => new WorkDate(r.UserId, r.HireDate!.Value)).ToList();
    }
}
