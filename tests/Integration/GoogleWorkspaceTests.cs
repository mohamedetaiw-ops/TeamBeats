using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Google;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 T019 (FR-019, INT-004) with spec 003 US6: the Google Workspace pack. A service account signs a JWT bearer
/// grant to act as the administrator or as a user; directory sync reads every user and group, keyed by Google ID, and
/// removes what Google no longer lists; Google Drive is read through the document repository contract only inside the
/// listed shared drives, and content opens as the user. Google is a fake of the test factory that checks each
/// assertion's signature with the test's own key.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class GoogleWorkspaceTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/pack-connections";
    private const string Account = "teambeats-sync@acme-intranet.iam.gserviceaccount.com";
    private const string Drive = "0AAbcDriveFinance1";
    private const string OtherDrive = "0AAbcDriveSecret99";

    private FakeRestApi Rest => world.Factory.Rest;

    /// <summary>A small Google Workspace: users, groups, two shared drives and who may read what.</summary>
    private sealed class FakeGoogle(RSA key, string domain)
    {
        public List<object> Users { get; } = [];
        public Dictionary<string, (string Name, string[] Members)> Groups { get; } = [];
        public HashSet<string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DriveReaders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Down { get; set; }
        public int UsersPageSize { get; set; } = 2;
        public List<(string Sub, string Scope)> Tokens { get; } = [];

        public string Admin => $"admin@{domain}";

        public HttpResponseMessage Token(FakeRestApi.Sent s)
        {
            var form = s.Body!.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);
            var parts = form["assertion"].Split('.');
            var valid = key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var claims = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
            var sub = claims.GetProperty("sub").GetString()!;
            if (!valid || claims.GetProperty("iss").GetString() != Account || claims.GetProperty("aud").GetString() != GoogleHosts.TokenUrl
                || Refused.Contains(sub) || !sub.EndsWith("@" + domain, StringComparison.Ordinal))
            {
                return FakeRestApi.Json(HttpStatusCode.BadRequest, """{"error":"unauthorized_client"}""");
            }
            var scope = claims.GetProperty("scope").GetString()!;
            Tokens.Add((sub, scope));
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { access_token = $"tok:{sub}:{(scope.Contains("drive") ? "drive" : "dir")}", expires_in = 3600 }));
        }

        private static string? Sub(FakeRestApi.Sent s) =>
            s.Headers.TryGetValue("Authorization", out var a) && a.StartsWith("Bearer tok:", StringComparison.Ordinal) ? a["Bearer tok:".Length..].Split(':')[0] : null;

        private static Dictionary<string, string> Query(Uri u) =>
            u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        public HttpResponseMessage Directory(FakeRestApi.Sent s)
        {
            if (Down)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            // A revoked delegation refuses the tokens already given out too.
            if (Sub(s) != Admin || Refused.Contains(Admin))
            {
                return FakeRestApi.Json(HttpStatusCode.Forbidden, "{}");
            }
            var q = Query(s.Uri);
            var path = s.Uri.AbsolutePath["/admin/directory/v1/".Length..].Split('/');
            if (path[0] == "users")
            {
                var start = q.TryGetValue("pageToken", out var p) ? int.Parse(p, System.Globalization.CultureInfo.InvariantCulture) : 0;
                var page = Users.Skip(start).Take(UsersPageSize).ToList();
                var next = start + UsersPageSize < Users.Count ? (start + UsersPageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { users = page, nextPageToken = next }));
            }
            if (path is ["groups"])
            {
                return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { groups = Groups.Select(g => new { id = g.Key, name = g.Value.Name }) }));
            }
            var members = Groups[path[1]].Members.Select(m => new { id = m, type = "USER" }).Append(new { id = "nested-group", type = "GROUP" });
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { members }));
        }

        public HttpResponseMessage DriveApi(FakeRestApi.Sent s)
        {
            var sub = Sub(s);
            if (sub is null)
            {
                return FakeRestApi.Json(HttpStatusCode.Unauthorized, "{}");
            }
            var path = s.Uri.AbsolutePath["/drive/v3/".Length..].Split('/');
            var q = Query(s.Uri);
            object File(string id, string drive, string name, string type = "application/pdf") =>
                new { id, name, mimeType = type, size = "1234", modifiedTime = "2026-11-10T08:00:00Z", version = "7", webViewLink = $"https://drive.google.com/file/d/{id}", parents = new[] { drive }, driveId = drive, trashed = false };
            return path switch
            {
                ["drives", var id] => id is Drive or OtherDrive ? Ok(new { id, name = id == Drive ? "Finance" : "Board" }) : FakeRestApi.Json(HttpStatusCode.NotFound, "{}"),
                ["files"] => Ok(new
                {
                    files = q["driveId"] == Drive ? new[] { File("fileA111", Drive, "Budget 2027.pdf"), File("folderB22", Drive, "Reports", "application/vnd.google-apps.folder") } : [],
                }),
                ["files", var id, "permissions"] => Ok(new
                {
                    permissions = new object[]
                    {
                        new { type = "user", role = "writer", emailAddress = "Alice@acme.example" },
                        new { type = "domain", role = "reader", domain = "acme.example" },
                        new { type = "user", role = "owner", emailAddress = "gone@acme.example", deleted = true },
                    },
                }),
                ["files", "docC333"] when sub != Admin && !DriveReaders.Contains(sub) => FakeRestApi.Json(HttpStatusCode.NotFound, "{}"),
                ["files", "docC333"] => Ok(File("docC333", Drive, "Plan", "application/vnd.google-apps.document")),
                ["files", "docC333", "export"] => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-plan"u8.ToArray()) },
                ["files", "fileA111"] when q.ContainsKey("alt") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-budget"u8.ToArray()) },
                ["files", "fileA111"] => Ok(File("fileA111", Drive, "Budget 2027.pdf")),
                ["files", "secretD44"] => Ok(File("secretD44", OtherDrive, "Board pack.pdf")),
                ["changes", "startPageToken"] => Ok(new { startPageToken = "100" }),
                ["changes"] => Ok(new
                {
                    newStartPageToken = "101",
                    changes = new object[]
                    {
                        new { fileId = "fileA111", removed = false, file = File("fileA111", Drive, "Budget 2027 v2.pdf") },
                        new { fileId = "oldE555", removed = true },
                        new { fileId = "movedF6", removed = false, file = File("movedF6", OtherDrive, "Moved.pdf") },
                    },
                }),
                _ => FakeRestApi.Json(HttpStatusCode.NotFound, "{}"),
            };
        }

        private static HttpResponseMessage Ok(object body) => FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(body));

        private static byte[] FromBase64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private sealed record Setup(TestTenant Tenant, Guid Connection, FakeGoogle Google, string Domain);

    private static object User(string id, string email, string name, bool suspended = false, string? manager = null) => new
    {
        id,
        primaryEmail = email,
        name = new { fullName = name },
        suspended,
        organizations = new[] { new { title = "Analyst", department = "Finance", name = "Acme", primary = true } },
        phones = new[] { new { type = "work", value = "+966 11 000 0000" } },
        externalIds = new[] { new { type = "organization", value = "E-" + id[^3..] } },
        relations = manager is null ? [] : new[] { new { type = "manager", value = manager } },
    };

    private async Task<Setup> SetupAsync(string[] features, bool allowDrive = true)
    {
        var t = await world.CreateTenantAsync("gw-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var domain = $"{t.Key}.example";
        var key = RSA.Create(2048);
        var google = new FakeGoogle(key, domain);
        Rest.On("oauth2.googleapis.com", google.Token);
        Rest.On("admin.googleapis.com", google.Directory);
        Rest.On("www.googleapis.com", google.DriveApi);
        var admin = t["admin"].Client;
        foreach (var host in new[] { "oauth2.googleapis.com", "admin.googleapis.com" }.Concat(allowDrive ? ["www.googleapis.com"] : []))
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        }
        // The key file Google gives, made by the test: the client email must match the settings.
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "google-key"), JsonSerializer.Serialize(new { type = "service_account", client_email = Account, private_key = key.ExportPkcs8PrivateKeyPem() }));
        var created = await admin.PostAsJsonAsync(Packs, new
        {
            kind = GooglePack.KindName,
            displayName = "Google Workspace",
            settings = new { domain = domain.ToUpperInvariant(), adminEmail = google.Admin, serviceAccountEmail = Account, privateKeyRef = SecretReferences.For(t.Id, "google-key"), features, sharedDrives = new[] { Drive } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{id}:enable", null)).StatusCode);
        return new Setup(t, id, google, domain);
    }

    private Task<SyncRun?> RunAsync(Setup s) =>
        world.Factory.InScopeAsync(s.Tenant.Id, async sp =>
        {
            var run = await sp.GetRequiredService<DirectorySyncService>().RunAsync(s.Connection, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });

    private Task<T> RepoAsync<T>(TestTenant t, Func<IDocumentRepository, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, sp => action(sp.GetRequiredService<IDocumentRepository>()));

    private Task<Guid?> UserIdAsync(Setup s, string googleId) =>
        world.Database.TenantScalarAsync<Guid?>(s.Tenant.Id, "select user_id from identity.directory_user_link where directory_id = @d", ("d", googleId));

    private Task<string?> MembershipStatusAsync(Setup s, Guid userId) =>
        world.Database.TenantScalarAsync<string>(s.Tenant.Id, "select status from tenancy.tenant_membership where user_id = @u", ("u", userId));

    private Task MakeDueAsync(Setup s) =>
        world.Database.TenantScalarAsync<long>(s.Tenant.Id, "with u as (update identity.directory_connection set next_run_at = now() returning 1) select count(*) from u");

    [Fact]
    public async Task Directory_sync_reads_every_google_user_by_id_keeps_groups_and_disables_suspended_and_removed_users()
    {
        var s = await SetupAsync([GoogleFeatures.Directory]);
        var test = await (await s.Tenant["admin"].Client.PostAsync($"{Packs}/{s.Connection}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());
        Assert.Contains((s.Google.Admin, GoogleHosts.DirectoryScope), s.Google.Tokens);

        s.Google.Users.AddRange([
            User("100000000000000000001", $"alice@{s.Domain}", "Alice Hassan", manager: $"BOB@{s.Domain}"),
            User("100000000000000000002", $"bob@{s.Domain}", "Bob Saleh"),
            User("100000000000000000003", $"carol@{s.Domain}", "Carol Off", suspended: true),
        ]);
        s.Google.Groups["03ep43zb3x7ys8e"] = ("Finance", ["100000000000000000001", "100000000000000000002"]);

        var saved = await s.Tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory",
            new { connectionId = s.Connection, syncGroups = new[] { "03ep43zb3x7ys8e" }, userScope = new { mode = "all", groups = Array.Empty<string>() }, deletionThresholdPct = 100 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("google", (await saved.ReadAsync<JsonElement>()).GetProperty("source").GetString());

        var run = await RunAsync(s);
        Assert.Equal("succeeded", run!.Outcome);
        Assert.Equal(2, run.Created);
        Assert.Equal(1, run.Groups);
        var alice = (await UserIdAsync(s, "100000000000000000001"))!.Value;
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(s.Tenant.Id,
            "select count(*) from identity.external_identity where user_id = @u and issuer = @i and subject = @d",
            ("u", alice), ("i", GoogleDirectory.Issuer), ("d", "100000000000000000001")));
        Assert.Null(await UserIdAsync(s, "100000000000000000003"));
        Assert.Equal("100000000000000000002", await world.Database.TenantScalarAsync<string>(s.Tenant.Id,
            "select attributes->>'manager' from identity.directory_user where directory_id = @d", ("d", "100000000000000000001")));
        Assert.Equal("100000000000000000001,100000000000000000002", await world.Database.TenantScalarAsync<string>(s.Tenant.Id,
            "select array_to_string(array(select unnest(members) order by 1), ',') from identity.directory_group where directory_id = @d", ("d", "03ep43zb3x7ys8e")));

        // Bob is deleted in Google: a complete read no longer lists him, so he loses access; the group follows.
        s.Google.Users.RemoveAt(1);
        s.Google.Groups["03ep43zb3x7ys8e"] = ("Finance", ["100000000000000000001"]);
        var bob = (await UserIdAsync(s, "100000000000000000002"))!.Value;
        await MakeDueAsync(s);
        var second = await RunAsync(s);
        Assert.Equal("succeeded", second!.Outcome);
        Assert.Equal(1, second.Disabled);
        Assert.Equal("disabled", await MembershipStatusAsync(s, bob));
        Assert.Equal("active", await MembershipStatusAsync(s, alice));
    }

    [Fact]
    public async Task A_down_or_refusing_google_changes_nothing_and_a_short_list_is_stopped_by_the_threshold()
    {
        var s = await SetupAsync([GoogleFeatures.Directory]);
        s.Google.Users.AddRange([User("200000000000000000001", $"dana@{s.Domain}", "Dana"), User("200000000000000000002", $"omar@{s.Domain}", "Omar"),
            User("200000000000000000003", $"lina@{s.Domain}", "Lina")]);
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { connectionId = s.Connection, deletionThresholdPct = 10 })).StatusCode);
        Assert.Equal(3, (await RunAsync(s))!.Created);

        s.Google.Down = true;
        await MakeDueAsync(s);
        var down = await RunAsync(s);
        Assert.Equal(("failed", "unreachable"), (down!.Outcome, down.Reason));

        s.Google.Down = false;
        s.Google.Refused.Add(s.Google.Admin);
        await MakeDueAsync(s);
        var refused = await RunAsync(s);
        Assert.Equal(("failed", "consent_missing"), (refused!.Outcome, refused.Reason));

        // Google answers with one user only: removing two of three is over the threshold, so nothing is applied.
        s.Google.Refused.Clear();
        s.Google.Users.RemoveRange(1, 2);
        await MakeDueAsync(s);
        Assert.Equal("stopped_threshold", (await RunAsync(s))!.Outcome);
        Assert.Equal("active", await MembershipStatusAsync(s, (await UserIdAsync(s, "200000000000000000003"))!.Value));
    }

    [Fact]
    public async Task Drive_is_read_only_inside_listed_shared_drives_and_content_opens_as_the_user()
    {
        var s = await SetupAsync([GoogleFeatures.Drive]);
        var t = s.Tenant;

        var containers = await RepoAsync(t, r => r.ContainersAsync(s.Connection));
        Assert.Equal([new RepositoryContainer(Drive, "Finance")], containers);
        var page = await RepoAsync(t, r => r.ListAsync(s.Connection, Drive, null, null));
        Assert.Equal(["fileA111", "folderB22"], page.Items.Select(i => i.Id));
        Assert.True(page.Items[1].Folder);
        Assert.Null(page.Items[0].ParentId);
        await Assert.ThrowsAsync<RepositoryUnavailableException>(() => RepoAsync(t, r => r.ListAsync(s.Connection, OtherDrive, null, null)));

        var item = await RepoAsync(t, r => r.GetAsync(s.Connection, "fileA111"));
        Assert.Equal(2, item!.Permissions!.Count);
        Assert.Contains(new RepositoryPermission(new RepositoryPrincipal("user", "alice@acme.example", null), "write"), item.Permissions);
        Assert.Contains(new RepositoryPermission(new RepositoryPrincipal("domain", null, "acme.example"), "read"), item.Permissions);
        // A file of a drive that is not listed is never returned, even though the administrator can read it.
        Assert.Null(await RepoAsync(t, r => r.GetAsync(s.Connection, "secretD44")));
        Assert.Null(await RepoAsync(t, r => r.GetAsync(s.Connection, "../etc")));

        var start = await RepoAsync(t, r => r.ChangesAsync(s.Connection, Drive, null));
        Assert.Equal(("100", 0), (start.Cursor, start.Changes.Count));
        var changes = await RepoAsync(t, r => r.ChangesAsync(s.Connection, Drive, start.Cursor));
        Assert.Equal("101", changes.Cursor);
        Assert.Equal("Budget 2027 v2.pdf", changes.Changes[0].Item!.Name);
        Assert.True(changes.Changes[1].Removed);
        Assert.True(changes.Changes[2].Removed);

        // Content opens as the user: Drive decides. Google Docs open as PDF.
        await using (var content = await RepoAsync(t, r => r.OpenAsync(s.Connection, "fileA111", $"Alice@{s.Domain}")))
        {
            Assert.Equal("application/pdf", content!.MediaType);
            using var reader = new StreamReader(content.Content);
            Assert.Equal("%PDF-budget", await reader.ReadToEndAsync());
        }
        Assert.Contains(($"alice@{s.Domain}", GoogleHosts.DriveScope), s.Google.Tokens);
        Assert.Null(await RepoAsync(t, r => r.OpenAsync(s.Connection, "docC333", $"bob@{s.Domain}")));
        s.Google.DriveReaders.Add($"bob@{s.Domain}");
        await using (var doc = await RepoAsync(t, r => r.OpenAsync(s.Connection, "docC333", $"bob@{s.Domain}")))
        {
            Assert.Equal(("application/pdf", "Plan.pdf"), (doc!.MediaType, doc.Name));
        }
        Assert.Null(await RepoAsync(t, r => r.OpenAsync(s.Connection, "fileA111", "guest@elsewhere.example")));
        Assert.Null(await RepoAsync(t, r => r.OpenAsync(s.Connection, "secretD44", $"alice@{s.Domain}")));

        // Directory is off on this connection, so directory sync cannot use it.
        var dir = await t["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { connectionId = s.Connection });
        Assert.Equal(HttpStatusCode.Conflict, dir.StatusCode);
    }

    [Fact]
    public async Task Settings_are_checked_and_the_package_and_tenant_boundary_hold()
    {
        var s = await SetupAsync([GoogleFeatures.Directory, GoogleFeatures.Drive]);
        var admin = s.Tenant["admin"].Client;
        var bad = await admin.PostAsJsonAsync(Packs, new
        {
            kind = GooglePack.KindName,
            displayName = "Google",
            settings = new
            {
                domain = "not a domain",
                adminEmail = "admin@elsewhere.example",
                serviceAccountEmail = "someone@gmail.com",
                privateKeyRef = SecretReferences.For(Guid.NewGuid(), "google-key"),
                customerId = "x",
                features = new[] { "directory", "calendar" },
                sharedDrives = new[] { "bad id!" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var text = await bad.Content.ReadAsStringAsync();
        foreach (var key in new[] { "validation.google.domain", "validation.google.admin_email", "validation.google.service_account", "validation.custom_rest.secret_ref",
            "validation.google.customer_id", "validation.google.features" })
        {
            Assert.Contains(key, text, StringComparison.Ordinal);
        }
        var drives = await admin.PostAsJsonAsync(Packs, new
        {
            kind = GooglePack.KindName,
            displayName = "Google",
            settings = new { domain = s.Domain, adminEmail = s.Google.Admin, serviceAccountEmail = Account, privateKeyRef = SecretReferences.For(s.Tenant.Id, "google-key"), features = new[] { "drive" } },
        });
        Assert.Contains("validation.google.shared_drives", await drives.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A key that does not parse is reported by Test connection, and nothing is sent to Google.
        world.Factory.Secrets.Set(SecretReferences.For(s.Tenant.Id, "google-key"), "not a key");
        var before = Rest.SentTo("oauth2.googleapis.com").Count;
        var test = await (await admin.PostAsync($"{Packs}/{s.Connection}:test", null)).ReadAsync<JsonElement>();
        Assert.Contains("integrations.test.google_key_invalid", test.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, Rest.SentTo("oauth2.googleapis.com").Count);

        // Payload capture is not offered: directory and documents carry personal data.
        var capture = await admin.PutAsJsonAsync($"/api/v1/integrations/connections/{s.Connection}/capture", new { hours = 1, publicOrInternal = true });
        Assert.Equal(HttpStatusCode.BadRequest, capture.StatusCode);

        // Another tenant cannot read this connection's drives or use it for directory sync.
        var other = await world.CreateTenantAsync("gw-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Empty(await RepoAsync(other, r => r.ContainersAsync(s.Connection)));
        Assert.Null(await RepoAsync(other, r => r.OpenAsync(s.Connection, "fileA111", $"alice@{s.Domain}")));
        Assert.Equal(HttpStatusCode.Conflict, (await other["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { connectionId = s.Connection })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"{Packs}/{s.Connection}:test", null)).StatusCode);

        // Outside the package the pack is not offered.
        var workplace = await world.CreateTenantAsync("gw-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var refused = await workplace["admin"].Client.PostAsJsonAsync(Packs, new { kind = GooglePack.KindName, displayName = "Google", settings = new { } });
        Assert.NotEqual(HttpStatusCode.Created, refused.StatusCode);
    }
}
