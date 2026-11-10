using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.SyncAgent;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 (R2) T015: the sync agent program itself against the real API. It enrols with a one-time code and keeps
/// its key in its state folder, reads a directory (played by the test instead of a domain controller), sends the
/// records signed, and the next read asks only for changes since the cursor and reports deleted entries. The service
/// account password stays in the agent's settings and never reaches TeamBeats.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SyncAgentProgramTests(TenantWorld world) : IDisposable
{
    private const string Packs = "/api/v1/integrations/pack-connections";
    private const string Password = "fictional-service-password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tb-agent-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private sealed class FakeDirectory : ILdapDirectory, ILdapDirectoryFactory
    {
        public long Usn { get; set; } = 1000;
        public List<LdapEntry> Users { get; } = [];
        public List<LdapEntry> Groups { get; } = [];
        public List<LdapEntry> Deleted { get; } = [];
        public Dictionary<string, List<string>> MembersOf { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Filters { get; } = [];
        public LdapTarget? Opened { get; private set; }

        public ILdapDirectory Open(LdapTarget target)
        {
            Opened = target;
            return this;
        }

        public (string NamingContext, long HighestUsn) RootInfo() => ("DC=corp,DC=example", Usn);

        public IEnumerable<LdapEntry> Search(string baseDn, string filter, string[] attributes, bool showDeleted = false)
        {
            Filters.Add(filter);
            return showDeleted ? Deleted : filter.Contains("(objectClass=group)", StringComparison.Ordinal) ? Groups : Users;
        }

        public LdapEntry? Find(string distinguishedName, string[] attributes) =>
            Users.Concat(Groups).FirstOrDefault(e => string.Equals(e.DistinguishedName, distinguishedName, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<string> Members(string groupDn) => MembersOf.TryGetValue(groupDn, out var m) ? m : [];

        public void Dispose()
        {
        }
    }

    private static LdapEntry Entry(string dn, Guid id, params (string Name, string Value)[] values)
    {
        var attributes = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase) { ["objectGUID"] = [id.ToByteArray()] };
        foreach (var (name, value) in values)
        {
            attributes[name] = [Encoding.UTF8.GetBytes(value)];
        }
        return new LdapEntry(dn, attributes);
    }

    [Fact]
    public async Task The_agent_enrols_reads_the_directory_and_sends_only_changes_on_the_next_read()
    {
        var t = await world.CreateTenantAsync("agp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync(Packs, new { kind = "onprem_ad", displayName = "Head office AD", settings = new { host = "dc01.corp.example", baseDn = "DC=corp,DC=example" } });
        var connection = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{connection}:enable", null)).StatusCode);
        var code = (await (await admin.PostAsync($"{Packs}/{connection}/agents/enrolment-code", null)).ReadAsync<JsonElement>()).GetProperty("code").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/tenant/directory", new { connectionId = connection, deletionThresholdPct = 100 })).StatusCode);

        // Enrol as the program does, and keep the key in the state folder.
        var http = world.Factory.TenantClient(t.Key);
        var client = new AgentClient(http, TimeProvider.System);
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var enrolled = await client.EnrolAsync(http.BaseAddress!, code, "AGENT-01", key, "1.0.0", CancellationToken.None);
            Assert.Equal(connection, enrolled.ConnectionId);
            new AgentState(_folder).Save(enrolled.AgentId, enrolled.ConnectionId, http.BaseAddress!, key);
        }
        var state = new AgentState(_folder);
        var agent = state.Load();
        await client.HeartbeatAsync(agent, "1.0.0", CancellationToken.None);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_folder, "agent.key")));
        }

        var (rana, sami, finance) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var ad = new FakeDirectory();
        ad.Users.Add(Entry("CN=Rana,OU=Staff,DC=corp,DC=example", rana, ("userAccountControl", "512"), ("displayName", "Rana Khalil"),
            ("mail", $"rana@{t.Key}.example"), ("department", "Finance"), ("manager", "CN=Sami,OU=Staff,DC=corp,DC=example")));
        ad.Users.Add(Entry("CN=Sami,OU=Staff,DC=corp,DC=example", sami, ("userAccountControl", "512"), ("displayName", "Sami Odeh"), ("mail", $"sami@{t.Key}.example")));
        ad.Groups.Add(Entry("CN=Finance,OU=Groups,DC=corp,DC=example", finance, ("cn", "Finance"), ("objectClass", "group")));
        ad.MembersOf["CN=Finance,OU=Groups,DC=corp,DC=example"] = ["CN=Rana,OU=Staff,DC=corp,DC=example"];
        var options = Options.Create(new AgentOptions { StateFolder = _folder, LdapUser = "svc-teambeats@corp.example", LdapPassword = Password });
        var runner = new JobRunner(client, ad, options, NullLogger<JobRunner>.Instance);

        async Task<SyncRun?> ReadAsync()
        {
            Assert.Equal("running", (await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                var run = await sp.GetRequiredService<DirectorySyncService>().RunAsync(connection, CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return run;
            }))!.Outcome);
            var job = await client.PullAsync(agent, CancellationToken.None);
            Assert.NotNull(job);
            await runner.RunAsync(agent, job, CancellationToken.None);
            return await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                var run = await sp.GetRequiredService<DirectorySyncService>().ApplyAgentReadAsync(
                    new AgentJobFinished(job.Id, connection, "directory_sync", true, null, 0, 0), CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return run;
            });
        }

        var first = await ReadAsync();
        Assert.Equal(("succeeded", 2), (first!.Outcome, first.Created));
        Assert.Equal("dc01.corp.example", ad.Opened!.Host);
        Assert.DoesNotContain(ad.Filters, f => f.Contains("uSNChanged", StringComparison.Ordinal));
        var manager = await world.Database.TenantScalarAsync<string>(t.Id,
            "select attributes ->> 'manager' from identity.directory_user where directory_id = @d", ("d", rana.ToString("D")));
        Assert.Equal(sami.ToString("D"), manager);
        Assert.Equal(1, await world.Database.TenantScalarAsync<int>(t.Id,
            "select cardinality(members) from identity.directory_group where directory_id = @d", ("d", finance.ToString("D"))));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from integrations.agent_directory_record where record::text like @p", ("p", "%" + Password + "%")));

        // Next read: Sami is disabled and the Finance group was deleted; only changes after the cursor are asked for.
        ad.Usn = 1500;
        ad.Users.Clear();
        ad.Users.Add(Entry("CN=Sami,OU=Staff,DC=corp,DC=example", sami, ("userAccountControl", "514"), ("displayName", "Sami Odeh"), ("mail", $"sami@{t.Key}.example")));
        ad.Groups.Clear();
        ad.Deleted.Add(Entry("CN=Finance\\0ADEL:x,CN=Deleted Objects,DC=corp,DC=example", finance, ("isDeleted", "TRUE"), ("objectClass", "group")));
        ad.Filters.Clear();
        await world.Database.TenantScalarAsync<long>(t.Id, "with u as (update identity.directory_connection set next_run_at = now() returning 1) select count(*) from u");
        var second = await ReadAsync();
        Assert.Equal(1, second!.Disabled);
        Assert.Contains(ad.Filters, f => f.Contains("(uSNChanged>=1001)", StringComparison.Ordinal));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from identity.directory_group where directory_id = @d", ("d", finance.ToString("D"))));
    }

    [Fact]
    public void Ldap_errors_become_plain_error_classes()
    {
        Assert.Equal("credential_refused", JobRunner.ErrorClass(new System.DirectoryServices.Protocols.LdapException(49)));
        Assert.Equal("not_responding", JobRunner.ErrorClass(new System.DirectoryServices.Protocols.LdapException(81)));
        Assert.Equal("server_error", JobRunner.ErrorClass(new System.DirectoryServices.Protocols.LdapException(1)));
        Assert.Equal("credential_missing", JobRunner.ErrorClass(new InvalidOperationException("credential_missing")));
    }
}
