using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TeamBeats.DevFakes;

/// <summary>
/// In-memory Microsoft identity platform and Graph for the TeamBeats application: client credentials tokens for
/// directories that granted admin consent, object counts and directory delta queries (directory sync), and delegated
/// tokens with the signed-in user's mail, calendar, free/busy and the directory's rooms (spec 018). Used by tests and,
/// loaded from a scenario file, by the development host and staging. All directories, people and numbers are fictional.
/// </summary>
public sealed class FakeMicrosoftGraph : HttpMessageHandler
{
    public const string ClientId = "0b1e2f3a-4c5d-4e6f-8a9b-0c1d2e3f4a5b";
    public const string ClientSecret = "fictional-platform-app-secret";

    private readonly ConcurrentDictionary<string, Directory> _directories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DirectoryData> _data = new(StringComparer.Ordinal);

    public bool Unavailable { get; set; }

    /// <summary>Users per delta page, so paging with nextLink is exercised.</summary>
    public int PageSize { get; set; } = 2;

    // Delta tokens name this instance, because its versions start again when the host restarts. A token from an earlier
    // instance (staging after a redeploy) reads everything again, so scenario changes reach the next sync.
    private readonly string _instance = Guid.NewGuid().ToString("N")[..8];

    private string DeltaToken(long version) => $"{_instance}-{version}";

    private long SinceVersion(string token)
    {
        var parts = token.Split('-');
        return parts.Length == 2 && parts[0] == _instance && long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    public sealed record Directory(IReadOnlyList<string> Roles, long Users, long Groups);

    /// <summary>The customer's admin granted these application permissions to the TeamBeats application.</summary>
    public void Consent(Guid directoryId, IReadOnlyList<string> roles, long users = 3400, long groups = 120) =>
        _directories[directoryId.ToString("D")] = new Directory(roles, users, groups);

    public void Revoke(Guid directoryId) => _directories.TryRemove(directoryId.ToString("D"), out _);

    public bool HasConsent(Guid directoryId) => _directories.ContainsKey(directoryId.ToString("D"));

    private readonly ConcurrentDictionary<string, DelegatedCode> _delegatedCodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Mailbox> _mailboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<Room>> _rooms = new(StringComparer.Ordinal);

    private sealed record DelegatedCode(string Directory, string UserId, string ClientId, string RedirectUri, string? CodeChallenge, string Scope, string? Upn = null);

    /// <summary>A message as Graph returns it to Mail.ReadBasic: no body, only the preview.</summary>
    public sealed record Message(string Id, string Subject, string FromName, string FromAddress, DateTimeOffset Received, string Preview, bool IsRead, string Importance = "normal");

    /// <summary>A calendar event. Attendees are email addresses; free/busy is computed from everyone's events.</summary>
    public sealed record Event(string Id, string Subject, DateTimeOffset Start, DateTimeOffset End, string? Location, string OrganizerName,
        string OrganizerAddress, IReadOnlyList<string> Attendees, bool IsOnline = false, string ShowAs = "busy");

    public sealed record Room(string Id, string DisplayName, string EmailAddress, int Capacity, string? Building, string? FloorLabel);

    public sealed class Mailbox
    {
        public List<Message> Messages { get; } = [];
        public List<Event> Events { get; } = [];
    }

    /// <summary>The mailbox and calendar of one directory user (by object ID).</summary>
    public Mailbox MailboxOf(Guid directoryId, string userId) => _mailboxes.GetOrAdd($"{directoryId:D}/{userId}", _ => new Mailbox());

    // Mailboxes keyed by the signed-in user's UPN (the login hint), for tests that seed users without a fake directory
    // entry (spec 018 MailCalendarTests). A token whose user is not in the fake directory reads these instead.

    public sealed record FakeMessage(string Id, string FromName, string FromAddress, string Subject, DateTimeOffset Received, bool IsRead = false,
        string Importance = "normal", bool HasAttachments = false, string Preview = "", bool Focused = true);

    public sealed record FakeEvent(string Id, string Subject, DateTimeOffset Start, DateTimeOffset End, bool AllDay = false, string ShowAs = "busy",
        string Sensitivity = "normal", bool Cancelled = false, string Response = "accepted", string? Location = null, string? JoinUrl = null, string? ICalUId = null);

    public sealed record FakeScheduleItem(string Status, DateTimeOffset Start, DateTimeOffset End);

    public sealed class FakeMailbox
    {
        public List<FakeMessage> Messages { get; } = [];
        public List<FakeEvent> Events { get; } = [];
        public int UnreadCount { get; set; }
        public bool Exists { get; set; } = true;
    }

    private readonly ConcurrentDictionary<string, FakeMailbox> _upnMailboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<FakeScheduleItem>> _schedules = new(StringComparer.OrdinalIgnoreCase);

    public FakeMailbox MailboxFor(string upn) => _upnMailboxes.GetOrAdd(upn, _ => new FakeMailbox());

    /// <summary>Free and busy that the colleague shares; a colleague without an entry does not share.</summary>
    public void Schedule(string upn, params FakeScheduleItem[] items) => _schedules[upn] = [.. items];

    /// <summary>Status codes the next delegated Graph calls answer with, one per call (429 carries Retry-After: 0).</summary>
    public ConcurrentQueue<int> DelegatedFaults { get; } = new();

    /// <summary>Delegated Graph calls received, by path.</summary>
    public ConcurrentBag<string> DelegatedCalls { get; } = [];

    public void AddRoom(Guid directoryId, Room room) => _rooms.GetOrAdd(directoryId.ToString("D"), _ => []).Add(room);

    private readonly ConcurrentDictionary<string, HashSet<string>> _delegated = new(StringComparer.Ordinal);

    /// <summary>Delegated permissions consented for the TeamBeats application in a directory (plain names such as Mail.ReadBasic).</summary>
    /// <remarks>Replaces what was consented before, so tests can take consent away.</remarks>
    public void ConsentDelegated(Guid directoryId, params string[] scopes) =>
        _delegated[directoryId.ToString("D")] = new HashSet<string>(scopes, StringComparer.Ordinal);

    /// <summary>Adds delegated permissions to what the directory already consented, as a further admin consent does.</summary>
    public void AddDelegatedConsent(Guid directoryId, params string[] scopes) =>
        _delegated.AddOrUpdate(directoryId.ToString("D"), _ => new HashSet<string>(scopes, StringComparer.Ordinal),
            (_, existing) => new HashSet<string>(existing.Concat(scopes), StringComparer.Ordinal));

    /// <summary>The next authorize answers with this error instead of a code (for example consent or conditional access).</summary>
    public (string Error, string Description)? NextAuthorizeError { get; set; }

    /// <summary>The next authorization code redemption fails with this error code.</summary>
    public string? NextTokenError { get; set; }

    /// <summary>Delegated access tokens issued, so tests can check they never leave the server.</summary>
    public ConcurrentBag<string> IssuedDelegatedTokens { get; } = [];

    /// <summary>Scope names without the Graph resource prefix.</summary>
    public static string[] PlainScopes(string scope) =>
        scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Replace("https://graph.microsoft.com/", string.Empty, StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Why Microsoft would refuse a delegated authorize request for the TeamBeats application, or null when it would
    /// issue a code: wrong client, no PKCE, or delegated permissions the directory has not consented.
    /// </summary>
    public string? DelegatedRefusal(string directory, IReadOnlyDictionary<string, string> query)
    {
        var scopes = PlainScopes(query.GetValueOrDefault("scope") ?? string.Empty).Where(x => x is not ("openid" or "profile" or "email" or "offline_access"));
        return query.GetValueOrDefault("client_id") != ClientId || query.GetValueOrDefault("code_challenge_method") != "S256"
            || !_delegated.TryGetValue(directory, out var consented) || !scopes.All(consented.Contains)
            ? "AADSTS65001: The user or administrator has not consented."
            : null;
    }

    /// <summary>
    /// Plays Microsoft's authorize page for tests: returns the query Microsoft appends to the redirect URI. The user is
    /// the directory user whose mail matches <c>login_hint</c>.
    /// </summary>
    public string Authorize(Uri authorizeUrl)
    {
        var q = FakeIdentityProvider.ParseQuery(authorizeUrl.Query);
        var directory = authorizeUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
        var state = Uri.EscapeDataString(q["state"]);
        if (NextAuthorizeError is { } e)
        {
            NextAuthorizeError = null;
            return $"?error={e.Error}&error_description={Uri.EscapeDataString(e.Description)}&state={state}";
        }
        if (DelegatedRefusal(directory, q) is { } refusal)
        {
            return $"?error=consent_required&error_description={Uri.EscapeDataString(refusal)}&state={state}";
        }
        var hint = q.GetValueOrDefault("login_hint");
        var user = Guid.TryParse(directory, out var directoryId)
            ? Data(directoryId).CurrentUsers().FirstOrDefault(u => string.Equals(u.Properties.GetValueOrDefault("mail") as string, hint, StringComparison.OrdinalIgnoreCase)).Id
            : null;
        var code = "code-" + Guid.NewGuid().ToString("N");
        _delegatedCodes[code] = new DelegatedCode(directory, user ?? "unknown-user", ClientId, q["redirect_uri"], q["code_challenge"], q["scope"], hint);
        return $"?code={code}&state={state}";
    }

    /// <summary>
    /// The user signed in at the fake authorize page and agreed to the delegated scopes. Returns the authorization code
    /// the token endpoint redeems (with PKCE when a challenge was sent).
    /// </summary>
    public string IssueDelegatedCode(Guid directoryId, string userId, string clientId, string redirectUri, string? codeChallenge, string scope, string? upn = null)
    {
        var code = "dc-" + Guid.NewGuid().ToString("N");
        _delegatedCodes[code] = new DelegatedCode(directoryId.ToString("D"), userId, clientId, redirectUri, codeChallenge, scope, upn);
        return code;
    }

    /// <summary>Directory content for delta queries. Every change gets a version, and a delta link returns changes after its version.</summary>
    public DirectoryData Data(Guid directoryId) => _data.GetOrAdd(directoryId.ToString("D"), _ => new DirectoryData());

    public sealed class DirectoryData
    {
        private readonly object _gate = new();
        private long _version;
        private readonly Dictionary<string, (Dictionary<string, object?> Props, bool Deleted, long Version)> _users = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Name, bool Deleted, long Version)> _groups = new(StringComparer.Ordinal);
        private readonly List<(string Group, string Member, bool Added, long Version)> _memberLog = [];
        private readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _kinds = new(StringComparer.Ordinal);

        public long Version { get { lock (_gate) { return _version; } } }

        public void AddUser(string oid, string displayName, string? mail, bool enabled = true, string? jobTitle = null)
        {
            lock (_gate)
            {
                _users[oid] = (new Dictionary<string, object?>
                {
                    ["displayName"] = displayName, ["mail"] = mail, ["userPrincipalName"] = mail, ["jobTitle"] = jobTitle, ["accountEnabled"] = enabled,
                }, false, ++_version);
            }
        }

        /// <summary>Extra Graph user properties (officeLocation, preferredLanguage, employeeId, mobilePhone and so on).</summary>
        public void SetProperties(string oid, IReadOnlyDictionary<string, object?> properties)
        {
            lock (_gate)
            {
                var (props, deleted, _) = _users[oid];
                foreach (var (k, v) in properties)
                {
                    props[k] = v;
                }
                _users[oid] = (props, deleted, ++_version);
            }
        }

        /// <summary>The current, not deleted, users with their properties (for the fake sign-in page and delegated calls).</summary>
        public IReadOnlyList<(string Id, IReadOnlyDictionary<string, object?> Properties)> CurrentUsers()
        {
            lock (_gate)
            {
                return _users.Where(u => !u.Value.Deleted).OrderBy(u => u.Key, StringComparer.Ordinal)
                    .Select(u => (u.Key, (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(u.Value.Props))).ToList();
            }
        }

        /// <summary>Sets or clears the user's manager, reported as <c>manager@delta</c> the way Graph reports it (spec 010).</summary>
        public void SetManager(string oid, string? managerOid)
        {
            lock (_gate)
            {
                var (props, deleted, _) = _users[oid];
                props["manager@delta"] = managerOid is null
                    ? (props.TryGetValue("manager@delta", out var old) && old is object[] { Length: > 0 } list
                        ? new object[] { new Dictionary<string, object?> { ["id"] = ((Dictionary<string, object?>)list[0])["id"], ["@removed"] = new { reason = "deleted" } } }
                        : Array.Empty<object>())
                    : new object[] { new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.user", ["id"] = managerOid } };
                _users[oid] = (props, deleted, ++_version);
            }
        }

        public void UpdateUser(string oid, string? displayName = null, string? mail = null, bool? enabled = null)
        {
            lock (_gate)
            {
                var (props, _, _) = _users[oid];
                if (displayName is not null) props["displayName"] = displayName;
                if (mail is not null) { props["mail"] = mail; props["userPrincipalName"] = mail; }
                if (enabled is not null) props["accountEnabled"] = enabled;
                _users[oid] = (props, false, ++_version);
            }
        }

        /// <summary>Organization attributes used by the organization mapping (spec 002 US5).</summary>
        public void SetOrg(string oid, string? company, string? department, string? division = null)
        {
            lock (_gate)
            {
                var (props, _, _) = _users[oid];
                props["companyName"] = company;
                props["department"] = department;
                props["employeeOrgData"] = new Dictionary<string, object?> { ["division"] = division };
                _users[oid] = (props, false, ++_version);
            }
        }

        public void DeleteUser(string oid)
        {
            lock (_gate)
            {
                _users[oid] = (_users[oid].Props, true, ++_version);
            }
        }

        public void AddGroup(string gid, string name) => AddContainer(gid, name, "group");

        public void AddAdminUnit(string id, string name) => AddContainer(id, name, "admin_unit");

        public void Rename(string id, string name)
        {
            lock (_gate)
            {
                _groups[id] = (name, _groups[id].Deleted, ++_version);
            }
        }

        public void DeleteContainer(string id)
        {
            lock (_gate)
            {
                _groups[id] = (_groups[id].Name, true, ++_version);
            }
        }

        private void AddContainer(string gid, string name, string kind)
        {
            lock (_gate)
            {
                _groups[gid] = (name, false, ++_version);
                _kinds[gid] = kind;
                _members.TryAdd(gid, new HashSet<string>(StringComparer.Ordinal));
            }
        }

        public void AddMember(string gid, string oid) => ChangeMember(gid, oid, true);

        public void RemoveMember(string gid, string oid) => ChangeMember(gid, oid, false);

        private void ChangeMember(string gid, string oid, bool added)
        {
            lock (_gate)
            {
                var changed = added ? _members[gid].Add(oid) : _members[gid].Remove(oid);
                if (changed)
                {
                    _memberLog.Add((gid, oid, added, ++_version));
                    _groups[gid] = (_groups[gid].Name, _groups[gid].Deleted, _version);
                }
            }
        }

        public (List<object> Items, long Version) UserChanges(long since)
        {
            lock (_gate)
            {
                var items = _users.Where(u => u.Value.Version > since && !(since == 0 && u.Value.Deleted)).OrderBy(u => u.Key, StringComparer.Ordinal)
                    .Select(u => u.Value.Deleted
                        ? (object)new Dictionary<string, object?> { ["id"] = u.Key, ["@removed"] = new { reason = "changed" } }
                        : new Dictionary<string, object?>(u.Value.Props) { ["id"] = u.Key })
                    .ToList();
                return (items, _version);
            }
        }

        public (List<object> Items, long Version) GroupChanges(long since, string kind = "group")
        {
            lock (_gate)
            {
                var items = new List<object>();
                foreach (var (gid, g) in _groups.Where(g => g.Value.Version > since && _kinds[g.Key] == kind && !(since == 0 && g.Value.Deleted)).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    if (g.Deleted)
                    {
                        items.Add(new Dictionary<string, object?> { ["id"] = gid, ["@removed"] = new { reason = "deleted" } });
                        continue;
                    }
                    var members = since == 0
                        ? _members[gid].Select(m => (object)new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.user", ["id"] = m }).ToList()
                        : _memberLog.Where(l => l.Group == gid && l.Version > since)
                            .Select(l => (object)(l.Added
                                ? new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.user", ["id"] = l.Member }
                                : new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.user", ["id"] = l.Member, ["@removed"] = new { reason = "deleted" } }))
                            .ToList();
                    items.Add(new Dictionary<string, object?> { ["id"] = gid, ["displayName"] = g.Name, ["members@delta"] = members });
                }
                return (items, _version);
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        var uri = request.RequestUri!;
        if (uri.Host == "login.microsoftonline.com" && uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
        {
            var directory = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            var form = FakeIdentityProvider.ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken));
            if (form.GetValueOrDefault("grant_type") == "authorization_code")
            {
                return RedeemDelegated(directory, form);
            }
            if (form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret
                || form.GetValueOrDefault("grant_type") != "client_credentials" || !_directories.TryGetValue(directory, out var d))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_client\"}") };
            }
            var payload = Base64Url(JsonSerializer.Serialize(new { tid = directory, roles = d.Roles }));
            var token = $"{Base64Url("{\"alg\":\"none\"}")}.{payload}.sig-{directory}";
            return Json(new { access_token = token, token_type = "Bearer", expires_in = 3600 });
        }
        if (uri.Host == "graph.microsoft.com" && uri.AbsolutePath.EndsWith("/$count", StringComparison.Ordinal))
        {
            var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
            var directory = bearer.Split(".sig-").ElementAtOrDefault(1) ?? string.Empty;
            if (!_directories.TryGetValue(directory, out var d))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            var count = uri.AbsolutePath.Contains("/users/", StringComparison.Ordinal) ? d.Users : d.Groups;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(count.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        }
        if (uri.Host == "graph.microsoft.com" && (uri.AbsolutePath.EndsWith("/users/delta", StringComparison.Ordinal) || uri.AbsolutePath.EndsWith("/groups/delta", StringComparison.Ordinal)
            || uri.AbsolutePath.EndsWith("/administrativeUnits/delta", StringComparison.Ordinal)))
        {
            var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
            var directory = bearer.Split(".sig-").ElementAtOrDefault(1) ?? string.Empty;
            if (!_directories.ContainsKey(directory))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            var data = _data.GetOrAdd(directory, _ => new DirectoryData());
            var query = FakeIdentityProvider.ParseQuery(uri.Query);
            var since = query.TryGetValue("$deltatoken", out var token) ? SinceVersion(token) : 0;
            var skip = query.TryGetValue("$skiptoken", out var skipToken) ? int.Parse(skipToken, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var isUsers = uri.AbsolutePath.EndsWith("/users/delta", StringComparison.Ordinal);
            var isUnits = uri.AbsolutePath.EndsWith("/administrativeUnits/delta", StringComparison.Ordinal);
            var (items, version) = isUsers ? data.UserChanges(since) : data.GroupChanges(since, isUnits ? "admin_unit" : "group");
            var basePath = $"https://graph.microsoft.com/v1.0/{(isUsers ? "users" : isUnits ? "directory/administrativeUnits" : "groups")}/delta";
            var page = items.Skip(skip).Take(isUsers ? PageSize : int.MaxValue).ToList();
            var body = new Dictionary<string, object?> { ["value"] = page };
            if (isUsers && skip + PageSize < items.Count)
            {
                body["@odata.nextLink"] = $"{basePath}?$deltatoken={DeltaToken(since)}&$skiptoken={skip + PageSize}";
            }
            else
            {
                body["@odata.deltaLink"] = $"{basePath}?$deltatoken={DeltaToken(version)}";
            }
            return Json(body);
        }
        if (uri.Host == "graph.microsoft.com")
        {
            return await DelegatedAsync(request, uri, cancellationToken);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage RedeemDelegated(string directory, Dictionary<string, string> form)
    {
        if (NextTokenError is { } error)
        {
            NextTokenError = null;
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($"{{\"error\":\"{error}\"}}") };
        }
        var code = form.GetValueOrDefault("code") ?? string.Empty;
        if (!_delegatedCodes.TryRemove(code, out var issued) || issued.Directory != directory
            || form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret
            || issued.ClientId != ClientId || issued.RedirectUri != form.GetValueOrDefault("redirect_uri")
            || (issued.CodeChallenge is { Length: > 0 } challenge && challenge != Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(
                System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? string.Empty)))))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
        }
        // Scopes arrive as https://graph.microsoft.com/Mail.ReadBasic or as the bare name; the token lists bare names.
        var scopes = issued.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.StartsWith("https://graph.microsoft.com/", StringComparison.Ordinal) ? x["https://graph.microsoft.com/".Length..] : x)
            .Where(x => x is not ("openid" or "profile" or "email" or "offline_access")).ToArray();
        var payload = Base64Url(JsonSerializer.Serialize(new { tid = directory, oid = issued.UserId, scp = string.Join(' ', scopes), upn = issued.Upn }));
        var token = $"{Base64Url("{\"alg\":\"none\"}")}.{payload}.sig-{directory}~{issued.UserId}~{string.Join(',', scopes)}";
        IssuedDelegatedTokens.Add(token);
        return Json(new { access_token = token, token_type = "Bearer", expires_in = 3600, scope = string.Join(' ', scopes.Append("openid").Append("profile")) });
    }

    private async Task<HttpResponseMessage> DelegatedAsync(HttpRequestMessage request, Uri uri, CancellationToken ct)
    {
        DelegatedCalls.Add(uri.AbsolutePath);
        if (DelegatedFaults.TryDequeue(out var fault))
        {
            var failed = new HttpResponseMessage((HttpStatusCode)fault) { Content = new StringContent("{\"error\":{\"code\":\"fault\"}}") };
            if (fault == 429)
            {
                failed.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            }
            return failed;
        }
        var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
        var parts = (bearer.Split(".sig-").ElementAtOrDefault(1) ?? string.Empty).Split('~');
        var directory = parts[0];
        if (!Guid.TryParse(directory, out var directoryId))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        var path = uri.AbsolutePath;
        if (path == "/v1.0/places/microsoft.graph.room")
        {
            return Json(new { value = _rooms.GetValueOrDefault(directory, []).Select(r => new
            {
                id = r.Id, displayName = r.DisplayName, emailAddress = r.EmailAddress, capacity = r.Capacity, building = r.Building, floorLabel = r.FloorLabel,
            }) });
        }
        if (parts.Length < 3)
        {
            if (path.StartsWith("/v1.0/sites/", StringComparison.Ordinal) || path.StartsWith("/v1.0/drives/", StringComparison.Ordinal))
            {
                return SharePointApp(directory, Uri.UnescapeDataString(path), FakeIdentityProvider.ParseQuery(uri.Query));
            }
            // An application token: these calls need a signed-in user.
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }
        var userId = parts[1];
        var scopes = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var data = Data(directoryId);
        if (path.StartsWith("/v1.0/me/drive/", StringComparison.Ordinal))
        {
            var owner = UpnOf(bearer) is { Length: > 0 } driveUpn ? driveUpn.ToLowerInvariant() : $"{directory}/{userId}";
            return await DriveAsync(request, Uri.UnescapeDataString(path), owner, scopes, ct);
        }
        if (path.StartsWith("/v1.0/drives/", StringComparison.Ordinal))
        {
            var reader = UpnOf(bearer) is { Length: > 0 } readerUpn ? readerUpn
                : data.CurrentUsers().FirstOrDefault(u => u.Id == userId).Properties?.GetValueOrDefault("mail") as string;
            return await SharePointUserAsync(request, directory, Uri.UnescapeDataString(path), FakeIdentityProvider.ParseQuery(uri.Query), reader?.ToLowerInvariant(),
                scopes, ct);
        }
        var me = data.CurrentUsers().FirstOrDefault(u => u.Id == userId);
        // Tokens hold everything the fake needs, so a mailbox token still works after the host restarts (staging redeploys).
        if (me.Id is null && UpnOf(bearer) is { Length: > 0 } upn)
        {
            return await UpnMailboxAsync(request, uri, scopes, upn, ct);
        }
        if (me.Id is null || me.Properties.GetValueOrDefault("accountEnabled") is false)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        var query = FakeIdentityProvider.ParseQuery(uri.Query);
        var top = query.TryGetValue("$top", out var t) && int.TryParse(t, out var n) ? Math.Clamp(n, 1, 1000) : 10;
        var mailbox = MailboxOf(directoryId, userId);

        if (path == "/v1.0/me")
        {
            // Navigation properties such as manager@delta belong to delta queries, not to /me.
            return Json(new Dictionary<string, object?>(me.Properties.Where(p => !p.Key.Contains('@', StringComparison.Ordinal))) { ["id"] = userId });
        }
        if (path == "/v1.0/me/mailFolders/inbox")
        {
            if (!scopes.Overlaps(["Mail.ReadBasic", "Mail.Read"]))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            return Json(new { unreadItemCount = mailbox.Messages.Count(m => !m.IsRead) });
        }
        if (path is "/v1.0/me/messages" or "/v1.0/me/mailFolders/inbox/messages")
        {
            if (!scopes.Overlaps(["Mail.ReadBasic", "Mail.Read"]))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            var unreadOnly = query.GetValueOrDefault("$filter")?.Replace(" ", string.Empty, StringComparison.Ordinal) == "isReadeqfalse";
            var messages = mailbox.Messages.Where(m => !unreadOnly || !m.IsRead).OrderByDescending(m => m.Received).Take(top).Select(m => new
            {
                id = m.Id, subject = m.Subject, bodyPreview = m.Preview, isRead = m.IsRead, importance = m.Importance,
                receivedDateTime = m.Received.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                from = new { emailAddress = new { name = m.FromName, address = m.FromAddress } },
                webLink = $"https://outlook.office.com/mail/fake/{m.Id}",
            });
            return Json(new { value = messages });
        }
        if (path is "/v1.0/me/calendarView" or "/v1.0/me/events")
        {
            if (!scopes.Overlaps(["Calendars.ReadBasic", "Calendars.Read"]))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            var from = query.TryGetValue("startDateTime", out var s0) ? DateTimeOffset.Parse(s0, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.MinValue;
            var to = query.TryGetValue("endDateTime", out var e0) ? DateTimeOffset.Parse(e0, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.MaxValue;
            var email = me.Properties.GetValueOrDefault("mail") as string;
            var events = AllEvents(directoryId).Where(e => e.OrganizerAddress == email || e.Attendees.Contains(email ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .Where(e => e.Start < to && e.End > from).OrderBy(e => e.Start).Take(Math.Max(top, 50)).Select(e => new
                {
                    id = e.Id, subject = e.Subject, isOnlineMeeting = e.IsOnline, showAs = e.ShowAs,
                    start = new { dateTime = e.Start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.0000000", System.Globalization.CultureInfo.InvariantCulture), timeZone = "UTC" },
                    end = new { dateTime = e.End.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.0000000", System.Globalization.CultureInfo.InvariantCulture), timeZone = "UTC" },
                    location = new { displayName = e.Location ?? string.Empty },
                    organizer = new { emailAddress = new { name = e.OrganizerName, address = e.OrganizerAddress } },
                    attendees = e.Attendees.Select(a => new { emailAddress = new { address = a }, type = "required" }),
                    webLink = $"https://outlook.office.com/calendar/fake/{e.Id}",
                    iCalUId = $"{e.Id}@fake.outlook",
                });
            return Json(new { value = events });
        }
        if (path == "/v1.0/me/calendar/getSchedule" && request.Method == HttpMethod.Post)
        {
            if (!scopes.Overlaps(["Calendars.ReadBasic", "Calendars.Read"]))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var start = DateTimeOffset.Parse(root.GetProperty("startTime").GetProperty("dateTime").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
            var end = DateTimeOffset.Parse(root.GetProperty("endTime").GetProperty("dateTime").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
            var interval = root.TryGetProperty("availabilityViewInterval", out var iv) ? iv.GetInt32() : 30;
            var all = AllEvents(directoryId);
            var value = root.GetProperty("schedules").EnumerateArray().Select(x => x.GetString()!).Select(address =>
            {
                var busy = all.Where(e => e.ShowAs != "free" && (e.OrganizerAddress.Equals(address, StringComparison.OrdinalIgnoreCase)
                    || e.Attendees.Contains(address, StringComparer.OrdinalIgnoreCase)) && e.Start < end && e.End > start).ToList();
                var view = new StringBuilder();
                for (var slot = start; slot < end; slot = slot.AddMinutes(interval))
                {
                    var slotEnd = slot.AddMinutes(interval);
                    view.Append(busy.Any(e => e.Start < slotEnd && e.End > slot) ? '2' : '0');
                }
                return new
                {
                    scheduleId = address, availabilityView = view.ToString(),
                    scheduleItems = busy.Select(e => new
                    {
                        status = e.ShowAs,
                        start = new { dateTime = e.Start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.0000000", System.Globalization.CultureInfo.InvariantCulture), timeZone = "UTC" },
                        end = new { dateTime = e.End.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.0000000", System.Globalization.CultureInfo.InvariantCulture), timeZone = "UTC" },
                    }),
                };
            });
            return Json(new { value });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // SharePoint Online (spec 011 US9): sites granted to the application with Sites.Selected, their document libraries
    // and items. Readers are the emails that may read an item; an item without readers is read by everyone in the
    // directory. Application tokens list, read items with permissions and read changes; delegated tokens with
    // Files.Read.All list and read content as the person, so items they may not read are never returned. With
    // Files.ReadWrite.All they upload and rename where they may write: into or on items without readers, which the site's
    // members may change; items with readers are read only to those readers. A Purview label is an ID and a name.

    public sealed record SpSite(string Id, string Directory, string Host, string Path, string Name, bool Granted);

    public sealed record SpDrive(string Id, string SiteId, string Name);

    public sealed record SpItem(string Id, string DriveId, string? ParentId, string Name, bool Folder, byte[] Content, IReadOnlyList<string> Readers,
        DateTimeOffset Modified, long Version, bool Deleted = false, string? LabelId = null, string? LabelName = null);

    private readonly ConcurrentDictionary<string, SpSite> _spSites = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SpDrive> _spDrives = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SpItem> _spItems = new(StringComparer.Ordinal);
    private long _spVersion;

    /// <summary>Adds a site; <paramref name="granted"/> false is a site the customer has not granted to the application.</summary>
    public string AddSharePointSite(Guid directoryId, string host, string path, string name, bool granted = true)
    {
        var id = $"{host},{Guid.NewGuid():D},{Guid.NewGuid():D}";
        _spSites[id] = new SpSite(id, directoryId.ToString("D"), host.ToLowerInvariant(), path, name, granted);
        return id;
    }

    /// <summary>Grants or withdraws a site for the application, as the customer would with Sites.Selected.</summary>
    public void GrantSharePointSite(string siteId, bool granted) => _spSites[siteId] = _spSites[siteId] with { Granted = granted };

    public string AddSharePointDrive(string siteId, string name)
    {
        var id = "b!" + Guid.NewGuid().ToString("N");
        _spDrives[id] = new SpDrive(id, siteId, name);
        return id;
    }

    /// <summary>Adds a file or folder; the ID returned is the item ID within the drive.</summary>
    public string AddSharePointItem(string driveId, string? parentId, string name, bool folder = false, byte[]? content = null, params string[] readers)
    {
        var id = "01SP" + Guid.NewGuid().ToString("N")[..22].ToUpperInvariant();
        _spItems[id] = new SpItem(id, driveId, parentId, name, folder, content ?? [], readers.Select(r => r.ToLowerInvariant()).ToList(), DateTimeOffset.UtcNow,
            Interlocked.Increment(ref _spVersion));
        return id;
    }

    /// <summary>Changes who may read an item, as a site owner would in SharePoint.</summary>
    public void SetSharePointReaders(string itemId, params string[] readers) =>
        _spItems[itemId] = _spItems[itemId] with { Readers = readers.Select(r => r.ToLowerInvariant()).ToList(), Version = Interlocked.Increment(ref _spVersion) };

    public void EditSharePointItem(string itemId, string? name = null, byte[]? content = null) =>
        _spItems[itemId] = _spItems[itemId] with
        {
            Name = name ?? _spItems[itemId].Name, Content = content ?? _spItems[itemId].Content, Modified = DateTimeOffset.UtcNow,
            Version = Interlocked.Increment(ref _spVersion),
        };

    /// <summary>Puts a Microsoft Purview sensitivity label on a file, or takes it off with null.</summary>
    public void SetSharePointLabel(string itemId, string? labelId, string? labelName = null) =>
        _spItems[itemId] = _spItems[itemId] with { LabelId = labelId, LabelName = labelName, Version = Interlocked.Increment(ref _spVersion) };

    /// <summary>The current files and folders of a drive, for tests that check what a person wrote.</summary>
    public IReadOnlyList<SpItem> SharePointItems(string driveId) => _spItems.Values.Where(i => i.DriveId == driveId && !i.Deleted).ToList();

    public void DeleteSharePointItem(string itemId) =>
        _spItems[itemId] = _spItems[itemId] with { Deleted = true, Version = Interlocked.Increment(ref _spVersion) };

    private static HttpResponseMessage SpMissing() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"itemNotFound\"}}") };

    private static HttpResponseMessage SpDenied() =>
        new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"accessDenied\"}}") };

    private bool SpDriveGranted(string directory, string driveId) =>
        _spDrives.TryGetValue(driveId, out var drive) && _spSites.TryGetValue(drive.SiteId, out var site) && site.Directory == directory && site.Granted;

    private object SpItemJson(SpItem i, bool withDeleted = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = i.Id, ["name"] = i.Name, ["eTag"] = $"\"{{{i.Id}}},{i.Version}\"",
            ["lastModifiedDateTime"] = i.Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["webUrl"] = $"https://fake-sharepoint.example/{i.DriveId}/{i.Id}",
            ["parentReference"] = new Dictionary<string, object?>
            {
                ["driveId"] = i.DriveId, ["id"] = i.ParentId ?? $"ROOT{i.DriveId[2..]}", ["path"] = i.ParentId is null ? $"/drives/{i.DriveId}/root:" : $"/drives/{i.DriveId}/root:/x",
            },
        };
        if (i.Folder)
        {
            body["folder"] = new { childCount = _spItems.Values.Count(c => c.ParentId == i.Id && !c.Deleted) };
        }
        else
        {
            body["file"] = new { mimeType = FileMime(i.Name) };
            body["size"] = i.Content.Length;
            if (i.LabelId is not null)
            {
                body["sensitivityLabel"] = new { labelId = i.LabelId, displayName = i.LabelName };
            }
        }
        if (withDeleted && i.Deleted)
        {
            body["deleted"] = new { state = "deleted" };
        }
        return body;
    }

    private static string FileMime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    private HttpResponseMessage SpChildren(string driveId, string folder, IReadOnlyDictionary<string, string> query, Func<SpItem, bool> visible)
    {
        var parent = folder == "root" ? null : folder;
        if (parent is not null && (!_spItems.TryGetValue(parent, out var p) || p.DriveId != driveId || p.Deleted || !p.Folder || !visible(p)))
        {
            return SpMissing();
        }
        var all = _spItems.Values.Where(i => i.DriveId == driveId && i.ParentId == parent && !i.Deleted && visible(i)).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        var top = query.TryGetValue("$top", out var t) && int.TryParse(t, out var n) ? Math.Clamp(n, 1, 200) : 200;
        var skip = query.TryGetValue("$skiptoken", out var k) && int.TryParse(k, out var m) ? m : 0;
        var body = new Dictionary<string, object?> { ["value"] = all.Skip(skip).Take(top).Select(i => SpItemJson(i)).ToList() };
        if (skip + top < all.Count)
        {
            body["@odata.nextLink"] = $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{folder}/children?$top={top}&$skiptoken={skip + top}";
        }
        return Json(body);
    }

    /// <summary>Application calls: resolve a site by address, list its drives, list and read items with permissions, and delta.</summary>
    private HttpResponseMessage SharePointApp(string directory, string path, IReadOnlyDictionary<string, string> query)
    {
        if (!_directories.TryGetValue(directory, out var d) || !d.Roles.Contains("Sites.Selected"))
        {
            return SpDenied();
        }
        const string sites = "/v1.0/sites/";
        if (path.StartsWith(sites, StringComparison.Ordinal))
        {
            var rest = path[sites.Length..];
            if (rest.EndsWith("/drives", StringComparison.Ordinal))
            {
                var siteId = rest[..^"/drives".Length];
                return _spSites.TryGetValue(siteId, out var site) && site.Directory == directory && site.Granted
                    ? Json(new { value = _spDrives.Values.Where(x => x.SiteId == siteId).OrderBy(x => x.Name).Select(x => new { id = x.Id, name = x.Name }) })
                    : SpMissing();
            }
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            var found = colon > 0
                ? _spSites.Values.FirstOrDefault(x => x.Directory == directory && x.Host == rest[..colon].ToLowerInvariant()
                    && string.Equals(x.Path.TrimEnd('/'), rest[(colon + 1)..].TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                : _spSites.GetValueOrDefault(rest);
            return found is { Granted: true } && found.Directory == directory
                ? Json(new { id = found.Id, displayName = found.Name, webUrl = $"https://{found.Host}{(found.Path == "/" ? string.Empty : found.Path)}" })
                : SpMissing();
        }
        var segments = path["/v1.0/drives/".Length..].Split('/');
        var driveId = segments[0];
        if (!SpDriveGranted(directory, driveId))
        {
            return SpMissing();
        }
        if (segments is [_, "root", "delta"])
        {
            var since = query.TryGetValue("token", out var tk) && tk != "latest" && long.TryParse(tk, out var v) ? v : -1;
            var now = Interlocked.Read(ref _spVersion);
            var changed = since < 0 ? [] : _spItems.Values.Where(i => i.DriveId == driveId && i.Version > since).OrderBy(i => i.Version).Select(i => SpItemJson(i, true)).ToList();
            return Json(new Dictionary<string, object?>
            {
                ["value"] = changed, ["@odata.deltaLink"] = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/delta?token={now}",
            });
        }
        if (segments is [_, "items", var folder, "children"])
        {
            return SpChildren(driveId, folder, query, _ => true);
        }
        if (segments is [_, "items", var id, ..] && _spItems.TryGetValue(id, out var item) && item.DriveId == driveId && !item.Deleted)
        {
            if (segments.Length == 3)
            {
                return Json(SpItemJson(item));
            }
            if (segments is [_, _, _, "permissions"])
            {
                // Items open to everyone inherit the site's Members group, which SharePoint reports as a site group.
                object[] value = item.Readers.Count == 0
                    ? [new { id = "members", roles = new[] { "write" }, grantedToV2 = new { siteGroup = new { displayName = "Members", id = "5" } } }]
                    : [.. item.Readers.Select(r => (object)new { id = r, roles = new[] { "read" }, grantedToV2 = new { user = new { email = r } } })];
                return Json(new { value });
            }
        }
        return SpMissing();
    }

    /// <summary>
    /// Delegated calls with Files.Read.All: children, item and content, limited to what the person may read. With
    /// Files.ReadWrite.All also upload by path (replacing a file of the same name as a new version) and rename.
    /// </summary>
    private async Task<HttpResponseMessage> SharePointUserAsync(HttpRequestMessage request, string directory, string path, IReadOnlyDictionary<string, string> query,
        string? reader, IReadOnlySet<string> scopes, CancellationToken ct)
    {
        if (!scopes.Overlaps(["Files.Read.All", "Files.ReadWrite.All"]))
        {
            return SpDenied();
        }
        var segments = path["/v1.0/drives/".Length..].Split('/');
        var driveId = segments[0];
        if (!_spDrives.TryGetValue(driveId, out var drive) || !_spSites.TryGetValue(drive.SiteId, out var site) || site.Directory != directory)
        {
            return SpMissing();
        }
        bool Visible(SpItem i) => i.Readers.Count == 0 || (reader is not null && i.Readers.Contains(reader));
        if (request.Method != HttpMethod.Get)
        {
            return await SharePointWriteAsync(request, driveId, path, scopes, Visible, ct);
        }
        if (segments is [_, "items", var folder, "children"])
        {
            return SpChildren(driveId, folder, query, Visible);
        }
        if (segments is [_, "items", var id, ..] && _spItems.TryGetValue(id, out var item) && item.DriveId == driveId && !item.Deleted)
        {
            if (!Visible(item))
            {
                return SpDenied();
            }
            if (segments.Length == 3)
            {
                return Json(SpItemJson(item));
            }
            if (segments is [_, _, _, "content"])
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(item.Content) };
            }
        }
        return SpMissing();
    }

    private async Task<HttpResponseMessage> SharePointWriteAsync(HttpRequestMessage request, string driveId, string path, IReadOnlySet<string> scopes,
        Func<SpItem, bool> visible, CancellationToken ct)
    {
        if (!scopes.Contains("Files.ReadWrite.All"))
        {
            return SpDenied();
        }
        var rest = path[$"/v1.0/drives/{driveId}/items/".Length..];
        if (request.Method == HttpMethod.Put && rest.EndsWith(":/content", StringComparison.Ordinal) && rest.IndexOf(":/", StringComparison.Ordinal) is var colon and > 0)
        {
            var folder = rest[..colon];
            var name = rest[(colon + 2)..^":/content".Length];
            SpItem? parent = null;
            if (folder != "root" && (!_spItems.TryGetValue(folder, out parent) || parent.DriveId != driveId || parent.Deleted || !parent.Folder || !visible(parent)))
            {
                return SpMissing();
            }
            if (parent is { Readers.Count: > 0 })
            {
                return SpDenied();
            }
            var content = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            var parentId = parent?.Id;
            var existing = _spItems.Values.FirstOrDefault(i => i.DriveId == driveId && i.ParentId == parentId && !i.Deleted
                && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is { Folder: true })
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":{\"code\":\"nameAlreadyExists\"}}") };
            }
            if (existing is not null)
            {
                EditSharePointItem(existing.Id, content: content);
                return Json(SpItemJson(_spItems[existing.Id]));
            }
            var id = AddSharePointItem(driveId, parentId, name, content: content);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(SpItemJson(_spItems[id])) };
        }
        if (request.Method == HttpMethod.Patch && !rest.Contains('/', StringComparison.Ordinal) && _spItems.TryGetValue(rest, out var item) && item.DriveId == driveId
            && !item.Deleted)
        {
            if (!visible(item))
            {
                return SpMissing();
            }
            if (item.Readers.Count > 0)
            {
                return SpDenied();
            }
            var body = request.Content is null ? default : await request.Content.ReadFromJsonAsync<JsonElement>(ct);
            var name = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"code\":\"invalidRequest\"}}") };
            }
            if (_spItems.Values.Any(i => i.DriveId == driveId && i.ParentId == item.ParentId && i.Id != item.Id && !i.Deleted
                && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":{\"code\":\"nameAlreadyExists\"}}") };
            }
            EditSharePointItem(item.Id, name: name);
            return Json(SpItemJson(_spItems[item.Id]));
        }
        return SpMissing();
    }

    /// <summary>A file in an employee's OneDrive. <see cref="Owner"/> is the UPN, or directory/user ID for directory users.</summary>
    public sealed record DriveItem(string Id, string Owner, string Name, byte[] Content);

    private readonly ConcurrentDictionary<string, DriveItem> _drive = new(StringComparer.Ordinal);

    /// <summary>Files in one employee's OneDrive (by UPN), for tests.</summary>
    public IReadOnlyList<DriveItem> DriveOf(string upn) => _drive.Values.Where(i => i.Owner == upn.ToLowerInvariant()).ToList();

    /// <summary>Changes a file as the Office web editor would.</summary>
    public void EditDriveItem(string id, byte[] content) => _drive[id] = _drive[id] with { Content = content };

    /// <summary>Removes a file as the employee would in OneDrive.</summary>
    public void RemoveDriveItem(string id) => _drive.TryRemove(id, out _);

    /// <summary>OneDrive with Files.ReadWrite: upload by path into a folder (renaming on a clash), read content, delete.</summary>
    private async Task<HttpResponseMessage> DriveAsync(HttpRequestMessage request, string path, string owner, IReadOnlySet<string> scopes, CancellationToken ct)
    {
        if (!scopes.Contains("Files.ReadWrite"))
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"accessDenied\"}}") };
        }
        var notFound = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"itemNotFound\"}}") };
        const string root = "/v1.0/me/drive/root:/";
        const string items = "/v1.0/me/drive/items/";
        if (request.Method == HttpMethod.Put && path.StartsWith(root, StringComparison.Ordinal) && path.EndsWith(":/content", StringComparison.Ordinal))
        {
            var full = path[root.Length..^":/content".Length];
            var folder = full.Contains('/', StringComparison.Ordinal) ? full[..full.LastIndexOf('/')] : string.Empty;
            var name = full[(full.LastIndexOf('/') + 1)..];
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var n = 1; _drive.Values.Any(i => i.Owner == owner && i.Name == $"{folder}/{name}"); n++)
            {
                name = $"{stem} {n}{extension}";
            }
            var content = await request.Content!.ReadAsByteArrayAsync(ct);
            var id = "01FAKE" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
            _drive[id] = new DriveItem(id, owner, $"{folder}/{name}", content);
            return Json(new { id, name, size = content.Length, webUrl = $"https://fake-onedrive.example/edit/{id}" });
        }
        if (!path.StartsWith(items, StringComparison.Ordinal))
        {
            return notFound;
        }
        var rest = path[items.Length..];
        var itemId = rest.EndsWith("/content", StringComparison.Ordinal) ? rest[..^"/content".Length] : rest;
        if (!_drive.TryGetValue(itemId, out var item) || item.Owner != owner)
        {
            return notFound;
        }
        if (request.Method == HttpMethod.Get && rest.EndsWith("/content", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(item.Content) };
        }
        if (request.Method == HttpMethod.Delete && rest == itemId)
        {
            _drive.TryRemove(itemId, out _);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        return notFound;
    }

    private static string? UpnOf(string bearer)
    {
        var part = bearer.Split('.').ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(part))
        {
            return null;
        }
        var b64 = part.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        return doc.RootElement.TryGetProperty("upn", out var upn) && upn.ValueKind == JsonValueKind.String ? upn.GetString() : null;
    }

    /// <summary>The UPN-keyed mailbox path: Inbox count and messages, calendar view and getSchedule, with scope checks.</summary>
    private async Task<HttpResponseMessage> UpnMailboxAsync(HttpRequestMessage request, Uri uri, IReadOnlySet<string> scopes, string upn, CancellationToken ct)
    {
        var mailbox = MailboxFor(upn);
        var path = uri.AbsolutePath["/v1.0/me/".Length..];
        var query = FakeIdentityProvider.ParseQuery(uri.Query);
        static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
        var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"ErrorAccessDenied\"}}") };
        var noMailbox = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"MailboxNotEnabledForRESTAPI\"}}") };

        if (path.StartsWith("mailFolders/inbox", StringComparison.Ordinal))
        {
            var select = query.GetValueOrDefault("$select") ?? string.Empty;
            if ((!scopes.Contains("Mail.ReadBasic") && !scopes.Contains("Mail.Read")) || (select.Contains("bodyPreview", StringComparison.Ordinal) && !scopes.Contains("Mail.Read")))
            {
                return forbidden;
            }
            if (!mailbox.Exists)
            {
                return noMailbox;
            }
            if (path == "mailFolders/inbox")
            {
                return Json(new { unreadItemCount = mailbox.UnreadCount });
            }
            var focused = (query.GetValueOrDefault("$filter") ?? string.Empty).Contains("focused", StringComparison.Ordinal);
            var top = int.Parse(query.GetValueOrDefault("$top") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
            var items = mailbox.Messages.Where(m => !focused || m.Focused).OrderByDescending(m => m.Received).Take(top)
                .Select(m =>
                {
                    var item = new Dictionary<string, object?>
                    {
                        ["id"] = m.Id, ["subject"] = m.Subject, ["from"] = new { emailAddress = new { name = m.FromName, address = m.FromAddress } },
                        ["receivedDateTime"] = m.Received.ToString("o", System.Globalization.CultureInfo.InvariantCulture), ["isRead"] = m.IsRead,
                        ["importance"] = m.Importance, ["hasAttachments"] = m.HasAttachments, ["webLink"] = $"https://outlook.office365.com/owa/?ItemID={m.Id}",
                    };
                    if (select.Contains("bodyPreview", StringComparison.Ordinal))
                    {
                        item["bodyPreview"] = m.Preview;
                    }
                    return item;
                }).ToList();
            return Json(new { value = items });
        }
        if (path == "calendarView")
        {
            if (!scopes.Contains("Calendars.ReadBasic"))
            {
                return forbidden;
            }
            if (!mailbox.Exists)
            {
                return noMailbox;
            }
            var from = DateTimeOffset.Parse(query["startDateTime"], System.Globalization.CultureInfo.InvariantCulture);
            var to = DateTimeOffset.Parse(query["endDateTime"], System.Globalization.CultureInfo.InvariantCulture);
            var items = mailbox.Events.Where(e => e.End > from && e.Start < to).OrderBy(e => e.Start).Select(e => new Dictionary<string, object?>
            {
                ["id"] = e.Id, ["subject"] = e.Subject, ["start"] = new { dateTime = Time(e.Start), timeZone = "UTC" }, ["end"] = new { dateTime = Time(e.End), timeZone = "UTC" },
                ["isAllDay"] = e.AllDay, ["showAs"] = e.ShowAs, ["sensitivity"] = e.Sensitivity, ["isCancelled"] = e.Cancelled,
                ["responseStatus"] = new { response = e.Response }, ["location"] = new { displayName = e.Location ?? string.Empty },
                ["onlineMeeting"] = e.JoinUrl is null ? null : new { joinUrl = e.JoinUrl }, ["webLink"] = $"https://outlook.office365.com/owa/?itemid={e.Id}&path=/calendar/item",
                ["iCalUId"] = e.ICalUId ?? $"{e.Id}@fake.outlook",
            }).ToList();
            return Json(new { value = items });
        }
        if (path == "calendar/getSchedule" && request.Method == HttpMethod.Post)
        {
            if (!scopes.Contains("Calendars.ReadBasic"))
            {
                return forbidden;
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var values = body.RootElement.GetProperty("schedules").EnumerateArray().Select(x => x.GetString()!).Select(address => _schedules.TryGetValue(address, out var items)
                ? (object)new { scheduleId = address, scheduleItems = items.Select(i => new { status = i.Status, start = new { dateTime = Time(i.Start), timeZone = "UTC" }, end = new { dateTime = Time(i.End), timeZone = "UTC" } }) }
                : new { scheduleId = address, error = new { message = "Not shared", responseCode = "ErrorNoFreeBusyAccess" } }).ToList();
            return Json(new { value = values });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Every calendar event of a directory's mailboxes, once each.</summary>
    public IReadOnlyList<Event> DirectoryEvents(Guid directoryId) => AllEvents(directoryId);

    /// <summary>True when a UPN mailbox was set up for this address (a person without a directory account).</summary>
    public bool HasUpnMailbox(string upn) => _upnMailboxes.ContainsKey(upn);

    private List<Event> AllEvents(Guid directoryId)
    {
        var prefix = directoryId.ToString("D") + "/";
        return _mailboxes.Where(m => m.Key.StartsWith(prefix, StringComparison.Ordinal)).SelectMany(m => m.Value.Events)
            .DistinctBy(e => e.Id).ToList();
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
