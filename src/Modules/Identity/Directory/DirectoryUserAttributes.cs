using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Identity.Directory;

/// <summary>
/// Gives authorization conditions the job title and office that directory sync mirrors (spec 004 section 6, spec 003
/// User Story 6 scenario 7). Only directory values are used, never profile edits, so a user cannot set their own
/// attributes (spec 004 section 14). A user who is not linked to the directory has no such attributes.
/// </summary>
public sealed class DirectoryUserAttributes(IDbSession session) : IUserAttributeProvider
{
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(Guid userId, CancellationToken ct)
    {
        var json = await session.ScalarAsync<string?>(
            """
            select u.attributes::text from identity.directory_user_link l
            join identity.directory_user u on u.connection_id = l.connection_id and u.directory_id = l.directory_id
            where l.user_id = @u
            """, new { u = userId }, ct);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json is null)
        {
            return result;
        }
        using var doc = JsonDocument.Parse(json);
        foreach (var (property, key) in new[] { ("jobTitle", ConditionAttributes.JobTitle), ("office", ConditionAttributes.Office) })
        {
            if (doc.RootElement.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            {
                result[key] = v.GetString()!;
            }
        }
        return result;
    }
}
