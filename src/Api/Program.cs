using TeamBeats.Api;

namespace TeamBeats.Api;

/// <summary>Production entry point. Development sign-in is not part of this composition (ADR-0004).</summary>
public sealed class ApiProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ProductionGuards.EnsureDevSignInDisabled(builder.Configuration);
        builder.AddTeamBeatsApi();
        var app = builder.Build();
        app.UseTeamBeatsApi();
        app.Run();
    }
}

public static class ProductionGuards
{
    public const string DevSignInKey = "Auth:DevSignIn:Enabled";
    public const string TestAccountsKey = "Auth:DevSignIn:TestAccounts";
    public const string SeedTenantKey = "Dev:SeedTenant";
    public const string FakesKey = "Dev:Fakes";

    /// <summary>
    /// Spec 003 US1 scenario 2 and US7: the production artifact refuses to start when the dev sign-in setting is enabled,
    /// or when test accounts, a seeded test tenant or fake external systems are configured, whatever the environment
    /// name says. The production artifact does not contain the fakes (TeamBeats.DevFakes) either.
    /// </summary>
    public static void EnsureDevSignInDisabled(IConfiguration configuration)
    {
        if (configuration.GetValue<bool>(DevSignInKey))
        {
            throw new InvalidOperationException(
                $"{DevSignInKey} is enabled, but this is the production host, which does not contain development sign-in. Refusing to start.");
        }
        foreach (var key in new[] { TestAccountsKey, SeedTenantKey, FakesKey })
        {
            if (configuration.GetSection(key).GetChildren().Any())
            {
                throw new InvalidOperationException(
                    $"{key} is configured, but test accounts, test tenants and fake external systems never exist in production. Refusing to start.");
            }
        }
    }
}
