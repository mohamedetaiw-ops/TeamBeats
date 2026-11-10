using System.Text;
using TeamBeats.SyncAgent;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 017 (R2) T015: how the sync agent turns Active Directory entries into canonical directory records.</summary>
public sealed class SyncAgentMappingTests
{
    private static LdapEntry Entry(Guid? id, params (string Name, string Value)[] values)
    {
        var attributes = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
        if (id is { } g)
        {
            attributes["objectGUID"] = [g.ToByteArray()];
        }
        foreach (var (name, value) in values)
        {
            attributes[name] = [Encoding.UTF8.GetBytes(value)];
        }
        return new LdapEntry("CN=x,DC=corp,DC=example", attributes);
    }

    [Fact]
    public void A_user_is_keyed_by_object_guid_and_absent_attributes_are_reported_as_cleared()
    {
        var id = Guid.NewGuid();
        var user = DirectoryMapping.User(Entry(id, ("userAccountControl", "512"), ("displayName", "Rana"), ("mail", "rana@corp.example")), _ => null)!;
        Assert.Equal(id.ToString("D"), user.ExternalId);
        Assert.True(user.AccountEnabled);
        Assert.Equal("Rana", user.DisplayName);
        Assert.Equal(string.Empty, user.JobTitle);
        Assert.Equal(string.Empty, user.ManagerId);
        Assert.Null(DirectoryMapping.User(Entry(null, ("displayName", "No GUID")), _ => null));
    }

    [Fact]
    public void A_disabled_account_or_one_without_its_control_flags_is_not_enabled_and_a_tombstone_is_removed()
    {
        Assert.False(DirectoryMapping.User(Entry(Guid.NewGuid(), ("userAccountControl", "514")), _ => null)!.AccountEnabled);
        Assert.False(DirectoryMapping.User(Entry(Guid.NewGuid()), _ => null)!.AccountEnabled);
        Assert.True(DirectoryMapping.User(Entry(Guid.NewGuid(), ("isDeleted", "TRUE")), _ => null)!.Removed);
    }

    [Fact]
    public void A_changed_group_is_sent_with_its_whole_membership_and_a_tombstone_is_removed()
    {
        var group = DirectoryMapping.Group(Entry(Guid.NewGuid(), ("cn", "Finance")), ["b", "a", "b"])!;
        Assert.Equal("Finance", group.DisplayName);
        Assert.Equal(["a", "b"], group.MembersAdded);
        Assert.True(group.MembersComplete);
        Assert.True(DirectoryMapping.Group(Entry(Guid.NewGuid(), ("isDeleted", "TRUE")), [])!.Removed);
    }

    [Fact]
    public void An_incremental_read_asks_only_for_changes_after_the_cursor()
    {
        Assert.Equal("(&(objectCategory=person)(objectClass=user))", DirectoryMapping.Filter(DirectoryMapping.UserKind, null, null));
        Assert.Equal("(&(objectClass=group)(cn=TB-*)(uSNChanged>=1001))", DirectoryMapping.Filter(DirectoryMapping.GroupKind, "(cn=TB-*)", 1000));
    }
}
