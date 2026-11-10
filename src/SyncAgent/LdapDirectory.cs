using System.DirectoryServices.Protocols;
using System.Net;

namespace TeamBeats.SyncAgent;

/// <summary>Where and how the agent reads: the job's settings from TeamBeats plus the service account kept on this side.</summary>
public sealed record LdapTarget(string Host, int Port, string BaseDn, string? UserFilter, string? GroupFilter, NetworkCredential Credential);

/// <summary>Read access to a directory over LDAPS. Read only: the agent never writes to Active Directory.</summary>
public interface ILdapDirectory : IDisposable
{
    /// <summary>The domain's naming context and the highest committed update number when the read starts.</summary>
    (string NamingContext, long HighestUsn) RootInfo();

    IEnumerable<LdapEntry> Search(string baseDn, string filter, string[] attributes, bool showDeleted = false);

    /// <summary>The entry at one DN, or null.</summary>
    LdapEntry? Find(string distinguishedName, string[] attributes);

    /// <summary>All values of the member attribute, read in ranges so groups over 1,500 members are complete.</summary>
    IReadOnlyList<string> Members(string groupDn);
}

/// <summary>Opens directories. A seam so tests can play a directory without a domain controller.</summary>
public interface ILdapDirectoryFactory
{
    ILdapDirectory Open(LdapTarget target);
}

public sealed class LdapDirectoryFactory : ILdapDirectoryFactory
{
    public ILdapDirectory Open(LdapTarget target) => new LdapDirectory(target);
}

/// <summary>
/// <see cref="ILdapDirectory"/> with System.DirectoryServices.Protocols. LDAPS only: the connection uses TLS and checks
/// the domain controller's certificate with the operating system's trust store; plain LDAP is never tried (spec 003
/// FR-011). Searches page 500 entries at a time.
/// </summary>
public sealed class LdapDirectory : ILdapDirectory
{
    private const int PageSize = 500;
    private const string ShowDeletedOid = "1.2.840.113556.1.4.417";
    private readonly LdapConnection _connection;

    public LdapDirectory(LdapTarget target)
    {
        _connection = new LdapConnection(new LdapDirectoryIdentifier(target.Host, target.Port, false, false), target.Credential, AuthType.Basic)
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        _connection.SessionOptions.ProtocolVersion = 3;
        _connection.SessionOptions.SecureSocketLayer = true;
        _connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        _connection.Bind();
    }

    public (string NamingContext, long HighestUsn) RootInfo()
    {
        var response = (SearchResponse)_connection.SendRequest(new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "highestCommittedUSN"));
        var root = response.Entries[0];
        var context = (string)root.Attributes["defaultNamingContext"][0];
        var usn = long.Parse((string)root.Attributes["highestCommittedUSN"][0], System.Globalization.CultureInfo.InvariantCulture);
        return (context, usn);
    }

    public IEnumerable<LdapEntry> Search(string baseDn, string filter, string[] attributes, bool showDeleted = false)
    {
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, attributes);
        var paging = new PageResultRequestControl(PageSize);
        request.Controls.Add(paging);
        if (showDeleted)
        {
            request.Controls.Add(new DirectoryControl(ShowDeletedOid, null, true, true));
        }
        while (true)
        {
            var response = (SearchResponse)_connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
            {
                yield return Convert(entry);
            }
            var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0)
            {
                yield break;
            }
            paging.Cookie = cookie;
        }
    }

    public LdapEntry? Find(string distinguishedName, string[] attributes)
    {
        try
        {
            var response = (SearchResponse)_connection.SendRequest(new SearchRequest(distinguishedName, "(objectClass=*)", SearchScope.Base, attributes));
            return response.Entries.Count == 0 ? null : Convert(response.Entries[0]);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
    }

    public IReadOnlyList<string> Members(string groupDn)
    {
        var members = new List<string>();
        var start = 0;
        while (true)
        {
            var name = $"member;range={start}-*";
            var response = (SearchResponse)_connection.SendRequest(new SearchRequest(groupDn, "(objectClass=*)", SearchScope.Base, name));
            if (response.Entries.Count == 0)
            {
                return members;
            }
            var attribute = response.Entries[0].Attributes.Values.Cast<DirectoryAttribute>()
                .FirstOrDefault(a => a.Name.StartsWith("member;range=", StringComparison.OrdinalIgnoreCase));
            if (attribute is null)
            {
                return members;
            }
            members.AddRange(attribute.GetValues(typeof(string)).Cast<string>());
            if (attribute.Name.EndsWith("-*", StringComparison.Ordinal))
            {
                return members;
            }
            start = members.Count;
        }
    }

    private static LdapEntry Convert(SearchResultEntry entry)
    {
        var attributes = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (DirectoryAttribute attribute in entry.Attributes.Values)
        {
            attributes[attribute.Name] = attribute.GetValues(typeof(byte[])).Cast<byte[]>().ToList();
        }
        return new LdapEntry(entry.DistinguishedName, attributes);
    }

    public void Dispose() => _connection.Dispose();
}
