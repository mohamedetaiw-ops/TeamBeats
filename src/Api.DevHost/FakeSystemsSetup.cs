using Microsoft.Extensions.Options;
using TeamBeats.DevFakes;
using TeamBeats.Modules.Identity.DevAuth;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Modules.Identity.Sso;
using TeamBeats.Modules.Integrations.Microsoft365;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Api.DevHost;

/// <summary>
/// Puts fake external systems in place of the real ones (Microsoft Entra ID, Microsoft Graph, OpenRouter, Open-Meteo) when
/// <c>Dev:Fakes:Enabled</c> is true. The real adapters keep running; only their HTTP clients are pointed at in-process
/// fakes, and browser redirects to Microsoft are rewritten to the fake pages on the same host. Staging must run with
/// fakes on, so it can never reach a real directory, mailbox or weather service. Development host only (ADR-0004).
/// </summary>
public static class FakeSystemsSetup
{
    public const string Section = "Dev:Fakes";
    public const string EnabledKey = Section + ":Enabled";
    public const string ScenarioKey = Section + ":Scenario";
    public const string RunDirectorySyncKey = Section + ":RunDirectorySync";

    /// <summary>
    /// Direct test sign-in links (spec 003 US7): link name to the email of a person in the scenario, for example
    /// <c>employee</c>. <c>/dev/fake/signin/{name}</c> signs that person in through the fake Microsoft page.
    /// </summary>
    public const string SignInLinksKey = Section + ":SignInLinks";

    public static IReadOnlyDictionary<string, string> ReadSignInLinks(IConfiguration configuration) =>
        configuration.GetSection(SignInLinksKey).GetChildren()
            .ToDictionary(c => c.Key, c => (c.Value ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every direct sign-in link must name an enabled person of the scenario, so a link never fails silently.</summary>
    public static void ValidateSignInLinks(IReadOnlyDictionary<string, string> links, FakeScenario scenario)
    {
        foreach (var (name, email) in links)
        {
            var person = scenario.Directories.SelectMany(d => d.Users)
                .FirstOrDefault(u => string.Equals(u.Mail, email, StringComparison.OrdinalIgnoreCase));
            if (person is null || !person.Enabled)
            {
                throw new InvalidOperationException(
                    $"{SignInLinksKey}:{name} must be the email of an enabled person in the scenario, but '{email}' is not.");
            }
        }
    }

    /// <summary>Named HTTP client the AI gateway (spec 013) uses for OpenRouter; the fake is registered on it ahead of time.</summary>
    public const string OpenRouterClient = "openrouter";

    public static bool Enabled(IConfiguration configuration) => configuration.GetValue<bool>(EnabledKey);

    /// <summary>
    /// Staging must use fakes, and nothing may point the TeamBeats Microsoft 365 application at a real one while fakes
    /// are on (constitution V, spec 003 US7: staging holds fictional data only).
    /// </summary>
    public static void Validate(IConfiguration configuration, IHostEnvironment env)
    {
        if (!DevAuthModule.IsAllowed(env))
        {
            throw new InvalidOperationException("Fake external systems only run in Development, Test or Staging.");
        }
        if (env.IsEnvironment(DevAuthModule.StagingEnvironment) && !Enabled(configuration))
        {
            throw new InvalidOperationException($"{EnabledKey} must be true in Staging. Staging never connects to real Microsoft 365 or AI providers.");
        }
        if (!Enabled(configuration))
        {
            return;
        }
        var clientId = configuration["Integrations:Microsoft365:ClientId"];
        if (!string.IsNullOrEmpty(clientId) && clientId != FakeMicrosoftGraph.ClientId)
        {
            throw new InvalidOperationException("Fakes are on, but Integrations:Microsoft365:ClientId names a real application. Remove it.");
        }
        if (string.IsNullOrWhiteSpace(configuration[ScenarioKey]))
        {
            throw new InvalidOperationException($"{ScenarioKey} must name a scenario file when fakes are on.");
        }
    }

    public static WebApplicationBuilder AddFakeExternalSystems(this WebApplicationBuilder builder)
    {
        Validate(builder.Configuration, builder.Environment);
        if (!Enabled(builder.Configuration))
        {
            return builder;
        }
        var path = builder.Configuration[ScenarioKey]!;
        if (!Path.IsPathRooted(path))
        {
            path = File.Exists(Path.Combine(builder.Environment.ContentRootPath, path))
                ? Path.Combine(builder.Environment.ContentRootPath, path)
                : Path.Combine(AppContext.BaseDirectory, path);
        }
        var scenario = FakeScenario.Load(path);
        ValidateSignInLinks(ReadSignInLinks(builder.Configuration), scenario);
        var graph = new FakeMicrosoftGraph();
        scenario.ApplyTo(graph, DateTimeOffset.UtcNow);
        var systems = new FakeSystems(scenario, new FakeIdentityProvider(), graph, new FakeOpenRouter());

        // The fake Graph accepts only its own fictional application credentials.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:Microsoft365:ClientId"] = FakeMicrosoftGraph.ClientId,
            ["Integrations:Microsoft365:ClientSecret"] = FakeMicrosoftGraph.ClientSecret,
            ["Ai:OpenRouter:ApiKey"] = FakeOpenRouter.ApiKey,
        });
        // Debug windows (spec 013 US8) need a log key. Development and staging get a random one per process unless the
        // secret store has one, so entries written before a restart can no longer be read, which suits fictional data.
        if (string.IsNullOrEmpty(builder.Configuration["Ai:DebugLog:Key"]))
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DebugLog:Key"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            });
        }

        var services = builder.Services;
        services.AddSingleton(systems);
        services.AddHttpClient(HttpOidcProvider.ClientName).ConfigurePrimaryHttpMessageHandler(() => systems.Entra);
        services.AddHttpClient(HttpMicrosoftGraph.ClientName).ConfigurePrimaryHttpMessageHandler(() => systems.Graph);
        services.AddHttpClient(OpenRouterClient).ConfigurePrimaryHttpMessageHandler(() => systems.OpenRouter);
        services.AddHttpClient(TeamBeats.Modules.Integrations.Weather.OpenMeteoWeather.ClientName).ConfigurePrimaryHttpMessageHandler(() => systems.Weather);

        // Single sign-on client secrets named in the scenario resolve to their fictional values.
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(ISecretStore));
        if (inner is not null)
        {
            services.Remove(inner);
            services.Add(new ServiceDescriptor(typeof(ISecretStore), sp => new FakeSecretStore(
                (ISecretStore)(inner.ImplementationInstance ?? inner.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, inner.ImplementationType!)),
                scenario), inner.Lifetime));
        }

        services.AddSingleton<ScenarioSeeder>();
        if (builder.Configuration.GetValue<bool>(RunDirectorySyncKey))
        {
            services.AddHostedService<FakeDirectorySyncScheduler>();
        }
        return builder;
    }

    public static WebApplication UseFakeExternalSystems(this WebApplication app)
    {
        if (Enabled(app.Configuration))
        {
            app.UseFakeMicrosoftRedirects();
            app.MapFakeEndpoints(app.Services.GetRequiredService<IOptions<HostResolutionOptions>>().Value.BaseDomain,
                ReadSignInLinks(app.Configuration), SsoEndpoints.StartPath);
        }
        return app;
    }
}

/// <summary>Resolves the scenario's fictional single sign-on secrets; everything else goes to the configured store.</summary>
public sealed class FakeSecretStore(ISecretStore inner, FakeScenario scenario) : ISecretStore
{
    public async Task<string?> GetTenantSecretAsync(Guid tenantId, string reference, CancellationToken ct = default)
    {
        var name = SecretReferences.NameFor(tenantId, reference);
        var fake = scenario.Directories.Select(d => d.SsoApp).FirstOrDefault(a => a is not null && a.SecretName == name);
        return fake is not null ? fake.ClientSecret : await inner.GetTenantSecretAsync(tenantId, reference, ct);
    }
}

/// <summary>
/// Runs due directory syncs inside the development host, where the fake directory lives. Staging workers run the
/// production image, which has no fakes, so their scheduler is turned off (<c>Workers:DirectorySync:Enabled=false</c>).
/// </summary>
public sealed class FakeDirectorySyncScheduler(IServiceScopeFactory scopes, ILogger<FakeDirectorySyncScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<TenantInfo> tenants;
                await using (var list = scopes.CreateAsyncScope())
                {
                    tenants = await list.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(stoppingToken);
                }
                foreach (var tenant in tenants)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                    context.SetTenant(tenant.Id);
                    context.SetActor(ActorType.System, null);
                    await scope.ServiceProvider.GetRequiredService<DirectorySyncService>().RunIfDueAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Fake directory sync scheduler run failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
