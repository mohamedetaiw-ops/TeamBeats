using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Modules.Experience.Shell;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Api.DevHost;

/// <summary>
/// Gives the seeded test tenant an employee home (spec 008 US2) when <c>Dev:SeedHome:File</c> names a home file: a page
/// from the home template with the placements the file lists, published and set as the shell home. It works through the
/// Experience module's own use cases, as the first configured test administrator, so it is validated, authorized and
/// audited as if that administrator had built it. Nothing happens while that administrator has never signed in, or when
/// the tenant already has a home, so restarts are safe and a home an administrator changed is never replaced.
/// </summary>
public sealed class StagingHomeSeeder(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<StagingHomeSeeder> logger)
{
    public const string FileKey = "Dev:SeedHome:File";

    public sealed record HomePlacement(string Id, string ComponentRef, int? Span = null, JsonElement? Props = null);

    public sealed record HomeFile(string Template, string Slug, string? Variant, IReadOnlyDictionary<string, IReadOnlyList<HomePlacement>> Regions)
    {
        [JsonPropertyName("$comment")]
        public string? Comment { get; init; }
    }

    public async Task SeedAsync(string tenantKey, CancellationToken ct)
    {
        if (configuration[FileKey] is not { Length: > 0 } path)
        {
            return;
        }
        var file = JsonSerializer.Deserialize<HomeFile>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, path), ct), JsonDefaults.Options)
            ?? throw new InvalidOperationException($"{FileKey} names an empty home file.");
        var key = TenantRules.NormalizeKey(tenantKey);
        TenantInfo? tenant;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenant = (await list.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct)).FirstOrDefault(t => t.Key == key);
        }
        if (tenant is null)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var context = sp.GetRequiredService<TenantContextAccessor>();
        context.SetTenant(tenant.Id);
        context.SetActor(ActorType.System, null);
        var admin = await FirstAdminAsync(sp, ct);
        if (admin is null)
        {
            logger.LogInformation("Home seed: no test administrator of {Tenant} has signed in yet; the home is seeded on the next start", key);
            return;
        }
        context.SetActor(ActorType.User, admin.Id);

        var shells = sp.GetRequiredService<ShellService>();
        var shell = await shells.GetShellAdminAsync(ct);
        if (shell.Definition.HomePageId is not null)
        {
            return;
        }

        var pages = sp.GetRequiredService<PageService>();
        var page = (await pages.ListAsync(ct)).FirstOrDefault(p => p.Slug == file.Slug) is { } existing
            ? await pages.GetAsync(existing.Id, ct)
            : await pages.CreateAsync(new CreatePageRequest(file.Template, file.Slug, null, file.Variant), ct);
        var regions = page.Draft.Regions
            .Select(r => new PageRegion(r.Id, file.Regions.TryGetValue(r.Id, out var items)
                ? items.Select(p => new Placement(p.Id, p.ComponentRef, p.Span, p.Props)).ToList()
                : r.Placements))
            .ToList();
        page = await pages.SaveDraftAsync(page.Id, page.ETag, new SaveDraftRequest(page.Draft with { Regions = regions }), ct);
        await pages.PublishAsync(page.Id, new PublishRequest("Staging home seed"), ct);
        await shells.PublishShellAsync(shell.Definition with { HomePageId = page.Id }, ct);
        await sp.GetRequiredService<IDbSession>().CommitAsync(ct);
        logger.LogInformation("Home seed: page {Slug} published as the home of {Tenant}", file.Slug, key);
    }

    private async Task<UserInfo?> FirstAdminAsync(IServiceProvider sp, CancellationToken ct)
    {
        var users = sp.GetRequiredService<IUserDirectory>();
        var emails = configuration.GetSection(TestTenantSeeder.Section + ":Admins").Get<List<string>>() ?? [];
        foreach (var email in emails)
        {
            if (await users.FindByEmailAsync(email, ct) is { Status: "active" } user)
            {
                return user;
            }
        }
        return null;
    }
}
