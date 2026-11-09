using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Services;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Modules.Experience.Shell;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
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
    /// <param name="News">Fictional news published before the home, so the news feed and links have something to show.</param>
    /// <param name="Services">Fictional form services published before the home, so the catalogue and My services are not empty.</param>
    public sealed record HomeFile(string Template, string Slug, string? Variant, IReadOnlyDictionary<string, IReadOnlyList<HomePlacement>> Regions, int Revision = 1,
        IReadOnlyList<SeedNews>? News = null, IReadOnlyList<SeedService>? Services = null, IReadOnlyList<SeedLink>? Navigation = null)
    {
        [JsonPropertyName("$comment")]
        public string? Comment { get; init; }
    }

    /// <summary>A news item to publish once, found again by its English title. <c>Key</c> names it in placement links as <c>seed:news:key</c>.</summary>
    public sealed record SeedNews(string Key, IReadOnlyDictionary<string, VariantInput> Variants, bool Pinned = false, bool Featured = false);

    /// <summary>A form service to publish once, found again by its English name.</summary>
    public sealed record SeedService(IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string>? Descriptions,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Synonyms, FormDefinition Form);

    /// <summary>A global menu entry: an IA entry with this slug pointing at a product address. The menu is set only while the tenant has none.</summary>
    public sealed record SeedLink(string Slug, IReadOnlyDictionary<string, string> Labels, string Url);

    public const string NewsLinkPrefix = "seed:news:";

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

        var links = await SeedContentAsync(sp, file, ct);

        var shells = sp.GetRequiredService<ShellService>();
        await SeedNavigationAsync(shells, file, ct);
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
                ? items.Select(p => new Placement(p.Id, p.ComponentRef, p.Span, Link(p.Props, links))).ToList()
                : r.Placements))
            .ToList();
        page = await pages.SaveDraftAsync(page.Id, page.ETag, new SaveDraftRequest(page.Draft with { Regions = regions }), ct);
        await pages.PublishAsync(page.Id, new TeamBeats.Modules.Experience.Pages.PublishRequest(reason), ct);
        if (shell.Definition.HomePageId != page.Id)
        {
            await shells.PublishShellAsync(shell.Definition with { HomePageId = page.Id }, ct);
        }
        await sp.GetRequiredService<IDbSession>().CommitAsync(ct);
        logger.LogInformation("Home seed: page {Slug} published as the home of {Tenant}", file.Slug, key);
        return true;
    }

    /// <summary>
    /// Publishes the file's news and services that the tenant does not have yet, through the Content and Services use
    /// cases as the same administrator, and returns the read link of every seeded news item by its key.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> SeedContentAsync(IServiceProvider sp, HomeFile file, CancellationToken ct)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        if (file.News is { Count: > 0 } news)
        {
            var items = sp.GetRequiredService<Items>();
            var existing = await items.ListAsync(null, null, null, 500, ct);
            foreach (var seed in news)
            {
                var title = seed.Variants.TryGetValue("en", out var en) ? en.Title : seed.Variants.Values.First().Title;
                var found = existing.FirstOrDefault(i => i.Variants.Values.Any(v => v.Title == title));
                if (found is null)
                {
                    found = await items.CreateAsync(new ItemRequest(null, "news", null, null, null, null, "internal", seed.Pinned, seed.Featured,
                        seed.Variants, null, null), ct);
                    found = await items.PublishAsync(found.Id, new TeamBeats.Modules.Content.PublishRequest(null, null, true), ct);
                    logger.LogInformation("Home seed: news {Key} published", seed.Key);
                }
                links[seed.Key] = $"/content/read/{found.Id}";
            }
        }
        if (file.Services is { Count: > 0 } services)
        {
            var admin = sp.GetRequiredService<ServiceAdmin>();
            var existing = await admin.ListAsync(ct);
            foreach (var seed in services)
            {
                var name = seed.Names.GetValueOrDefault("en");
                if (existing.Any(s => s.Service.Names.GetValueOrDefault("en") == name))
                {
                    continue;
                }
                var created = await admin.CreateAsync(new ServiceSettingsRequest("form", seed.Names, seed.Descriptions, seed.Synonyms, null, null, null, "internal", null), ct);
                await admin.SaveDraftAsync(created.Service.Id, new ServiceDraftRequest(seed.Form, null, null), ct);
                await admin.PublishAsync(created.Service.Id, ct);
                logger.LogInformation("Home seed: service {Name} published", name);
            }
        }
        return links;
    }

    /// <summary>Gives the tenant a global menu from the file's links when it has none, so the side rail is not empty.</summary>
    private async Task SeedNavigationAsync(ShellService shells, HomeFile file, CancellationToken ct)
    {
        if (file.Navigation is not { Count: > 0 } navigation || (await shells.ListMenusAsync(ct)).Any(m => m.Kind == "global"))
        {
            return;
        }
        var nodes = await shells.ListNodesAsync(ct);
        var items = new List<MenuItemInput>();
        for (var i = 0; i < navigation.Count; i++)
        {
            var link = navigation[i];
            var id = nodes.FirstOrDefault(n => n.Slug == link.Slug)?.Id ?? Ids.New();
            await shells.SaveNodeAsync(id, new NodeRequest(null, link.Labels, link.Slug, null, link.Url, null, i), ct);
            items.Add(new MenuItemInput(id, null));
        }
        await shells.PublishMenuAsync("global", "tenant", new MenuRequest(items), ct);
        logger.LogInformation("Home seed: global menu with {Count} entries published", items.Count);
    }

    /// <summary>Replaces <c>seed:news:key</c> strings in placement properties with the seeded item's read link.</summary>
    private static JsonElement? Link(JsonElement? props, IReadOnlyDictionary<string, string> links)
    {
        if (props is not { ValueKind: JsonValueKind.Object } value || links.Count == 0)
        {
            return props;
        }
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        foreach (var (key, child) in node.ToList())
        {
            if (child is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith(NewsLinkPrefix, StringComparison.Ordinal)
                && links.TryGetValue(text[NewsLinkPrefix.Length..], out var link))
            {
                node[key] = link;
            }
        }
        return JsonSerializer.SerializeToElement(node);
    }
}
