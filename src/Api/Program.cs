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

    /// <summary>
    /// Spec 003 US1 scenario 2: the production artifact refuses to start when the dev sign-in setting is enabled,
    /// whatever the environment name says.
    /// </summary>
    public static void EnsureDevSignInDisabled(IConfiguration configuration)
    {
        if (configuration.GetValue<bool>(DevSignInKey))
        {
            throw new InvalidOperationException(
                $"{DevSignInKey} is enabled, but this is the production host, which does not contain development sign-in. Refusing to start.");
        }
    }
}
