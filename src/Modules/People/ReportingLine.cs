using TeamBeats.Messaging;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.People;

/// <summary>
/// The reporting line (spec 010 FR-004, FR-005). Each change closes the current line and opens a new one, so the
/// history keeps the dates of every change, and publishes <see cref="ReportingLineChanged"/>.
/// </summary>
public sealed class ReportingLine(IDbSession session, IClock clock, ITenantContext context, IOutbox outbox) : IReportingLine
{
    /// <summary>Guards the recursive walks against bad data; a real chain is far shorter.</summary>
    public const int MaxDepth = 50;

    public Task<Guid?> ManagerOfAsync(Guid userId, CancellationToken ct = default) =>
        session.ScalarAsync<Guid?>("select manager_user_id from people.reporting_line where user_id = @u and valid_to is null", new { u = userId }, ct);

    public async Task<IReadOnlyList<Guid>> ChainAsync(Guid userId, int levels, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            with recursive chain(manager, depth, path) as (
              select l.manager_user_id, 1, array[l.user_id]
              from people.reporting_line l where l.user_id = @u and l.valid_to is null and l.manager_user_id is not null
              union all
              select l.manager_user_id, c.depth + 1, c.path || c.manager
              from chain c join people.reporting_line l on l.user_id = c.manager and l.valid_to is null
              where l.manager_user_id is not null and not (l.manager_user_id = any(c.path || c.manager)) and c.depth < @n
            )
            select manager from chain order by depth
            """, new { u = userId, n = Math.Clamp(levels, 1, MaxDepth) }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlyList<Guid>> DirectReportsAsync(Guid managerId, CancellationToken ct = default) =>
        await session.QueryAsync("select user_id from people.reporting_line where manager_user_id = @m and valid_to is null",
            new { m = managerId }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlySet<Guid>> AllReportsAsync(Guid managerId, CancellationToken ct = default) =>
        (await ReportsWithLevelAsync(managerId, ct)).Keys.ToHashSet();

    /// <summary>Every report and how many levels below the manager they sit (1 for direct reports).</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> ReportsWithLevelAsync(Guid managerId, CancellationToken ct = default) =>
        (await session.QueryAsync(
            """
            with recursive reports(user_id, depth) as (
              select user_id, 1 from people.reporting_line where manager_user_id = @m and valid_to is null
              union
              select l.user_id, r.depth + 1 from reports r join people.reporting_line l on l.manager_user_id = r.user_id and l.valid_to is null
              where r.depth < @n
            )
            select user_id, min(depth) from reports where user_id <> @m group by user_id
            """, new { m = managerId, n = MaxDepth }, r => (Id: r.GetGuid(0), Depth: r.GetInt32(1)), ct)).ToDictionary(x => x.Id, x => x.Depth);

    public async Task<bool> IsManagerOfAsync(Guid managerId, Guid userId, CancellationToken ct = default) =>
        (await ChainAsync(userId, MaxDepth, ct)).Contains(managerId);

    /// <summary>Current lines of every person who has one.</summary>
    public async Task<IReadOnlyDictionary<Guid, Guid?>> CurrentAsync(CancellationToken ct) =>
        (await session.QueryAsync("select user_id, manager_user_id from people.reporting_line where valid_to is null", null,
            r => (User: r.GetGuid(0), Manager: r.Get<Guid?>("manager_user_id")), ct)).ToDictionary(x => x.User, x => x.Manager);

    /// <summary>Records a new line when it differs from the current one. A person with no line and no manager needs no row.</summary>
    public async Task<bool> SetAsync(Guid userId, Guid? managerId, string source, CancellationToken ct)
    {
        var current = await session.QuerySingleOrDefaultAsync(
            "select id, manager_user_id from people.reporting_line where user_id = @u and valid_to is null for update",
            new { u = userId }, r => (Id: r.GetGuid(0), Manager: r.Get<Guid?>("manager_user_id")), ct);
        if (current == default ? managerId is null : current.Manager == managerId)
        {
            return false;
        }
        var now = clock.UtcNow;
        if (current != default)
        {
            await session.ExecAsync("update people.reporting_line set valid_to = @now where id = @id", new { now, id = current.Id }, ct);
        }
        await session.ExecAsync(
            """
            insert into people.reporting_line (tenant_id, id, user_id, manager_user_id, source, valid_from, changed_by)
            values (platform.current_tenant_id(), @id, @u, @m, @s, @now, @actor)
            """, new { id = Ids.New(), u = userId, m = managerId, s = source, now, actor = context.ActorId }, ct);
        await outbox.EnqueueAsync(new ReportingLineChanged(userId, current == default ? null : current.Manager, managerId, source), ct);
        return true;
    }

    /// <summary>
    /// People whose proposed manager chain loops back to them (US2 scenario 5). The input maps each person to the
    /// manager the directory proposes; anyone missing keeps the manager they have.
    /// </summary>
    public static IReadOnlySet<Guid> FindCycles(IReadOnlyDictionary<Guid, Guid?> proposed)
    {
        var inCycle = new HashSet<Guid>();
        foreach (var start in proposed.Keys)
        {
            var seen = new List<Guid> { start };
            var at = start;
            while (proposed.TryGetValue(at, out var next) && next is { } m && seen.Count <= MaxDepth)
            {
                var index = seen.IndexOf(m);
                if (index >= 0)
                {
                    inCycle.UnionWith(seen.Skip(index));
                    break;
                }
                seen.Add(m);
                at = m;
            }
        }
        return inCycle;
    }
}
