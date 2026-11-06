using System.Security.Cryptography;
using System.Text;
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
/// <item><c>Password</c> (Staging, and allowed in Development and Test): only the configured test accounts can sign in,
/// with their username and password. The password comes from a deployment secret, never from source control.</item>
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
        string.Equals(configuration[ModeKey], nameof(DevSignInMode.Password), StringComparison.OrdinalIgnoreCase)
            ? DevSignInMode.Password
            : DevSignInMode.Open;

    public static IReadOnlyList<TestAccount> ReadAccounts(IConfiguration configuration) =>
        configuration.GetSection(AccountsKey).Get<List<TestAccount>>() ?? [];

    /// <summary>
    /// Fails startup on any unsafe combination. Staging must use password mode, and every test account in password mode
    /// needs a username, an email and a non-empty password.
    /// </summary>
    public static void Validate(IConfiguration configuration, IHostEnvironment env)
    {
        if (!IsAllowed(env))
        {
            // Second guard, in case this assembly is ever composed into a non-test host.
            throw new InvalidOperationException("Development sign-in cannot run outside Development, Test or Staging.");
        }
        var mode = ReadMode(configuration);
        if (env.IsEnvironment(StagingEnvironment) && mode != DevSignInMode.Password)
        {
            throw new InvalidOperationException(
                $"{ModeKey} must be Password in Staging. Open sign-in would let anyone choose any account.");
        }
        if (mode != DevSignInMode.Password)
        {
            return;
        }
        var accounts = ReadAccounts(configuration);
        if (accounts.Count == 0)
        {
            throw new InvalidOperationException($"{ModeKey} is Password but no test accounts are configured under {AccountsKey}.");
        }
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(account.Email))
            {
                throw new InvalidOperationException("Every test account needs a username and an email.");
            }
            if (string.IsNullOrEmpty(account.Password))
            {
                throw new InvalidOperationException(
                    $"Test account '{account.Username}' has no password. Set it from a deployment secret, never in source control.");
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

    /// <summary>Finds the test account for a username and password. Compares in constant time.</summary>
    public static TestAccount? MatchAccount(IReadOnlyList<TestAccount> accounts, string? username, string? password)
    {
        var name = (username ?? string.Empty).Trim();
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty));
        TestAccount? match = null;
        foreach (var account in accounts)
        {
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(account.Password ?? string.Empty));
            var passwordOk = CryptographicOperations.FixedTimeEquals(given, expected);
            if (passwordOk && string.Equals(account.Username.Trim(), name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(account.Password))
            {
                match = account;
            }
        }
        return match;
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
            if (mode == DevSignInMode.Password)
            {
                if (MatchAccount(accounts, request.Username, request.Password) is not { } account)
                {
                    accessor.SetTenant(tenant.Id);
                    accessor.SetActor(ActorType.Anonymous, null);
                    await audit.WriteAsync(new AuditEntry(AuditActions.SignInRefused, "user", null, AuditOutcome.Denied,
                        Changes: new { reason = "test_account_password", issuer = Issuer }), ct);
                    await session.CommitAsync(ct);
                    throw new AppException(ProblemCodes.SignInRefused, 403);
                }
                email = account.Email.Trim().ToLowerInvariant();
                displayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName;
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
                SignInOutcome.Succeeded => SetCookie(response, sessions, result.SessionToken!, result.UserId!.Value),
                SignInOutcome.RefusedTenantInactive => throw new AppException(
                    tenant.Status == TenantLifecycleStatus.Suspended ? ProblemCodes.TenantSuspended : ProblemCodes.TenantClosed, 403),
                _ => throw new AppException(ProblemCodes.SignInRefused, 403),
            };
        });

        dev.MapPost("/platform/signin", async (DevSignInRequest request, HttpResponse response, ITenantContext context,
            SessionService sessions, IDbSession session, CancellationToken ct) =>
        {
            // Passwordless platform sign-in exists only in open mode. Password mode (staging) has no platform console sign-in.
            if (mode == DevSignInMode.Password || context.Host.Kind != HostScopeKind.Platform)
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

public enum DevSignInMode
{
    Open,
    Password,
}

public sealed record DevSignInRequest(string? Email, string? DisplayName, string? Username = null, string? Password = null);

/// <summary>A configured test account for password mode. Fictional data only; the password comes from a secret.</summary>
public sealed class TestAccount
{
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
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
