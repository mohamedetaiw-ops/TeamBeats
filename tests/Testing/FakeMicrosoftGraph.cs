using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeamBeats.Testing;

/// <summary>
/// In-memory Microsoft identity platform and Graph for the TeamBeats application: client credentials tokens for
/// directories that granted admin consent, and object counts. All directories and numbers are fictional.
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

    public sealed record Directory(IReadOnlyList<string> Roles, long Users, long Groups);

    /// <summary>The customer's admin granted these application permissions to the TeamBeats application.</summary>
    public void Consent(Guid directoryId, IReadOnlyList<string> roles, long users = 3400, long groups = 120) =>
        _directories[directoryId.ToString("D")] = new Directory(roles, users, groups);

    // Mailboxes and calendars for delegated reads (spec 018), keyed by the signed-in user's UPN. All content is fictional.

    public sealed record FakeMessage(string Id, string FromName, string FromAddress, string Subject, DateTimeOffset Received, bool IsRead = false,
        string Importance = "normal", bool HasAttachments = false, string Preview = "", bool Focused = true);

    public sealed record FakeEvent(string Id, string Subject, DateTimeOffset Start, DateTimeOffset End, bool AllDay = false, string ShowAs = "busy",
        string Sensitivity = "normal", bool Cancelled = false, string Response = "accepted", string? Location = null, string? JoinUrl = null);

    public sealed record FakeScheduleItem(string Status, DateTimeOffset Start, DateTimeOffset End);

    public sealed class FakeMailbox
    {
        public List<FakeMessage> Messages { get; } = [];
        public List<FakeEvent> Events { get; } = [];
        public int UnreadCount { get; set; }
        public bool Exists { get; set; } = true;
    }

    private readonly ConcurrentDictionary<string, FakeMailbox> _mailboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<FakeScheduleItem>> _schedules = new(StringComparer.OrdinalIgnoreCase);

    public FakeMailbox Mailbox(string upn) => _mailboxes.GetOrAdd(upn, _ => new FakeMailbox());

    /// <summary>Free and busy that the colleague shares; a colleague without an entry does not share.</summary>
    public void Schedule(string upn, params FakeScheduleItem[] items) => _schedules[upn] = [.. items];

    /// <summary>Status codes the next delegated Graph calls answer with, one per call (429 carries Retry-After: 0).</summary>
    public ConcurrentQueue<int> DelegatedFaults { get; } = new();

    /// <summary>Delegated Graph calls received, by path.</summary>
    public ConcurrentBag<string> DelegatedCalls { get; } = [];

    public void Revoke(Guid directoryId) => _directories.TryRemove(directoryId.ToString("D"), out _);

    private readonly ConcurrentDictionary<string, HashSet<string>> _delegated = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Directory, string Challenge, string RedirectUri, string[] Scopes, string Upn)> _codes = new(StringComparer.Ordinal);

    /// <summary>Delegated permissions consented for the TeamBeats application in a directory (plain names such as Mail.ReadBasic).</summary>
    public void ConsentDelegated(Guid directoryId, params string[] scopes) =>
        _delegated[directoryId.ToString("D")] = new HashSet<string>(scopes, StringComparer.Ordinal);

    /// <summary>The next authorize answers with this error instead of a code (for example consent or conditional access).</summary>
    public (string Error, string Description)? NextAuthorizeError { get; set; }

    /// <summary>The next authorization code redemption fails with this error code.</summary>
    public string? NextTokenError { get; set; }

    /// <summary>Delegated access tokens issued, so tests can check they never leave the server.</summary>
    public ConcurrentBag<string> IssuedDelegatedTokens { get; } = [];

    /// <summary>Plays Microsoft's authorize page: returns the query Microsoft appends to the redirect URI.</summary>
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
        var scopes = q["scope"].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Replace("https://graph.microsoft.com/", string.Empty, StringComparison.Ordinal)).ToArray();
        if (q.GetValueOrDefault("client_id") != ClientId || q.GetValueOrDefault("code_challenge_method") != "S256"
            || !_delegated.TryGetValue(directory, out var consented) || !scopes.All(consented.Contains))
        {
            return $"?error=consent_required&error_description={Uri.EscapeDataString("AADSTS65001: The user or administrator has not consented.")}&state={state}";
        }
        var code = "code-" + Guid.NewGuid().ToString("N");
        _codes[code] = (directory, q["code_challenge"], q["redirect_uri"], scopes, q.GetValueOrDefault("login_hint") ?? string.Empty);
        return $"?code={code}&state={state}";
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
                return RedeemCode(directory, form);
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
        if (uri.Host == "graph.microsoft.com" && uri.AbsolutePath.StartsWith("/v1.0/me/", StringComparison.Ordinal))
        {
            return await DelegatedAsync(request, uri, cancellationToken);
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
            var since = query.TryGetValue("$deltatoken", out var token) ? long.Parse(token, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var skip = query.TryGetValue("$skiptoken", out var skipToken) ? int.Parse(skipToken, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var isUsers = uri.AbsolutePath.EndsWith("/users/delta", StringComparison.Ordinal);
            var isUnits = uri.AbsolutePath.EndsWith("/administrativeUnits/delta", StringComparison.Ordinal);
            var (items, version) = isUsers ? data.UserChanges(since) : data.GroupChanges(since, isUnits ? "admin_unit" : "group");
            var basePath = $"https://graph.microsoft.com/v1.0/{(isUsers ? "users" : isUnits ? "directory/administrativeUnits" : "groups")}/delta";
            var page = items.Skip(skip).Take(isUsers ? PageSize : int.MaxValue).ToList();
            var body = new Dictionary<string, object?> { ["value"] = page };
            if (isUsers && skip + PageSize < items.Count)
            {
                body["@odata.nextLink"] = $"{basePath}?$deltatoken={since}&$skiptoken={skip + PageSize}";
            }
            else
            {
                body["@odata.deltaLink"] = $"{basePath}?$deltatoken={version}";
            }
            return Json(body);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
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
        var parts = bearer.Split('.');
        if (parts.Length != 3 || !IssuedDelegatedTokens.Contains(bearer))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1])))).RootElement;
        var scopes = payload.GetProperty("scp").GetString()!.Split(' ').ToHashSet(StringComparer.Ordinal);
        var upn = payload.GetProperty("upn").GetString()!;
        var mailbox = Mailbox(upn);
        var path = uri.AbsolutePath["/v1.0/me/".Length..];
        var query = FakeIdentityProvider.ParseQuery(uri.Query);
        static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
        var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"ErrorAccessDenied\"}}") };
        var noMailbox = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"MailboxNotEnabledForRESTAPI\"}}") };

        if (path.StartsWith("mailFolders/inbox", StringComparison.Ordinal))
        {
            var select = query.GetValueOrDefault("$select") ?? string.Empty;
            if (!scopes.Contains("Mail.ReadBasic") && !scopes.Contains("Mail.Read") || (select.Contains("bodyPreview", StringComparison.Ordinal) && !scopes.Contains("Mail.Read")))
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
            }).ToList();
            return Json(new { value = items });
        }
        if (path == "calendar/getSchedule" && request.Method == HttpMethod.Post)
        {
            if (!scopes.Contains("Calendars.ReadBasic"))
            {
                return forbidden;
            }
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            var values = body.GetProperty("schedules").EnumerateArray().Select(x => x.GetString()!).Select(address => _schedules.TryGetValue(address, out var items)
                ? (object)new { scheduleId = address, scheduleItems = items.Select(i => new { status = i.Status, start = new { dateTime = Time(i.Start), timeZone = "UTC" }, end = new { dateTime = Time(i.End), timeZone = "UTC" } }) }
                : new { scheduleId = address, error = new { message = "Not shared", responseCode = "ErrorNoFreeBusyAccess" } }).ToList();
            return Json(new { value = values });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string Pad(string b64url)
    {
        var p = b64url.Replace('-', '+').Replace('_', '/');
        return p.PadRight(p.Length + ((4 - (p.Length % 4)) % 4), '=');
    }

    private HttpResponseMessage RedeemCode(string directory, IReadOnlyDictionary<string, string> form)
    {
        if (NextTokenError is { } error)
        {
            NextTokenError = null;
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($"{{\"error\":\"{error}\"}}") };
        }
        var verifier = form.GetValueOrDefault("code_verifier") ?? string.Empty;
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret
            || !_codes.TryRemove(form.GetValueOrDefault("code") ?? string.Empty, out var issued) || issued.Directory != directory
            || issued.Challenge != challenge || issued.RedirectUri != form.GetValueOrDefault("redirect_uri"))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
        }
        var payload = Base64Url(JsonSerializer.Serialize(new { tid = directory, scp = string.Join(' ', issued.Scopes), upn = issued.Upn }));
        var token = $"{Base64Url("{\"alg\":\"none\"}")}.{payload}.sig-{directory}";
        IssuedDelegatedTokens.Add(token);
        return Json(new { access_token = token, token_type = "Bearer", expires_in = 3600, scope = string.Join(' ', issued.Scopes.Append("openid").Append("profile")) });
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
