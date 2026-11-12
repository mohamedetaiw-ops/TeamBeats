using System.Text.Json;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Api.DevHost.Demo;

/// <summary>
/// Fills the seeded test tenant with the demo data package (docs/testing/demo-data.md) when <c>Dev:SeedDemo:Folder</c>
/// names a folder of pack files: organization units, names and time zones, news and knowledge with reactions and
/// comments, a service catalogue with requests at every stage, document libraries, published policies with an
/// attestation campaign, holiday calendars, project and team spaces, search tuning and assistant conversations.
/// <para>
/// Everything goes through the modules' own use cases, acting as the person the file names, so the data is validated,
/// authorized, audited, indexed and notified exactly as if those people had done it. Each step runs in its own scope and
/// transaction in the tenant's context, so forced row-level security sees the tenant. Each item is found again by a
/// natural key (English title or name, policy code, request key) and only added when missing, so restarts add nothing
/// and nothing an administrator changed is replaced. A person who does not exist yet is skipped; a step that fails is
/// logged and the rest goes on, so a seed never stops the host.
/// </para>
/// </summary>
public sealed partial class DemoDataSeeder(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<DemoDataSeeder> logger)
{
    public const string FolderKey = "Dev:SeedDemo:Folder";
    public const string PeopleKey = "Dev:SeedDemo:People";
    public const string AdminPersona = "admin";

    /// <summary>What one run did, by area, for the log and the tests.</summary>
    public sealed class Report
    {
        public Dictionary<string, int> Added { get; } = new(StringComparer.Ordinal);

        public List<string> Skipped { get; } = [];

        public void Add(string area, int count = 1) => Added[area] = Added.GetValueOrDefault(area) + count;
    }

    private sealed record Run(Guid TenantId, string TenantKey, DemoPack Pack, Dictionary<string, Guid> People, Report Report)
    {
        /// <summary>Seeded items by reference (<c>news:key</c>, <c>service:key</c> and so on), for links between areas.</summary>
        public Dictionary<string, (string Source, Guid Id)> Refs { get; } = new(StringComparer.Ordinal);

        public Guid? Person(string key) => People.TryGetValue(key, out var id) ? id : null;
    }

    public static DemoPack Load(string folder)
    {
        var path = Path.IsPathRooted(folder) ? folder : Path.Combine(AppContext.BaseDirectory, folder);
        var pack = new DemoPack();
        foreach (var file in Directory.GetFiles(path, "*.json").Order(StringComparer.Ordinal))
        {
            var part = JsonSerializer.Deserialize<DemoPack>(File.ReadAllText(file), JsonDefaults.Options)
                ?? throw new InvalidOperationException($"Demo pack file {file} is empty.");
            pack = pack.Merge(part);
        }
        return pack;
    }

    public async Task<Report?> SeedAsync(string tenantKey, CancellationToken ct)
    {
        if (configuration[FolderKey] is not { Length: > 0 } folder)
        {
            return null;
        }
        var pack = Load(folder);
        var key = TenantRules.NormalizeKey(tenantKey);
        TenantInfo? tenant;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenant = (await list.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct)).FirstOrDefault(t => t.Key == key);
        }
        if (tenant is null)
        {
            return null;
        }

        var people = await ResolvePeopleAsync(tenant.Id, pack, ct);
        if (!people.ContainsKey(AdminPersona))
        {
            logger.LogInformation("Demo data: no test administrator of {Tenant} has signed in yet; the demo data is seeded on a later start", key);
            return null;
        }
        var run = new Run(tenant.Id, key, pack, people, new Report());
        logger.LogInformation("Demo data: seeding {Tenant} with {People} of {Personas} people found", key, people.Count, pack.People.Count + 1);

        // Order matters: units before content audiences, services before requests, content before search tuning.
        await AreaAsync(run, "org units", () => SeedOrgUnitsAsync(run, ct));
        await AreaAsync(run, "profiles", () => SeedProfilesAsync(run, ct));
        await AreaAsync(run, "roles", () => SeedRolesAsync(run, ct));
        await AreaAsync(run, "news", () => SeedArticlesAsync(run, run.Pack.News, "news", ct));
        await AreaAsync(run, "knowledge", () => SeedArticlesAsync(run, run.Pack.Knowledge, "kb", ct));
        await AreaAsync(run, "services", () => SeedServicesAsync(run, ct));
        await AreaAsync(run, "requests", () => SeedRequestsAsync(run, ct));
        await AreaAsync(run, "documents", () => SeedDocumentsAsync(run, ct));
        await AreaAsync(run, "policies", () => SeedPoliciesAsync(run, ct));
        await AreaAsync(run, "calendars", () => SeedCalendarsAsync(run, ct));
        await AreaAsync(run, "calendar events", () => SeedEventsAsync(run, ct));
        await AreaAsync(run, "spaces", () => SeedSpacesAsync(run, ct));
        await DispatchAsync(ct);
        await AreaAsync(run, "search", () => SeedSearchAsync(run, ct));
        await AreaAsync(run, "assistant", () => SeedAssistantAsync(run, ct));
        await DispatchAsync(ct);

        logger.LogInformation("Demo data: {Tenant} done, added {Added}; skipped {Skipped}", key,
            string.Join(", ", run.Report.Added.Select(a => $"{a.Value} {a.Key}")), run.Report.Skipped.Count == 0 ? "nothing" : string.Join("; ", run.Report.Skipped));
        return run.Report;
    }

    /// <summary>One area at a time; a failure is logged and the other areas still run.</summary>
    private async Task AreaAsync(Run run, string area, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            run.Report.Skipped.Add($"{area} failed: {e.Message}");
            logger.LogWarning(e, "Demo data: {Area} of {Tenant} was not seeded completely", area, run.TenantKey);
        }
    }

    /// <summary>Personas found as active users of the tenant; configuration overrides the pack's addresses.</summary>
    private async Task<Dictionary<string, Guid>> ResolvePeopleAsync(Guid tenantId, DemoPack pack, CancellationToken ct)
    {
        var emails = new Dictionary<string, string>(pack.People, StringComparer.Ordinal);
        foreach (var child in configuration.GetSection(PeopleKey).GetChildren())
        {
            if (child.Value is { Length: > 0 } value)
            {
                emails[child.Key] = value;
            }
        }
        var admins = configuration.GetSection(TestTenantSeeder.Section + ":Admins").Get<List<string>>() ?? [];
        var found = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        context.SetTenant(tenantId);
        context.SetActor(ActorType.System, null);
        var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
        foreach (var admin in admins)
        {
            if (await directory.FindByEmailAsync(admin, ct) is { Status: "active" } user)
            {
                found[AdminPersona] = user.Id;
                break;
            }
        }
        foreach (var (persona, email) in emails)
        {
            if (persona != AdminPersona && await directory.FindByEmailAsync(email, ct) is { Status: "active" } user)
            {
                found[persona] = user.Id;
            }
        }
        return found;
    }

    /// <summary>
    /// Runs one step as a person of the tenant, in its own scope and transaction, with the clock set back by the given
    /// days. A scope per step: the authorization service keeps the grants of the first actor it sees in a scope.
    /// Returns default when the persona does not exist here.
    /// </summary>
    private async Task<T?> AsAsync<T>(Run run, string persona, double daysAgo, Func<IServiceProvider, Task<T>> work, CancellationToken ct)
    {
        if (run.Person(persona) is not { } userId)
        {
            run.Report.Skipped.Add($"{persona} not found");
            return default;
        }
        return await DemoClock.Back(daysAgo, async () =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var context = sp.GetRequiredService<TenantContextAccessor>();
            context.SetTenant(run.TenantId);
            context.SetActor(ActorType.User, userId);
            var result = await work(sp);
            await sp.GetRequiredService<IDbSession>().CommitAsync(ct);
            return result;
        });
    }

    private Task AsAsync(Run run, string persona, double daysAgo, Func<IServiceProvider, Task> work, CancellationToken ct) =>
        AsAsync<bool>(run, persona, daysAgo, async sp =>
        {
            await work(sp);
            return true;
        }, ct);

    /// <summary>Runs one step as the system actor, for reads no person should need.</summary>
    private async Task<T> AsSystemAsync<T>(Run run, Func<IServiceProvider, Task<T>> work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var context = sp.GetRequiredService<TenantContextAccessor>();
        context.SetTenant(run.TenantId);
        context.SetActor(ActorType.System, null);
        var result = await work(sp);
        await sp.GetRequiredService<IDbSession>().CommitAsync(ct);
        return result;
    }

    /// <summary>Delivers queued events now (indexing, notifications, campaign members) so later steps and the first page view see them.</summary>
    private async Task DispatchAsync(CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            await using var scope = scopes.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, ct) == 0)
            {
                return;
            }
        }
    }

    /// <summary>Runs a step that may fail on its own (one item), logging it and going on with the next.</summary>
    private async Task ItemAsync(Run run, string what, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var detail = e is AppException app
                ? $"{app.Code} {app.DetailKey} {string.Join(",", app.Errors?.Select(x => $"{x.Key}={string.Join("|", x.Value)}") ?? [])}".Trim()
                : e.Message;
            run.Report.Skipped.Add($"{what}: {detail}");
            logger.LogWarning("Demo data: {What} skipped: {Detail}", what, detail);
        }
    }
}
