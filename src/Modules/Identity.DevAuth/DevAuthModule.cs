using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Identity;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.DevAuth;

/// <summary>
/// Development-only sign-in (spec 003 US1, ADR-0004). This assembly is referenced only by <c>Api.DevHost</c>.
/// There is no password: a developer picks a seeded, fictional account. Never present in the production artifact.
/// </summary>
public static class DevAuthModule
{
    public const string Issuer = "urn:teambeats:dev";
    public const string EnabledKey = "Auth:DevSignIn:Enabled";

    public static bool IsAllowed(IHostEnvironment env) => env.IsDevelopment() || env.IsEnvironment("Test");

    public static IServiceCollection AddDevAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        if (!configuration.GetValue<bool>(EnabledKey))
        {
            return services;
        }
        if (!IsAllowed(env))
        {
            // Second guard, in case this assembly is ever composed into a non-development host.
            throw new InvalidOperationException("Development sign-in cannot run outside Development or Test.");
        }
        services.AddScoped<DevSeeder>();
        return services;
    }

    public static IEndpointRouteBuilder MapDevAuthEndpoints(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(EnabledKey))
        {
            return app;
        }

        var dev = app.MapGroup("/dev").WithTags("Development only").ExcludeFromDescription();

        dev.MapPost("/signin", async (DevSignInRequest request, HttpResponse response, ITenantContext context, SignInService signIn,
            SessionService sessions, IDbSession session, CancellationToken ct) =>
        {
            if (context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { } tenant)
            {
                throw AppException.NotFound();
            }
            var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
            var result = await signIn.SignInAsync(tenant, Issuer, email, email, request.DisplayName ?? email, "dev", "dev", ct);
            await session.CommitAsync(ct);
            return result.Outcome switch
            {
                SignInOutcome.Succeeded => SetCookie(response, sessions, result.SessionToken!, result.UserId!.Value),
                SignInOutcome.RefusedTenantInactive => throw new AppException(
                    tenant.Status == TenantLifecycleStatus.Suspended ? ProblemCodes.TenantSuspended : ProblemCodes.TenantClosed, 403),
                _ => throw new AppException(ProblemCodes.SignInRefused, 403),
            };
        });

        dev.MapPost("/platform/signin", async (DevSignInRequest request, HttpResponse response, ITenantContext context,
            SessionService sessions, IDbSession session, CancellationToken ct) =>
        {
            if (context.Host.Kind != HostScopeKind.Platform)
            {
                throw AppException.NotFound();
            }
            var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
            var operatorId = await session.ScalarAsync<Guid?>(
                "select id from identity.platform_operator where email = @e and status = 'active'", new { e = email }, ct)
                ?? throw new AppException(ProblemCodes.SignInRefused, 403);
            var token = await sessions.CreatePlatformSessionAsync(operatorId, ct);
            await session.CommitAsync(ct);
            sessions.SetCookie(response, token);
            return Results.Ok(new { operatorId });
        });

        return app;
    }

    private static IResult SetCookie(HttpResponse response, SessionService sessions, string token, Guid userId)
    {
        sessions.SetCookie(response, token);
        return Results.Ok(new { userId });
    }
}

public sealed record DevSignInRequest(string? Email, string? DisplayName);

/// <summary>Seed file format (deploy/compose/seed/dev-users.json). Fictional data only.</summary>
public sealed record DevSeed(IReadOnlyList<DevPlatformOperator> PlatformOperators);

public sealed record DevPlatformOperator(string Email, string DisplayName);

/// <summary>Seeds fictional platform operators for local development.</summary>
public sealed class DevSeeder(IDbSession session)
{
    public async Task SeedAsync(string seedFile, CancellationToken ct)
    {
        if (!File.Exists(seedFile))
        {
            return;
        }
        var seed = JsonSerializer.Deserialize<DevSeed>(await File.ReadAllTextAsync(seedFile, ct), JsonDefaults.Options)!;
        foreach (var op in seed.PlatformOperators)
        {
            await session.ExecAsync(
                "insert into identity.platform_operator (id, email, display_name, status) values (@id, @e, @n, 'active') on conflict (email) do nothing",
                new { id = Ids.New(), e = op.Email.ToLowerInvariant(), n = op.DisplayName }, ct);
        }
        await session.CommitAsync(ct);
    }
}
