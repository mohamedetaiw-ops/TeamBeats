using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.People;

/// <summary>
/// Employee number lookup for imports (spec 017 FR-020). The number is read under the source the tenant chose for the
/// field (spec 010 FR-002); inactive people and numbers held by more than one person are left out.
/// </summary>
public sealed class EmployeeNumbers(IDbSession session, ProfileStore store) : IEmployeeNumbers
{
    public async Task<IReadOnlyDictionary<string, Guid>> ResolveAsync(IReadOnlyCollection<string> employeeNumbers, CancellationToken ct = default)
    {
        var wanted = employeeNumbers.Select(n => n.Trim()).Where(n => n.Length is > 0 and <= 100).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0)
        {
            return new Dictionary<string, Guid>();
        }
        await store.EnsureFreshAsync(ct);
        var policy = (await store.PoliciesAsync(ct)).First(p => p.Key == ProfileFields.EmployeeNumber);
        // The column comes from a fixed list, never from input.
        var column = policy.Source switch
        {
            FieldSources.Directory => "directory_values",
            FieldSources.Person => "own_values",
            _ => "admin_values",
        };
        var rows = await session.QueryAsync(
            $"""
            select {column}->>'employeeNumber', user_id from people.profile
            where active and {column}->>'employeeNumber' = any(@wanted)
            """, new { wanted }, r => (Number: r.GetString(0), UserId: r.GetGuid(1)), ct);
        return rows.GroupBy(r => r.Number, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().UserId, StringComparer.Ordinal);
    }
}
