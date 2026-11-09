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
/// Development-only sign-in (spec 003 US1 and US7, ADR-0004). This assembly is referenced only by <c>Api.DevHost</c>
/// and is never present in the production artifact. Two modes:
/// <list type="bullet">
/// <item><c>Open</c> (Development, Test): a developer picks a seeded, fictional account by email. No password.</item>
/// <item><c>TestAccount</c> (Staging, and allowed in Development and Test): only the configured test accounts can sign in,
/// by username with one click and no password. Nothing else, including any other email, can sign in.</item>
/// </list>
/// </summary>
public static class DevAuthModule
{
    public const string Issuer = "urn:teambeats:dev";
    public const string EnabledKey = "Auth:DevSignIn:Enabled";
    public const string ModeKey = "Auth:DevSignIn:Mode";
    public const string AccountsKey = "Auth:DevSignIn:TestAccounts";

    /// <summary>The shared staging deployment is a Test environment (constitution V, spec 003 US7).</summary>
    public const string StagingEnvironment = "Staging";

    public static bool IsAllowed(IHostEnvironment env) =>
        env.IsDevelopment() || env.IsEnvironment("Test") || env.IsEnvironment(StagingEnvironment);

    public static DevSignInMode ReadMode(IConfiguration configuration) =>
        string.Equals(configuration[ModeKey], nameof(DevSignInMode.TestAccount), StringComparison.OrdinalIgnoreCase)
            ? DevSignInMode.TestAccount
            : DevSignInMode.Open;

    public static IReadOnlyList<TestAccount> ReadAccounts(IConfiguration configuration) =>
        configuration.GetSection(AccountsKey).Get<List<TestAccount>>() ?? [];

    /// <summary>
    /// Fails startup on any unsafe combination. Staging must use test account mode, and every test account needs a
    /// username and an email.
    /// </summary>
    public static void Validate(IConfiguration configuration, IHostEnvironment env)
    {
        if (!IsAllowed(env))
        {
            // Second guard, in case this assembly is ever composed into a non-test host.
            throw new InvalidOperationException("Development sign-in cannot run outside Development, Test or Staging.");
        }
        var mode = ReadMode(configuration);
        if (env.IsEnvironment(StagingEnvironment) && mode != DevSignInMode.TestAccount)
        {
            throw new InvalidOperationException(
                $"{ModeKey} must be TestAccount in Staging. Open sign-in would let anyone choose any account.");
        }
        if (mode != DevSignInMode.TestAccount)
        {
            return;
        }
        var accounts = ReadAccounts(configuration);
        if (accounts.Count == 0)
        {
            throw new InvalidOperationException($"{ModeKey} is TestAccount but no test accounts are configured under {AccountsKey}.");
        }
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(account.Email))
            {
                throw new InvalidOperationException("Every test account needs a username and an email.");
            }
        }
        if (accounts.Select(a => a.Username.Trim().ToLowerInvariant()).Distinct().Count() != accounts.Count)
        {
            throw new InvalidOperationException("Test account usernames must be unique.");
        }
    }

    public static IServiceCollection AddDevAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        if (!configuration.GetValue<bool>(EnabledKey))
        {
            return services;
        }
        Validate(configuration, env);
        services.AddScoped<DevSeeder>();
        return services;
    }

    /// <summary>Finds the configured test account for a username (not case sensitive).</summary>
    public static TestAccount? MatchAccount(IReadOnlyList<TestAccount> accounts, string? username)
    {
        var name = (username ?? string.Empty).Trim();
        return accounts.FirstOrDefault(a => string.Equals(a.Username.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    public static IEndpointRouteBuilder MapDevAuthEndpoints(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(EnabledKey))
        {
            return app;
        }

        var mode = ReadMode(configuration);
        var accounts = ReadAccounts(configuration);
        var dev = app.MapGroup("/dev").WithTags("Development only").ExcludeFromDescription();

        dev.MapPost("/signin", async (DevSignInRequest request, HttpResponse response, ITenantContext context, SignInService signIn,
            SessionService sessions, IDbSession session, TenantContextAccessor accessor, IAuditWriter audit, CancellationToken ct) =>
        {
            if (context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { } tenant)
            {
                throw AppException.NotFound();
            }

            string email;
            string displayName;
            string? next = null;
            if (mode == DevSignInMode.TestAccount)
            {
                if (MatchAccount(accounts, request.Username) is not { } account)
                {
                    accessor.SetTenant(tenant.Id);
                    accessor.SetActor(ActorType.Anonymous, null);
                    await audit.WriteAsync(new AuditEntry(AuditActions.SignInRefused, "user", null, AuditOutcome.Denied,
                        Changes: new { reason = "unknown_test_account", issuer = Issuer }), ct);
                    await session.CommitAsync(ct);
                    throw new AppException(ProblemCodes.SignInRefused, 403);
                }
                email = account.Email.Trim().ToLowerInvariant();
                displayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName;
                next = LocalPath(account.Next);
            }
            else
            {
                email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
                displayName = request.DisplayName ?? email;
            }

            var result = await signIn.SignInAsync(tenant, Issuer, email, email, displayName, "dev", "dev", ct);
            await session.CommitAsync(ct);
            return result.Outcome switch
            {
                SignInOutcome.Succeeded => SetCookie(response, sessions, result.SessionToken!, result.UserId!.Value, next),
                SignInOutcome.RefusedTenantInactive => throw new AppException(
                    tenant.Status == TenantLifecycleStatus.Suspended ? ProblemCodes.TenantSuspended : ProblemCodes.TenantClosed, 403),
                _ => throw new AppException(ProblemCodes.SignInRefused, 403),
            };
        });

        dev.MapPost("/platform/signin", async (DevSignInRequest request, HttpResponse response, ITenantContext context,
            SessionService sessions, IDbSession session, CancellationToken ct) =>
        {
            // Platform dev sign-in exists only in open mode. Test account mode (staging) has no platform console sign-in.
            if (mode == DevSignInMode.TestAccount || context.Host.Kind != HostScopeKind.Platform)
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

    private static IResult SetCookie(HttpResponse response, SessionService sessions, string token, Guid userId, string? next = null)
    {
        sessions.SetCookie(response, token);
        return Results.Ok(new { userId, next });
    }

    /// <summary>Only a path on this site: no scheme, host or protocol-relative address.</summary>
    public static string? LocalPath(string? path) =>
        path is { Length: > 0 } && path[0] == '/' && (path.Length == 1 || path[1] is not ('/' or '\\')) && !path.Contains("://", StringComparison.Ordinal) ? path : null;
}

public enum DevSignInMode
{
    Open,
    TestAccount,
}

public sealed record DevSignInRequest(string? Email, string? DisplayName, string? Username = null);

/// <summary>A configured test account for test account mode. Fictional data only, no password.</summary>
public sealed class TestAccount
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional local path the browser opens after sign-in. Staging uses it to connect the test admin's fake Outlook,
    /// so a demo starts with mail and calendar filled after every sign-in and redeploy.
    /// </summary>
    public string? Next { get; set; }
}

/// <summary>Seed file format (deploy/compose/seed/dev-users.json). Fictional data only.</summary>
public sealed record DevSeed(IReadOnlyList<DevPlatformOperator> PlatformOperators);

public sealed record DevPlatformOperator(string Email, string DisplayName);

/// <summary>Seeds fictional platform operators for local development and staging.</summary>
public sealed class DevSeeder(IDbSession session)
{
    /// <summary>Creates the platform operator if missing and returns its id. Does not commit.</summary>
    public async Task<Guid> EnsurePlatformOperatorAsync(string email, string displayName, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        await session.ExecAsync(
            "insert into identity.platform_operator (id, email, display_name, status) values (@id, @e, @n, 'active') on conflict (email) do nothing",
            new { id = Ids.New(), e = normalized, n = displayName }, ct);
        return await session.ScalarAsync<Guid>("select id from identity.platform_operator where email = @e", new { e = normalized }, ct);
    }

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
