using TeamBeats.Api;
using TeamBeats.Modules.Identity.DevAuth;
using TeamBeats.Persistence;

namespace TeamBeats.Api.DevHost;

/// <summary>Development and Test host. Refuses to run in any other environment.</summary>
public sealed class DevHostProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (!DevAuthModule.IsAllowed(builder.Environment))
        {
            throw new InvalidOperationException("The development host only runs in Development or Test.");
        }

        builder.AddTeamBeatsApi();
        builder.Services.AddDevAuth(builder.Configuration, builder.Environment);
        var app = builder.Build();
        app.UseTeamBeatsApi(endpoints => endpoints.MapDevAuthEndpoints(app.Configuration));

        if (app.Configuration.GetConnectionString("Owner") is { } owner && app.Configuration.GetValue<bool>("Dev:MigrateOnStart"))
        {
            await MigrationRunner.RunAsync(owner, TeamBeatsModules.Migrations);
        }

        if (app.Configuration.GetValue<bool>(DevAuthModule.EnabledKey) && app.Configuration["Dev:SeedFile"] is { } seed)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(seed, CancellationToken.None);
        }

        await app.RunAsync();
    }
}
