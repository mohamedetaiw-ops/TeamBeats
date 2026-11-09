using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Identity.Sso;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity;

public static class IdentityModule
{
    public static readonly MigrationSource Migrations = new("identity", typeof(IdentityModule).Assembly);

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SessionOptions>(configuration.GetSection(SessionOptions.Section));
        services.AddScoped<SessionService>();
        services.AddScoped<ISessionRevocation>(sp => sp.GetRequiredService<SessionService>());
        services.AddScoped<ISignInHistory, SignInHistory>();
        services.AddScoped<ISignInMethods, SignInMethodsReader>();
        services.AddScoped<SignInService>();
        services.AddHttpClient(Sso.HttpOidcProvider.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<Sso.IOidcProvider, Sso.HttpOidcProvider>();
        services.AddScoped<Sso.IdentityProviderStore>();
        services.AddScoped<Sso.GroupRoleSync>();
        services.AddScoped<Sso.SsoService>();
        services.AddScoped<UserDirectory>();
        services.AddScoped<IUserDirectory>(sp => sp.GetRequiredService<UserDirectory>());
        services.AddScoped<RequestLocale>();
        services.AddMessageHandler<TenantSuspended, RevokeSessionsOnSuspension>("identity.revoke_on_suspend");
        services.AddScoped<Directory.DirectorySyncService>();
        services.AddScoped<IDirectoryReader, Directory.DirectoryReader>();
        services.AddScoped<TeamBeats.Authorization.Contracts.IUserAttributeProvider, Directory.DirectoryUserAttributes>();
        services.AddScoped<Integrations.Contracts.IConnectionDependents, Directory.DirectoryConnectionDependents>();
        services.AddMessageHandler<Directory.DirectorySyncRequested, Directory.RunRequestedDirectorySync>("identity.directory_sync_now");
        services.AddMessageHandler<Integrations.Contracts.ConnectionDeleted, Directory.RemoveDirectoryOnConnectionDeleted>("identity.directory_connection_deleted");
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/signout", async (HttpResponse response, SessionService sessions, IDbSession session, CancellationToken ct) =>
        {
            await sessions.SignOutAsync(ct);
            await session.CommitAsync(ct);
            sessions.ClearCookie(response);
            return Results.NoContent();
        }).WithTags("Auth");

        app.MapSsoEndpoints();
        Directory.DirectoryEndpoints.MapDirectoryEndpoints(app);

        var me = app.MapGroup("/api/v1/me").RequireTenantUser().WithTags("Me");

        me.MapGet("/", async (ITenantContext context, UserDirectory users, RequestLocale locale, ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var user = await users.GetAsync(context.RequireUser(), ct) ?? throw AppException.NotFound();
            var tenant = await settings.GetLocaleSettingsAsync(ct);
            var branding = await settings.GetBrandingAsync(ct);
            return Results.Ok(new
            {
                user.Id,
                user.DisplayName,
                user.Email,
                user.PreferredLocale,
                locale = locale.Locale,
                dir = locale.Direction,
                tenant = new { id = context.TenantId, key = context.Host.Tenant?.Key, branding.DisplayName, tenant.EnabledLocales, tenant.DefaultLocale, tenant.TimeZone, tenant.NumberingSystem },
            });
        });

        // Sign out everywhere (spec 026 FR-003): every session of the caller ends, this one included.
        me.MapPost("/sessions/revoke-all", async (ITenantContext context, SessionService sessions, IDbSession session, HttpResponse response,
            CancellationToken ct) =>
        {
            await sessions.RevokeUserSessionsAsync(context.RequireUser(), SessionRules.SignOutEverywhere, ct);
            await session.CommitAsync(ct);
            sessions.ClearCookie(response);
            return Results.NoContent();
        });

        var users = app.MapGroup("/api/v1/identity/users").RequireTenantUser().InAdminArea().WithTags("Tenant: identity");
        users.MapPost("/{id:guid}/sessions/revoke-all", async (Guid id, IAuthorizationService authz, SessionService sessions, IDbSession session,
            CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.UsersManage, AuthzTarget.Tenant, ct);
            if (!await session.ScalarAsync<bool>("select exists (select 1 from identity.user_account where id = @id)", new { id }, ct))
            {
                throw AppException.NotFound();
            }
            var count = await sessions.RevokeUserSessionsAsync(id, SessionRules.AdminRevoked, ct);
            await session.CommitAsync(ct);
            return Results.Ok(new { revoked = count });
        });

        me.MapPut("/preferences", async (PreferencesRequest request, ITenantContext context, IDbSession session, CancellationToken ct) =>
        {
            if (request.Locale is not null && !Locales.IsSupported(request.Locale))
            {
                throw AppException.Validation("locale", "validation.locale.unsupported");
            }
            await session.ExecAsync("update identity.user_account set preferred_locale = @l where id = @u",
                new { l = request.Locale, u = context.RequireUser() }, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        return app;
    }
}

public sealed record PreferencesRequest(string? Locale);

public sealed class UserDirectory(IDbSession session) : IUserDirectory
{
    private const string Select =
        """
        select u.id, u.display_name, u.email, u.status, u.preferred_locale,
               (select max(created_at) from identity.user_session s where s.user_id = u.id) as last_sign_in
        from identity.user_account u
        """;

    private static UserInfo Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Get<string>("preferred_locale"), r.Get<DateTimeOffset?>("last_sign_in"));

    public Task<UserInfo?> GetAsync(Guid userId, CancellationToken ct = default) =>
        session.QuerySingleOrDefaultAsync(Select + " where u.id = @id", new { id = userId }, Map, ct);

    public Task<UserInfo?> FindByEmailAsync(string email, CancellationToken ct = default) =>
        session.QuerySingleOrDefaultAsync(Select + " where lower(u.email) = lower(@e)", new { e = email.Trim() }, Map, ct);

    public Task<bool> IsDisabledByDirectoryAsync(Guid userId, CancellationToken ct = default) =>
        session.ScalarAsync<bool>("select exists (select 1 from identity.directory_user_link where user_id = @u and disabled_by_directory)", new { u = userId }, ct);

    public async Task<IReadOnlyList<UserInfo>> ListAsync(string? search, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            Select + " where (@q::text is null or u.display_name ilike '%' || @q || '%' or u.email ilike '%' || @q || '%') order by u.display_name limit @n",
            new { q = search?.Replace("%", string.Empty, StringComparison.Ordinal), n = Math.Clamp(limit, 1, 500) }, Map, ct);

    public async Task<IReadOnlyList<UserInfo>> ListAllAsync(CancellationToken ct = default) =>
        await session.QueryAsync("select id, display_name, email, status, preferred_locale from identity.user_account", null,
            r => new UserInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Get<string>("preferred_locale"), null), ct);
}

/// <summary>Revokes all sessions of a suspended tenant (spec 001 plan "Key behaviors"). Runs even though the tenant is inactive.</summary>
public sealed class RevokeSessionsOnSuspension(SessionService sessions) : IMessageHandler<TenantSuspended>
{
    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Process;

    public async Task HandleAsync(TenantSuspended message, CancellationToken ct) =>
        await sessions.RevokeTenantSessionsAsync("tenant_suspended", ct);
}
