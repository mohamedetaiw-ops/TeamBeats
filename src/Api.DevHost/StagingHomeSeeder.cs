using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBeats.Authorization.Contracts;
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

    private const string SeedReason = "Staging home seed";

    public sealed record HomePlacement(string Id, string ComponentRef, int? Span = null, JsonElement? Props = null);

    /// <param name="Revision">Raised when the file changes. A home this seeder published from an older revision, and that nobody
    /// edited since, is brought up to date; a home an administrator changed or replaced is left alone.</param>
    public sealed record HomeFile(string Template, string Slug, string? Variant, IReadOnlyDictionary<string, IReadOnlyList<HomePlacement>> Regions, int Revision = 1)
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

        // A seed must never stop the host: anything unexpected is logged and the home is left for an administrator to set.
        try
        {
            foreach (var email in configuration.GetSection(TestTenantSeeder.Section + ":Admins").Get<List<string>>() ?? [])
            {
                if (await TrySeedAsAsync(tenant.Id, key, email, file, ct))
                {
                    return;
                }
            }
            logger.LogInformation("Home seed: no test administrator of {Tenant} can set the home yet; it is seeded on a later start", key);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Home seed: the home of {Tenant} was not seeded", key);
        }
    }

    /// <returns>True when the tenant has a home after this call, false when this administrator cannot set one.</returns>
    private async Task<bool> TrySeedAsAsync(Guid tenantId, string key, string email, HomeFile file, CancellationToken ct)
    {
        // A scope per administrator: the authorization service keeps the grants of the first actor it sees in a scope.
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var context = sp.GetRequiredService<TenantContextAccessor>();
        context.SetTenant(tenantId);
        context.SetActor(ActorType.System, null);
        if (await sp.GetRequiredService<IUserDirectory>().FindByEmailAsync(email, ct) is not { Status: "active" } admin)
        {
            logger.LogInformation("Home seed: test administrator {Email} of {Tenant} has not signed in yet", email, key);
            return false;
        }
        context.SetActor(ActorType.User, admin.Id);
        var authz = sp.GetRequiredService<IAuthorizationService>();
        foreach (var permission in new[] { Permissions.ShellEdit, Permissions.PageEdit, Permissions.PagePublish })
        {
            if (!(await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed)
            {
                logger.LogInformation("Home seed: {Email} of {Tenant} does not hold {Permission} for the whole tenant", email, key, permission);
                return false;
            }
        }

        var shells = sp.GetRequiredService<ShellService>();
        var shell = await shells.GetShellAdminAsync(ct);
        var pages = sp.GetRequiredService<PageService>();
        var reason = $"{SeedReason} r{file.Revision}";
        var list = await pages.ListAsync(ct);
        if (shell.Definition.HomePageId is { } homeId)
        {
            // Only a home this seeder published from an older revision, untouched since, is refreshed.
            var home = list.FirstOrDefault(p => p.Id == homeId);
            if (home is null || home.Slug != file.Slug || home.PublishedAt is not { } published || home.DraftUpdatedAt > published)
            {
                return true;
            }
            var last = (await pages.ReleasesAsync(homeId, ct)).OrderByDescending(r => r.Release).FirstOrDefault();
            if (last?.Reason is not { } lastReason || !lastReason.StartsWith(SeedReason, StringComparison.Ordinal) || lastReason == reason)
            {
                return true;
            }
        }

        var page = list.FirstOrDefault(p => p.Slug == file.Slug) is { } existing
            ? await pages.GetAsync(existing.Id, ct)
            : await pages.CreateAsync(new CreatePageRequest(file.Template, file.Slug, null, file.Variant), ct);
        var regions = page.Draft.Regions
            .Select(r => new PageRegion(r.Id, file.Regions.TryGetValue(r.Id, out var items)
                ? items.Select(p => new Placement(p.Id, p.ComponentRef, p.Span, p.Props)).ToList()
                : r.Placements))
            .ToList();
        page = await pages.SaveDraftAsync(page.Id, page.ETag, new SaveDraftRequest(page.Draft with { Regions = regions }), ct);
        await pages.PublishAsync(page.Id, new PublishRequest(reason), ct);
        if (shell.Definition.HomePageId != page.Id)
        {
            await shells.PublishShellAsync(shell.Definition with { HomePageId = page.Id }, ct);
        }
        await sp.GetRequiredService<IDbSession>().CommitAsync(ct);
        logger.LogInformation("Home seed: page {Slug} published as the home of {Tenant}", file.Slug, key);
        return true;
    }
}
