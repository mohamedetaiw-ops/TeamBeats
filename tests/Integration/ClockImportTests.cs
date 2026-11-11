using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Integrations.ClockData;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.People;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 T016 (US7 scenario 1, FR-020): clock data read daily over SFTP or uploaded as CSV, mapped by column,
/// matched to people by employee number inside the tenant, stored once per punch ID, with the rows not imported listed.
/// SFTP servers are fakes of the test factory.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ClockImportTests(TenantWorld world)
{
    private const string Base = "/api/v1/integrations/pack-connections";
    private static readonly string HostKey = "FakeHostKeyForTests" + new string('0', 24);

    private static string Host() => $"sftp-{Guid.NewGuid():N}"[..20] + ".clock.test";

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ck-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "omar", MembershipTypes.Employee);
        return t;
    }

    /// <summary>Sets employee numbers as directory sync would.</summary>
    private async Task NumberAsync(TestTenant t, params (string User, string Number)[] numbers)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            foreach (var (user, _) in numbers)
            {
                await sp.GetRequiredService<ProfileStore>().RefreshAsync(t[user].Id, CancellationToken.None);
            }
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        foreach (var (user, number) in numbers)
        {
            var updated = await world.Database.TenantScalarAsync<long>(t.Id,
                "with u as (update people.profile set directory_values = jsonb_build_object('employeeNumber', @n::text) where user_id = @u returning 1) select count(*) from u",
                ("n", number), ("u", t[user].Id));
            Assert.Equal(1, updated);
        }
    }

    private async Task<(Guid Id, FakeSftp.Server Server, string Host)> SftpConnectionAsync(TestTenant t)
    {
        var host = Host();
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "clock-sftp"), "fictional-sftp-password");
        var server = world.Factory.Sftp.On(host, new FakeSftp.Server { HostKeySha256 = HostKey, Username = "teambeats", Secret = "fictional-sftp-password" });
        server.Folders["/exports"] = new();
        var created = await admin.PostAsJsonAsync(Base, new
        {
            kind = ClockImportPack.KindName,
            displayName = "Gate readers",
            settings = new
            {
                source = "sftp",
                sftp = new { host = host.ToUpperInvariant(), username = "teambeats", secretRef = SecretReferences.For(t.Id, "clock-sftp"), hostKeySha256 = "SHA256:" + HostKey + "=", directory = "/exports" },
                delimiter = ",",
                hasHeader = true,
                columns = new { employeeNumber = "Badge", punchId = "Punch", timestamp = "When", direction = "Way", device = "Reader" },
                timeZone = "Asia/Riyadh",
                inValues = new[] { "IN" },
                outValues = new[] { "OUT" },
                importAt = "02:00",
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.ReadAsync<JsonElement>();
        var settings = view.GetProperty("settings");
        Assert.Equal(host, settings.GetProperty("sftp").GetProperty("host").GetString());
        Assert.Equal(HostKey, settings.GetProperty("sftp").GetProperty("hostKeySha256").GetString());
        Assert.Equal("*.csv", settings.GetProperty("sftp").GetProperty("filePattern").GetString());
        var id = view.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        return (id, server, host);
    }

    private Task<int> ScheduleAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var queued = await sp.GetRequiredService<ClockImports>().ScheduleDueAsync(now, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return queued;
        });

    private Task<int> RunJobsAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var ran = await sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 50);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return ran;
        });

    private Task<IReadOnlyList<ClockPunch>> PunchesAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IClockData>().ReadAsync(0, 5000));

    private static async Task<JsonElement[]> FilesAsync(TestTenant t, Guid id) =>
        (await (await t["admin"].Client.GetAsync($"{Base}/{id}/clock-files")).ReadAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToArray();

    [Fact]
    public async Task The_daily_SFTP_import_maps_rows_by_employee_number_and_lists_the_rows_not_imported()
    {
        var t = await TenantAsync();
        var other = await TenantAsync();
        await NumberAsync(t, ("sara", "E100"), ("omar", "E200"));
        await NumberAsync(other, ("sara", "E300"));
        var (id, server, host) = await SftpConnectionAsync(t);

        var test = await (await t["admin"].Client.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());
        Assert.Contains(test.GetProperty("checks").EnumerateArray(), c => c.GetProperty("resultKey").GetString() == "integrations.test.clock_no_files");

        server.Put("/exports", "punches_2026-11-09.csv",
            "Badge,Punch,When,Way,Reader\n" +
            "E100,P-1,2026-11-09 07:58:00,IN,Gate A\n" +
            "E200,P-2,2026-11-09 08:03:00,IN,Gate B\n" +
            "E100,P-3,2026-11-09 17:01:00,OUT,Gate A\n" +
            "E300,P-4,2026-11-09 08:00:00,IN,Gate A\n" +
            "E999,P-5,2026-11-09 08:00:00,IN,Gate A\n" +
            "E200,P-6,yesterday,OUT,Gate B\n", DateTimeOffset.UtcNow.AddHours(-1));
        server.Put("/exports", "readme.txt", "not clock data", DateTimeOffset.UtcNow.AddHours(-1));

        // Due once a day, after the import time in the source's time zone (02:00 in Riyadh is 23:00 UTC).
        Assert.Equal(0, await ScheduleAsync(t, new DateTimeOffset(2026, 11, 9, 22, 30, 0, TimeSpan.Zero)));
        Assert.Equal(1, await ScheduleAsync(t, new DateTimeOffset(2026, 11, 9, 23, 30, 0, TimeSpan.Zero)));
        Assert.Equal(0, await ScheduleAsync(t, new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero)));
        Assert.True(await RunJobsAsync(t) >= 1);

        var file = Assert.Single(await FilesAsync(t, id));
        Assert.Equal(("punches_2026-11-09.csv", "sftp", "imported"), (file.GetProperty("name").GetString(), file.GetProperty("source").GetString(), file.GetProperty("status").GetString()));
        Assert.Equal((6, 3, 0, 3), (file.GetProperty("rowCount").GetInt32(), file.GetProperty("imported").GetInt32(), file.GetProperty("duplicates").GetInt32(), file.GetProperty("unmatched").GetInt32()));

        var unmatched = await (await t["admin"].Client.GetAsync($"{Base}/{id}/clock-files/{file.GetProperty("id").GetGuid()}/unmatched")).ReadAsync<JsonElement>();
        Assert.Equal(
            ["5:E300:unknown_employee", "6:E999:unknown_employee", "7:E200:bad_time"],
            unmatched.EnumerateArray().Select(u => $"{u.GetProperty("line").GetInt32()}:{u.GetProperty("employeeNumber").GetString()}:{u.GetProperty("reason").GetString()}").ToArray());

        var punches = await PunchesAsync(t);
        Assert.Equal(3, punches.Count);
        var first = punches.Single(p => p.PunchId == "P-1");
        Assert.Equal((t["sara"].Id, ClockDirections.In, "Gate A"), (first.UserId, first.Direction, first.Device));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 4, 58, 0, TimeSpan.Zero), first.At);
        Assert.Equal(t["omar"].Id, punches.Single(p => p.PunchId == "P-2").UserId);
        Assert.Empty(await PunchesAsync(other));
        Assert.Equal(1, await world.Database.OutboxCountAsync(nameof(ClockDataImported), t.Id));
        Assert.Contains(world.Factory.Sftp.Sessions, s => s.Host == host && s.Secret == "fictional-sftp-password");

        // The run is in run history, with the count and no row content.
        var runs = await (await t["admin"].Client.GetAsync($"/api/v1/integrations/connections/{id}/runs")).ReadAsync<JsonElement>();
        var run = runs.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("action").GetString() == ClockImportPack.Import);
        Assert.Equal("succeeded", run.GetProperty("status").GetString());
        Assert.DoesNotContain("Gate A", runs.GetRawText(), StringComparison.Ordinal);

        // An import now reads only the new file; a punch repeated there is stored once.
        server.Put("/exports", "punches_2026-11-10.csv", "Badge,Punch,When,Way,Reader\nE100,P-3,2026-11-09 17:01:00,OUT,Gate A\nE200,P-7,2026-11-10 08:00:00,IN,Gate B\n",
            DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Accepted, (await t["admin"].Client.PostAsync($"{Base}/{id}/clock-files:import", null)).StatusCode);
        await RunJobsAsync(t);
        var files = await FilesAsync(t, id);
        Assert.Equal(2, files.Length);
        var second = files.Single(f => f.GetProperty("name").GetString() == "punches_2026-11-10.csv");
        Assert.Equal((1, 1), (second.GetProperty("imported").GetInt32(), second.GetProperty("duplicates").GetInt32()));
        Assert.Equal(4, (await PunchesAsync(t)).Count);
    }

    [Fact]
    public async Task An_uploaded_CSV_is_imported_once_and_only_administrators_of_the_tenant_see_the_files()
    {
        var t = await TenantAsync();
        await NumberAsync(t, ("sara", "E100"));
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync(Base, new
        {
            kind = ClockImportPack.KindName,
            displayName = "Attendance exports",
            settings = new
            {
                source = "upload",
                delimiter = ";",
                hasHeader = false,
                columns = new { employeeNumber = "1", date = "2", time = "3" },
                dateFormat = "dd/MM/yyyy",
                timeFormat = "HH:mm",
                timeZone = "UTC",
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        var test = await (await admin.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());

        static ByteArrayContent Body(string text)
        {
            var body = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            body.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            return body;
        }
        const string Content = "E100;09/11/2026;08:00\nE100;09/11/2026;08:00\nE100;09/11/2026;16:30\n";
        var uploaded = await admin.PostAsync($"{Base}/{id}/clock-files?name=november.csv", Body(Content));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        Assert.Equal("pending", (await uploaded.ReadAsync<JsonElement>()).GetProperty("status").GetString());
        await RunJobsAsync(t);
        var file = Assert.Single(await FilesAsync(t, id));
        Assert.Equal(("imported", 2, 1), (file.GetProperty("status").GetString(), file.GetProperty("imported").GetInt32(), file.GetProperty("duplicates").GetInt32()));
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from integrations.clock_file where content is not null"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = 'integration.clock_data_uploaded'"));

        var again = await admin.PostAsync($"{Base}/{id}/clock-files?name=again.csv", Body(Content));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Base}/{id}/clock-files?name=..%2Fx.csv", Body("E1;1/1/2026;08:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{Base}/{id}/clock-files?name=empty.csv", Body(""))).StatusCode);

        // A mapped column the file does not have refuses the whole file.
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"{Base}/{id}/clock-files?name=short.csv", Body("E100;09/11/2026\n"))).StatusCode);
        await RunJobsAsync(t);
        var refused = (await FilesAsync(t, id)).Single(f => f.GetProperty("name").GetString() == "short.csv");
        Assert.Equal(("imported", 1), (refused.GetProperty("status").GetString(), refused.GetProperty("unmatched").GetInt32()));

        // Employees and other tenants never reach the files.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Base}/{id}/clock-files")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PostAsync($"{Base}/{id}/clock-files?name=x.csv", Body("E100;09/11/2026;09:00"))).StatusCode);
        var other = await TenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Base}/{id}/clock-files")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"{Base}/{id}/clock-files?name=x.csv", Body("E100;09/11/2026;09:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other["admin"].Client.GetAsync($"{Base}/{id}/clock-files/{file.GetProperty("id").GetGuid()}/unmatched")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_host_key_or_credential_stops_the_import_and_a_server_that_is_down_is_retried()
    {
        var t = await TenantAsync();
        var (id, server, _) = await SftpConnectionAsync(t);
        var admin = t["admin"].Client;

        server.HostKeySha256 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var test = await (await admin.PostAsync($"{Base}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("failed", test.GetProperty("status").GetString());
        var check = Assert.Single(test.GetProperty("checks").EnumerateArray());
        Assert.Equal(("integrations.test.clock_host_key", ConnectorErrorClasses.HostKeyMismatch), (check.GetProperty("resultKey").GetString(), check.GetProperty("errorClass").GetString()));
        var sessions = world.Factory.Sftp.Sessions.Count;
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"{Base}/{id}/clock-files:import", null)).StatusCode);
        await RunJobsAsync(t);
        Assert.Equal(sessions, world.Factory.Sftp.Sessions.Count);
        Assert.Equal("failed", await RunStatusAsync(t, id, ConnectorErrorClasses.HostKeyMismatch));

        server.HostKeySha256 = HostKey;
        server.Secret = "changed";
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"{Base}/{id}/clock-files:import", null)).StatusCode);
        await RunJobsAsync(t);
        Assert.Equal("failed", await RunStatusAsync(t, id, ConnectorErrorClasses.CredentialRefused));

        server.Secret = "fictional-sftp-password";
        server.Down = true;
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"{Base}/{id}/clock-files:import", null)).StatusCode);
        await RunJobsAsync(t);
        Assert.Equal("retrying", await RunStatusAsync(t, id, ConnectorErrorClasses.NotResponding));
    }

    /// <summary>Spec 022 section 13: 50,000 punches import well within 10 minutes; here the budget is one minute.</summary>
    [Fact]
    public async Task Fifty_thousand_punches_import_within_the_budget()
    {
        var t = await TenantAsync();
        await NumberAsync(t, ("sara", "E100"), ("omar", "E200"));
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync(Base, new
        {
            kind = ClockImportPack.KindName,
            displayName = "Bulk",
            settings = new { source = "upload", columns = new { employeeNumber = "Badge", punchId = "Punch", timestamp = "When" }, timeZone = "UTC" },
        });
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        var csv = new StringBuilder("Badge,Punch,When\n");
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 50_000; i++)
        {
            csv.Append(i % 2 == 0 ? "E100" : "E200").Append(",P").Append(i).Append(',').Append(start.AddMinutes(i).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(csv.ToString()));
        body.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"{Base}/{id}/clock-files?name=year.csv", body)).StatusCode);
        await RunJobsAsync(t);
        watch.Stop();
        var file = Assert.Single(await FilesAsync(t, id));
        Assert.Equal(50_000, file.GetProperty("imported").GetInt32());
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(1), $"Import took {watch.Elapsed}.");
    }

    private async Task<string?> RunStatusAsync(TestTenant t, Guid id, string errorClass) =>
        await world.Database.TenantScalarAsync<string>(t.Id,
            "select status from integrations.run where connection_id = @c and error_class = @e order by created_at desc limit 1", ("c", id), ("e", errorClass));

    [Fact]
    public async Task Clock_settings_are_checked_and_the_package_holds()
    {
        var t = await TenantAsync();
        var admin = t["admin"].Client;
        var host = Host();
        var bad = await admin.PostAsJsonAsync(Base, new
        {
            kind = ClockImportPack.KindName,
            displayName = "Readers",
            settings = new
            {
                source = "sftp",
                sftp = new { host, username = "u", secretRef = SecretReferences.For(Guid.NewGuid(), "x"), hostKeySha256 = "not-a-key", directory = "/a/../etc" },
                hasHeader = false,
                columns = new { employeeNumber = "1", timestamp = "1", direction = "0" },
                timeZone = "Mars/Olympus",
                timestampFormat = "%%%",
                delimiter = ":",
                inValues = new[] { "IN" },
                outValues = new[] { "in" },
                importAt = "25:00",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        var keys = errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToHashSet();
        foreach (var key in new[]
        {
            "validation.custom_rest.host_not_allowed", "validation.custom_rest.secret_ref", "validation.clock.host_key", "validation.clock.directory",
            "validation.clock.column_number", "validation.clock.column_twice", "validation.clock.time_zone", "validation.clock.delimiter",
            "validation.clock.direction_values", "validation.clock.import_at",
        })
        {
            Assert.Contains(key, keys);
        }

        var noSource = await admin.PostAsJsonAsync(Base, new { kind = ClockImportPack.KindName, displayName = "Readers", settings = new { columns = new { } } });
        Assert.Equal(HttpStatusCode.BadRequest, noSource.StatusCode);
        var body = await noSource.Content.ReadAsStringAsync();
        Assert.Contains("validation.clock.source", body, StringComparison.Ordinal);
        Assert.Contains("validation.clock.column_required", body, StringComparison.Ordinal);

        var workplace = await TenantAsync("workplace");
        var refused = await workplace["admin"].Client.PostAsJsonAsync(Base, new
        {
            kind = ClockImportPack.KindName,
            displayName = "Readers",
            settings = new { source = "upload", columns = new { employeeNumber = "Badge", timestamp = "When" } },
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
