using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Modules.Experience.Personalization;
using TeamBeats.Modules.Experience.Shell;
using TeamBeats.Modules.Experience.Themes;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience;

/// <summary>Pages, templates, layouts and the component registry (spec 032; module map: Experience).</summary>
public static class ExperienceModule
{
    public static readonly MigrationSource Migrations = new("experience", typeof(ExperienceModule).Assembly);

    public static IServiceCollection AddExperienceModule(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<PlatformCatalogue>();
        services.AddSingleton<IComponentRegistration, PlatformComponents>();
        services.AddSingleton<ComponentRegistry>();
        services.AddScoped<ComponentEnablement>();
        services.AddScoped<ComponentAvailability>();
        services.AddScoped<AudienceEvaluator>();
        services.AddScoped<LayoutEngine>();
        services.AddScoped<HomeDefaults>();
        services.AddScoped<PersonalizationService>();
        services.AddScoped<PageService>();
        services.AddScoped<ThemeService>();
        services.AddScoped<ShellService>();
        return services;
    }

    public static IEndpointRouteBuilder MapExperienceEndpoints(this IEndpointRouteBuilder app)
    {
        var x = app.MapGroup("/api/v1/experience").RequireTenantUser().WithTags("Experience: pages");

        x.MapGet("/catalogue", async (IEntitlementService entitlements, PlatformCatalogue catalogue, ILocalizer localizer, RequestLocale locale,
            CancellationToken ct) =>
        {
            await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
            // Names and the text description of each structure in the caller's language, for the template chooser (FR-017).
            return Results.Ok(new
            {
                layouts = catalogue.Layouts.Values.OrderBy(l => l.Id).Select(l => new
                {
                    l.Id, l.Version, l.NameKey, l.Regions, l.Fallback, name = localizer.Get(l.NameKey, locale.Locale),
                }),
                templates = catalogue.Templates.Values.OrderBy(t => t.Id).Select(t => new
                {
                    t.Id, t.Version, t.State, t.NameKey, t.DescriptionKey, t.Layout, t.Variants, t.Regions,
                    name = localizer.Get(t.NameKey, locale.Locale), description = localizer.Get(t.DescriptionKey, locale.Locale),
                }),
            });
        });

        x.MapGet("/pages", async (PageService pages, CancellationToken ct) => Results.Ok(await pages.ListAsync(ct)));

        x.MapPost("/pages", async (CreatePageRequest request, PageService pages, IDbSession session, CancellationToken ct) =>
        {
            var page = await pages.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/experience/pages/{page.Id}", page);
        });

        x.MapGet("/pages/{id:guid}", async (Guid id, HttpResponse response, PageService pages, CancellationToken ct) =>
        {
            var page = await pages.GetAsync(id, ct);
            response.Headers.ETag = page.ETag;
            return Results.Ok(page);
        });

        x.MapPut("/pages/{id:guid}/draft", async (Guid id, SaveDraftRequest request, HttpRequest http, PageService pages, IDbSession session,
            CancellationToken ct) =>
        {
            var page = await pages.SaveDraftAsync(id, http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null, request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = page.ETag;
            return Results.Ok(page);
        });

        x.MapPost("/pages/{id:guid}/validate", async (Guid id, PageService pages, CancellationToken ct) =>
            Results.Ok(new { findings = await pages.ValidateDraftAsync(id, ct) }));

        x.MapPost("/pages/{id:guid}/publish", async (Guid id, PublishRequest? request, PageService pages, IDbSession session, CancellationToken ct) =>
        {
            var release = await pages.PublishAsync(id, request ?? new PublishRequest(null), ct);
            await session.CommitAsync(ct);
            return Results.Ok(release);
        });

        x.MapPost("/pages/{id:guid}/rollback", async (Guid id, RollbackRequest request, PageService pages, IDbSession session, CancellationToken ct) =>
        {
            var release = await pages.RollbackAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(release);
        });

        x.MapGet("/pages/{id:guid}/releases", async (Guid id, PageService pages, CancellationToken ct) => Results.Ok(await pages.ReleasesAsync(id, ct)));

        x.MapGet("/pages/{id:guid}/preview", async (Guid id, string? locale, PageService pages, CancellationToken ct) =>
            Results.Ok(await pages.PreviewAsync(id, locale, ct)));

        x.MapGet("/pages/{id:guid}/components", async (Guid id, string? region, PageService pages, CancellationToken ct) =>
            Results.Ok(await pages.PickerAsync(id, region, ct)));

        x.MapGet("/render", async (string? slug, PageService pages, RequestLocale locale, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(slug))
            {
                throw Primitives.AppException.NotFound();
            }
            return Results.Ok(await pages.RenderAsync(slug, locale.Locale, locale.Direction, ct));
        });

        x.MapGet("/components", async (ComponentEnablement enablement, CancellationToken ct) => Results.Ok(await enablement.ListAsync(ct)));

        x.MapPut("/components/{componentId}/enablement", async (string componentId, EnablementRequest request, ComponentEnablement enablement,
            IDbSession session, CancellationToken ct) =>
        {
            var view = await enablement.SetAsync(componentId, request.Enabled, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        MapThemeEndpoints(app);
        MapShellEndpoints(app);
        MapPersonalizationEndpoints(app);
        return app;
    }

    /// <summary>Brands, assignments, locks and the effective theme (spec 033 R1). Every call commits, so first-use seeding is kept.</summary>
    private static void MapThemeEndpoints(IEndpointRouteBuilder app)
    {
        var x = app.MapGroup("/api/v1/experience").RequireTenantUser().WithTags("Experience: themes");

        x.MapGet("/brands", async (ThemeService themes, IDbSession session, CancellationToken ct) =>
        {
            var brands = await themes.ListBrandsAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(brands);
        });

        x.MapPost("/brands", async (BrandRequest request, ThemeService themes, IDbSession session, CancellationToken ct) =>
        {
            var brand = await themes.CreateBrandAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/experience/brands/{brand.Id}", brand);
        });

        x.MapGet("/brands/{id:guid}", async (Guid id, ThemeService themes, IDbSession session, CancellationToken ct) =>
        {
            var brand = await themes.GetBrandAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(brand);
        });

        x.MapPut("/brands/{id:guid}", async (Guid id, BrandRequest request, ThemeService themes, IDbSession session, CancellationToken ct) =>
        {
            var brand = await themes.UpdateBrandAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(brand);
        });

        x.MapPost("/brands/{id:guid}/check", async (Guid id, BrandRequest request, ThemeService themes, CancellationToken ct) =>
            Results.Ok(await themes.CheckBrandAsync(id, request, ct)));

        // The token schema with platform defaults and allowed modes, so editors never hard-code token rules (spec 033 T011).
        x.MapGet("/tokens", () => Results.Ok(new
        {
            tokens = TokenSchema.All.Select(t => new TokenSchemaEntry(t.Name, t.Name.Split('.')[0], t.Type, t.CssName, t.Light, t.Dark, t.Compact, t.Script,
                [.. (t.AllowsDark ? new[] { ThemeModes.Dark } : []), .. (t.AllowsCompact ? new[] { ThemeModes.Compact } : [])])),
            assetSlots = ThemeService.Slots,
            fonts = TokenSchema.ApprovedFonts,
            easings = TokenSchema.Easings.Keys,
        }));

        x.MapPost("/assets", async (HttpRequest http, ThemeService themes, IDbSession session, CancellationToken ct) =>
        {
            var buffer = new byte[ImageValidator.MaxBytes + 1];
            int read, total = 0;
            while ((read = await http.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total > ImageValidator.MaxBytes)
                {
                    throw AppException.Validation("asset", "validation.asset.size");
                }
            }
            var asset = await themes.UploadAssetAsync(http.ContentType, buffer[..total], ct);
            await session.CommitAsync(ct);
            return Results.Created(asset.Url, asset);
        }).DisableAntiforgery();

        x.MapGet("/assets/{id:guid}", async (Guid id, ThemeService themes, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type) = await themes.OpenAssetAsync(id, ct);
            // Assets never change once written, so the browser may keep them; the URL is checked against the session.
            response.Headers.CacheControl = "private, max-age=31536000, immutable";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            if (type == "image/svg+xml")
            {
                response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            }
            return Results.File(content, type);
        });

        x.MapGet("/brand-assignments/{scope}", async (string scope, ThemeService themes, IDbSession session, CancellationToken ct) =>
        {
            var view = await themes.GetAssignmentAsync(scope, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        x.MapPut("/brand-assignments/{scope}", async (string scope, AssignmentRequest request, ThemeService themes, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await themes.SetAssignmentAsync(scope, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        x.MapGet("/locks/{scope}", async (string scope, ThemeService themes, IDbSession session, CancellationToken ct) =>
        {
            var locks = await themes.GetLocksAsync(scope, ct);
            await session.CommitAsync(ct);
            return Results.Ok(locks);
        });

        x.MapPut("/locks/{scope}", async (string scope, LockRequest request, ThemeService themes, IDbSession session, CancellationToken ct) =>
        {
            var locks = await themes.SetLocksAsync(scope, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(locks);
        });

        x.MapGet("/theme", async (string? scope, ThemeService themes, IDbSession session, CancellationToken ct) =>
        {
            var theme = await themes.ThemeAsync(scope, ct);
            await session.CommitAsync(ct);
            return Results.Ok(theme);
        });

        // The tenant comes from the session, never from the URL; v is the content hash, so a matching URL can be cached for a year.
        x.MapGet("/theme.css", async (string? scope, string? mode, string? density, string? v, HttpResponse response, ThemeService themes,
            IDbSession session, CancellationToken ct) =>
        {
            var (css, hash) = await themes.CssAsync(scope, mode, density, ct);
            await session.CommitAsync(ct);
            response.Headers.CacheControl = v == hash ? "private, max-age=31536000, immutable" : "private, max-age=60";
            response.Headers.ETag = $"\"{hash}\"";
            return Results.Text(css, "text/css; charset=utf-8");
        });
    }

    /// <summary>Shell, menus, information architecture, resolve and quick navigation (spec 008 T004 to T006). Every call commits.</summary>
    private static void MapShellEndpoints(IEndpointRouteBuilder app)
    {
        var x = app.MapGroup("/api/v1/experience").RequireTenantUser().WithTags("Experience: shell and navigation");

        x.MapGet("/shell", async (ShellService shell, RequestLocale locale, IDbSession session, CancellationToken ct) =>
        {
            var view = await shell.GetShellAsync(locale.Locale, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        x.MapGet("/shell/definition", async (ShellService shell, IDbSession session, CancellationToken ct) =>
        {
            var view = await shell.GetShellAdminAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        x.MapPut("/shell/definition", async (ShellDefinition request, ShellService shell, IDbSession session, CancellationToken ct) =>
        {
            var view = await shell.PublishShellAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        x.MapGet("/menus", async (ShellService shell, IDbSession session, CancellationToken ct) =>
        {
            var menus = await shell.ListMenusAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(menus);
        });

        x.MapPut("/menus/{kind}/{scope}", async (string kind, string scope, MenuRequest request, ShellService shell, IDbSession session,
            CancellationToken ct) =>
        {
            var menu = await shell.PublishMenuAsync(kind, scope, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(menu);
        });

        x.MapGet("/ia", async (ShellService shell, IDbSession session, CancellationToken ct) =>
        {
            var nodes = await shell.ListNodesAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(nodes);
        });

        x.MapPut("/ia/{id:guid}", async (Guid id, NodeRequest request, ShellService shell, IDbSession session, CancellationToken ct) =>
        {
            var node = await shell.SaveNodeAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(node);
        });

        x.MapGet("/resolve", async (string? path, ShellService shell, RequestLocale locale, IDbSession session, CancellationToken ct) =>
        {
            var result = await shell.ResolveAsync(path, locale.Locale, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        x.MapGet("/quick-nav", async (string? q, ShellService shell, RequestLocale locale, IDbSession session, CancellationToken ct) =>
        {
            var items = await shell.QuickNavAsync(q, locale.Locale, ct);
            await session.CommitAsync(ct);
            return Results.Ok(items);
        });
    }

    /// <summary>Preference profile, personal layouts, add panel, personalization policy, audience rules and targeting preview (spec 008 T010).</summary>
    private static void MapPersonalizationEndpoints(IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").RequireTenantUser().WithTags("Me: personalization");

        // Language stays on PUT /api/v1/me/preferences of the user account; this profile holds appearance and accessibility.
        me.MapGet("/preference-profile", async (PersonalizationService p, CancellationToken ct) => Results.Ok(await p.GetProfileAsync(ct)));

        me.MapPut("/preference-profile", async (PreferenceProfileRequest request, PersonalizationService p, IDbSession session, CancellationToken ct) =>
        {
            var view = await p.SaveProfileAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        me.MapGet("/pages/{pageId:guid}/layout", async (Guid pageId, HttpResponse response, PersonalizationService p, CancellationToken ct) =>
        {
            var view = await p.GetLayoutAsync(pageId, ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        me.MapPut("/pages/{pageId:guid}/layout", async (Guid pageId, LayoutRequest request, HttpRequest http, PersonalizationService p, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await p.SaveLayoutAsync(pageId, http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null, request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        me.MapDelete("/pages/{pageId:guid}/layout", async (Guid pageId, PersonalizationService p, IDbSession session, CancellationToken ct) =>
        {
            await p.ResetLayoutAsync(pageId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        me.MapDelete("/layouts", async (PersonalizationService p, IDbSession session, CancellationToken ct) =>
        {
            await p.ResetAllLayoutsAsync(ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        me.MapGet("/pages/{pageId:guid}/add-panel", async (Guid pageId, string? region, PersonalizationService p, CancellationToken ct) =>
            Results.Ok(await p.AddPanelAsync(pageId, region, ct)));

        var x = app.MapGroup("/api/v1/experience").RequireTenantUser().WithTags("Experience: personalization");

        x.MapGet("/pages/{pageId:guid}/personalization-policy", async (Guid pageId, PersonalizationService p, CancellationToken ct) =>
            Results.Ok(await p.GetPolicyAsync(pageId, ct)));

        x.MapPut("/pages/{pageId:guid}/personalization-policy", async (Guid pageId, PolicyRequest request, PersonalizationService p, IDbSession session,
            CancellationToken ct) =>
        {
            var policy = await p.SetPolicyAsync(pageId, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(policy);
        });

        x.MapGet("/audience-rules", async (PersonalizationService p, CancellationToken ct) => Results.Ok(await p.ListRulesAsync(ct)));

        x.MapPut("/audience-rules/{id:guid}", async (Guid id, AudienceRuleRequest request, PersonalizationService p, IDbSession session, CancellationToken ct) =>
        {
            var rule = await p.SaveRuleAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(rule);
        });

        x.MapPost("/audience-rules/preview", async (PreviewRequest request, PersonalizationService p, CancellationToken ct) =>
            Results.Ok(await p.PreviewAsync(request, ct)));
    }
}
