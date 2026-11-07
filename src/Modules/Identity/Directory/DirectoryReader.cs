using System.Text.Json;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Identity.Directory;

/// <summary>Reads the directory mirror kept by <see cref="DirectorySyncService"/>. Users disabled by the directory are left out.</summary>
public sealed class DirectoryReader(IDbSession session) : IDirectoryReader
{
    public async Task<DirectorySnapshot?> ReadAsync(CancellationToken ct = default)
    {
        var connectionId = await session.ScalarAsync<Guid?>("select connection_id from identity.directory_connection order by created_at limit 1", null, ct);
        if (connectionId is null)
        {
            return null;
        }

        var containers = await session.QueryAsync(
            "select directory_id, kind, display_name, members from identity.directory_group where connection_id = @c order by directory_id",
            new { c = connectionId }, r => (Container: new DirectoryContainer(r.GetString(0), r.GetString(1), r.Get<string>("display_name")), Members: r.GetFieldValue<string[]>(3)), ct);
        var memberOf = new Dictionary<string, (List<string> Units, List<string> Groups)>(StringComparer.Ordinal);
        foreach (var (container, members) in containers)
        {
            foreach (var m in members)
            {
                if (!memberOf.TryGetValue(m, out var lists))
                {
                    memberOf[m] = lists = ([], []);
                }
                (container.Kind == MirrorGroup.AdminUnitKind ? lists.Units : lists.Groups).Add(container.Id);
            }
        }

        var people = await session.QueryAsync(
            """
            select l.user_id, l.directory_id, d.attributes::text
            from identity.directory_user_link l join identity.directory_user d on d.connection_id = l.connection_id and d.directory_id = l.directory_id
            where l.connection_id = @c and not l.disabled_by_directory and d.account_enabled
            order by l.directory_id
            """, new { c = connectionId }, r =>
            {
                var a = JsonSerializer.Deserialize<MirrorAttributes>(r.GetString(2), JsonDefaults.Options)!;
                var id = r.GetString(1);
                memberOf.TryGetValue(id, out var lists);
                var profile = new DirectoryProfile(a.DisplayName, a.Email, a.JobTitle, a.Department, a.Company, a.Office, a.WorkPhone,
                    a.MobilePhone, a.PreferredLanguage, a.EmployeeId, HireDate(a.HireDate), a.Manager);
                return new DirectoryPerson(r.GetGuid(0), id, a.Company, a.Department, a.Division, lists.Units ?? [], lists.Groups ?? [], profile);
            }, ct);
        return new DirectorySnapshot(connectionId.Value, people, containers.Select(c => c.Container).ToList());
    }

    /// <summary>Graph reports the hire date as a date and time; only the date is kept.</summary>
    private static DateOnly? HireDate(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? DateOnly.FromDateTime(d.UtcDateTime) : null;
}
