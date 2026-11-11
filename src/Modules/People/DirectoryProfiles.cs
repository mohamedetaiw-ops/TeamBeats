using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.People;

/// <summary>
/// Applies what directory sync mirrored (spec 010 T003, T005): directory-owned profile values, the location, and the
/// reporting line from the <c>manager</c> attribute. A manager who is not an active member leaves the person with no
/// manager (US2 scenario 2); a cycle keeps the previous line for the people in it and is reported (scenario 5). When
/// the directory is unavailable nothing runs, so profiles keep their last values (FR-022).
/// </summary>
public sealed class DirectoryProfiles(
    IDbSession session,
    IClock clock,
    IDirectoryReader directory,
    ProfileStore store,
    ReportingLine lines,
    IAuditWriter audit,
    IOutbox outbox,
    ILogger<DirectoryProfiles> logger)
{
    /// <summary>Directory attributes a tenant may map to location (US5).</summary>
    public static readonly IReadOnlyList<string> LocationAttributes = ["officeLocation", "department", "companyName"];

    public async Task ApplyAsync(CancellationToken ct)
    {
        var snapshot = await directory.ReadAsync(ct);
        if (snapshot is null)
        {
            return;
        }
        await store.EnsureFreshAsync(ct);
        var settings = await store.SettingsAsync(ct);
        var policies = await store.PoliciesAsync(ct);
        var rows = (await store.GetManyAsync(snapshot.People.Select(p => p.UserId), ct)).ToDictionary(r => r.UserId);
        // When HR owns the hire date (spec 019), the directory's copy does not move it.
        var hireFromHr = policies.First(p => p.Key == ProfileFields.HireDate).Source == FieldSources.Hr;

        var updates = new List<object>();
        var locations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var person in snapshot.People)
        {
            if (!rows.TryGetValue(person.UserId, out var row) || person.Profile is not { } p)
            {
                continue;
            }
            var values = Values(p);
            var rawLocation = settings.LocationAttribute switch
            {
                "department" => p.Department,
                "companyName" => p.Company,
                _ => p.Office,
            };
            var locationKey = SearchText.LocationKey(rawLocation);
            if (locationKey is not null)
            {
                locations.TryAdd(locationKey, rawLocation!.Trim());
            }
            var changed = values.Keys.Union(row.DirectoryValues.Keys)
                .Where(k => values.GetValueOrDefault(k) != row.DirectoryValues.GetValueOrDefault(k)).Order(StringComparer.Ordinal).ToList();
            if (locationKey != row.LocationKey)
            {
                changed.Add("location");
            }
            var hire = hireFromHr ? row.HireDate : p.HireDate;
            if (changed.Count == 0 && row.DirectoryId == person.DirectoryId && row.HireDate == hire)
            {
                continue;
            }
            var updated = row with { DirectoryValues = values, DirectoryId = person.DirectoryId, LocationKey = locationKey, HireDate = hire };
            updates.Add(new
            {
                userId = row.UserId,
                directoryId = person.DirectoryId,
                directoryValues = values,
                hireDate = hire,
                locationKey,
                searchFields = ProfileStore.SearchFields(updated, policies),
            });
            if (changed.Count > 0)
            {
                await outbox.EnqueueAsync(new ProfileUpdated(row.UserId, changed), ct);
            }
        }
        await session.ExecAsync(
            """
            update people.profile p set directory_id = r."directoryId", directory_values = r."directoryValues", hire_date = r."hireDate",
              location_key = r."locationKey", search_fields = r."searchFields", version = p.version + 1, updated_at = now()
            from jsonb_to_recordset(@rows) as r("userId" uuid, "directoryId" text, "directoryValues" jsonb, "hireDate" date, "locationKey" text, "searchFields" jsonb)
            where p.user_id = r."userId"
            """, new { rows = Jsonb.From(updates) }, ct);

        var added = await session.QueryAsync(
            """
            insert into people.location (tenant_id, key, label)
            select platform.current_tenant_id(), r.key, r.label from jsonb_to_recordset(@rows) as r(key text, label text)
            on conflict do nothing returning key
            """, new { rows = Jsonb.From(locations.Select(l => new { key = l.Key, label = l.Value })) }, r => r.GetString(0), ct);
        foreach (var key in added)
        {
            await outbox.EnqueueAsync(new LocationAdded(key), ct);
        }

        await ApplyManagersAsync(snapshot, rows, ct);
        await session.ExecAsync("update people.settings set directory_synced_at = @now", new { now = clock.UtcNow }, ct);
    }

    private async Task ApplyManagersAsync(DirectorySnapshot snapshot, IReadOnlyDictionary<Guid, ProfileRow> rows, CancellationToken ct)
    {
        // Only active members of this tenant can be managers; a directory ID from elsewhere matches nobody.
        var members = snapshot.People.Where(p => rows.TryGetValue(p.UserId, out var r) && r.Active)
            .ToDictionary(p => p.DirectoryId, p => p.UserId, StringComparer.Ordinal);
        var proposed = new Dictionary<Guid, Guid?>();
        foreach (var person in snapshot.People.Where(p => members.ContainsKey(p.DirectoryId)))
        {
            var managerDirectoryId = person.Profile?.ManagerDirectoryId;
            proposed[person.UserId] = managerDirectoryId is { Length: > 0 } m && members.TryGetValue(m, out var manager) && manager != person.UserId ? manager : null;
        }
        var cycles = ReportingLine.FindCycles(proposed);
        if (cycles.Count > 0)
        {
            logger.LogWarning("Directory reporting cycle of {Count} people in connection {Connection}; their previous lines are kept.", cycles.Count, snapshot.ConnectionId);
            await audit.WriteAsync(new AuditEntry(AuditActions.PeopleReportingCycleDetected, "directory_connection", snapshot.ConnectionId.ToString(),
                Changes: new { people = cycles.Order().ToArray() }), ct);
        }
        var current = await lines.CurrentAsync(ct);
        foreach (var (user, manager) in proposed)
        {
            if (cycles.Contains(user) || (current.TryGetValue(user, out var now) ? now == manager : manager is null))
            {
                continue;
            }
            await lines.SetAsync(user, manager, "directory", ct);
        }
    }

    private static Dictionary<string, string> Values(DirectoryProfile p)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[key] = value.Trim();
            }
        }
        Put(ProfileFields.JobTitle, p.JobTitle);
        Put(ProfileFields.Department, p.Department);
        Put(ProfileFields.Office, p.Office);
        Put(ProfileFields.WorkPhone, p.WorkPhone);
        Put(ProfileFields.MobilePhone, p.MobilePhone);
        Put(ProfileFields.PreferredLanguage, p.PreferredLanguage);
        Put(ProfileFields.EmployeeNumber, p.EmployeeId);
        Put(ProfileFields.HireDate, p.HireDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        return values;
    }
}

/// <summary>Runs after every successful directory sync run.</summary>
public sealed class ApplyDirectoryProfiles(DirectoryProfiles profiles) : IMessageHandler<DirectorySynced>
{
    public Task HandleAsync(DirectorySynced message, CancellationToken ct) => profiles.ApplyAsync(ct);
}

/// <summary>Keeps one person's account values current when directory sync renames them or provisioning adds them.</summary>
public sealed class RefreshProfileOnSync(ProfileStore store) : IMessageHandler<UserProfileSynced>, IMessageHandler<UserProvisioned>, IMessageHandler<UserDeprovisioned>
{
    public Task HandleAsync(UserProfileSynced message, CancellationToken ct) => store.RefreshAsync(message.UserId, ct);

    public Task HandleAsync(UserProvisioned message, CancellationToken ct) => store.RefreshAsync(message.UserId, ct);

    public Task HandleAsync(UserDeprovisioned message, CancellationToken ct) => store.RefreshAsync(message.UserId, ct);
}
