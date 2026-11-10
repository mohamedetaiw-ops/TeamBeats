using System.Globalization;
using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.SyncAgent;

/// <summary>The outcome of one read: the records to send and the cursors for the next read.</summary>
public sealed record DirectoryReadResult(IReadOnlyList<DirectoryUserRecord> Users, IReadOnlyList<DirectoryGroupRecord> Groups, string UsersCursor,
    string GroupsCursor);

/// <summary>
/// One directory read for a job (spec 017 US6 scenario 2). With no cursors it reads every user and group under the base
/// DN; with cursors it reads only entries changed since (<c>uSNChanged</c>) and the deleted ones (tombstones). The
/// cursor for the next read is the highest committed update number taken before reading, so nothing changed during the
/// read is missed. A changed group is sent with its whole membership.
/// </summary>
public static class DirectoryRead
{
    public static DirectoryReadResult Run(ILdapDirectory directory, LdapTarget target, string? usersCursor, string? groupsCursor)
    {
        var (namingContext, highest) = directory.RootInfo();
        var usersAfter = Usn(usersCursor);
        var groupsAfter = Usn(groupsCursor);
        var dnToId = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        string? IdOf(string dn)
        {
            if (!dnToId.TryGetValue(dn, out var id))
            {
                id = directory.Find(dn, ["objectGUID"]) is { } found ? DirectoryMapping.ObjectGuid(found) : null;
                dnToId[dn] = id;
            }
            return id;
        }

        var users = new List<DirectoryUserRecord>();
        var userEntries = directory.Search(target.BaseDn, DirectoryMapping.Filter(DirectoryMapping.UserKind, target.UserFilter, usersAfter),
            DirectoryMapping.UserAttributes).ToList();
        foreach (var entry in userEntries)
        {
            dnToId[entry.DistinguishedName] = DirectoryMapping.ObjectGuid(entry);
        }
        foreach (var entry in userEntries)
        {
            if (DirectoryMapping.User(entry, IdOf) is { } record)
            {
                users.Add(record);
            }
        }

        var groups = new List<DirectoryGroupRecord>();
        foreach (var entry in directory.Search(target.BaseDn, DirectoryMapping.Filter(DirectoryMapping.GroupKind, target.GroupFilter, groupsAfter),
                     DirectoryMapping.GroupAttributes))
        {
            var members = directory.Members(entry.DistinguishedName).Select(IdOf).OfType<string>().ToArray();
            if (DirectoryMapping.Group(entry, members) is { } record)
            {
                groups.Add(record);
            }
        }

        if (usersAfter is not null || groupsAfter is not null)
        {
            // Deleted users and groups are tombstones under the domain's Deleted Objects container.
            var after = Math.Min(usersAfter ?? long.MaxValue, groupsAfter ?? long.MaxValue);
            foreach (var entry in directory.Search(namingContext, $"(&(isDeleted=TRUE)(uSNChanged>={after + 1})(|(objectClass=user)(objectClass=group)))",
                         DirectoryMapping.UserAttributes, showDeleted: true))
            {
                if (DirectoryMapping.IsGroup(entry))
                {
                    if (DirectoryMapping.Group(entry, []) is { } record)
                    {
                        groups.Add(record);
                    }
                }
                else if (DirectoryMapping.User(entry, _ => null) is { } user)
                {
                    users.Add(user);
                }
            }
        }

        var cursor = highest.ToString(CultureInfo.InvariantCulture);
        return new DirectoryReadResult(users, groups, cursor, cursor);
    }

    private static long? Usn(string? cursor) =>
        long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var usn) ? usn : null;
}
