using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Api;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Identity;
using TeamBeats.Modules.Identity.Sso;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Testing;

/// <summary>
/// The production API composition (ADR-0004: tests use the real session pipeline with sessions created by the
/// harness, not development sign-in) on a fresh database.
/// </summary>
public sealed class ApiFactory(TestDatabase database) : WebApplicationFactory<ApiProgram>
{
    /// <summary>A fresh delegated token key per test run; never a fixed value in code.</summary>
    public static readonly string TokenKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>A fresh AI debug log key per test run.</summary>
    public static readonly string AiLogKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public const string BaseDomain = "teambeats.test";

    public TestDatabase Database { get; } = database;

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "tb-storage-" + Guid.NewGuid().ToString("N")[..8]);

    public TestDocuments Documents { get; } = new();

    public FakeIdentityProvider Idp { get; } = new();

    public TestSecrets Secrets { get; } = new();

    public FakeMicrosoftGraph Graph { get; } = new();

    public FakeOpenMeteo Weather { get; } = new();

    public FakeOpenRouter OpenRouter { get; } = new();

    public FakeGotenberg Gotenberg { get; } = new();

    public TestUserAttributes UserAttributes { get; } = new();

    public TestEmailSender Email { get; } = new();

    public TestAgentToolLog AgentTools { get; } = new();

    public FakeTicketing Ticketing { get; } = new();

    public FakeRestApi Rest { get; } = new();

    public FakeSftp Sftp { get; } = new();

    public FakeSyslog Syslog { get; } = new();

    public FakePushService Push { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("ConnectionStrings:App", Database.AppConnectionString);
        builder.UseSetting("Tenancy:Hosts:BaseDomain", BaseDomain);
        builder.UseSetting("Storage:Root", StorageRoot);
        builder.UseSetting("Messaging:Transport", "InMemory");
        // Spec 026 FR-008: the test installation hosts two regions, so region choice and refusal can be tested.
        builder.UseSetting("Platform:Regions:Offered:0", "uae");
        builder.UseSetting("Platform:Regions:Offered:1", "ksa");
        builder.UseSetting("Platform:Regions:Default", "uae");
        builder.UseSetting("Http:TrustForwardedHeaders", "true");
        builder.UseSetting("Integrations:Microsoft365:ClientId", FakeMicrosoftGraph.ClientId);
        builder.UseSetting("Integrations:Microsoft365:ClientSecret", FakeMicrosoftGraph.ClientSecret);
        builder.UseSetting(TeamBeats.Modules.Integrations.Microsoft365.TokenProtector.KeySetting, TokenKey);
        builder.UseSetting(TeamBeats.Persistence.GotenbergConverter.UrlKey, FakeGotenberg.BaseUrl);
        builder.UseSetting(TeamBeats.Modules.AiGateway.OpenRouter.OpenRouterProvider.ApiKeySetting, FakeOpenRouter.ApiKey);
        builder.UseSetting(TeamBeats.Modules.AiGateway.OpenRouter.OpenRouterProvider.TimeoutSetting, "2");
        builder.UseSetting(TeamBeats.Modules.AiGateway.DebugLogProtector.KeySetting, AiLogKey);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Documents);
            services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IComponentRegistration, TeamBeats.Api.DevHost.ReferencePacks>();
            services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IComponentRegistration, UpgradePack>();
            services.AddScoped<IResourceDescriptorProvider, TestDocumentProvider>();
            services.AddSingleton(UserAttributes);
            services.AddScoped<IUserAttributeProvider, TestUserAttributeProvider>();
            services.AddSingleton<ISecretStore>(Secrets);
            services.AddSingleton<TeamBeats.Modules.Integrations.ClockData.IClockFileSource>(Sftp);
            services.AddSingleton<TeamBeats.Modules.Integrations.Siem.ISyslogSender>(Syslog);
            services.AddSingleton<TeamBeats.Persistence.IMalwareScanner, TeamBeats.Persistence.TestSignatureScanner>();
            services.AddScoped<TeamBeats.Modules.Notifications.IEmailSender>(_ => Email);
            services.AddSingleton(AgentTools);
            services.AddSingleton(Ticketing);
            services.AddSingleton<TeamBeats.Modules.Integrations.Runtime.IConnectorPack, TestTicketingPack>();
            services.AddScoped<TeamBeats.Modules.Agents.Contracts.IAgentTool, TestNoteTool>();
            services.AddScoped<TeamBeats.Modules.Agents.Contracts.IAgentTool, TestDraftTool>();
            services.AddScoped<TeamBeats.Modules.Agents.Contracts.IAgentTool, TestSecretTool>();
            services.AddHttpClient(HttpOidcProvider.ClientName).ConfigurePrimaryHttpMessageHandler(() => Idp);
            services.AddHttpClient(TeamBeats.Modules.Integrations.Microsoft365.HttpMicrosoftGraph.ClientName).ConfigurePrimaryHttpMessageHandler(() => Graph);
            services.AddHttpClient(TeamBeats.Modules.Integrations.Weather.OpenMeteoWeather.ClientName).ConfigurePrimaryHttpMessageHandler(() => Weather);
            services.AddHttpClient(TeamBeats.Persistence.GotenbergConverter.ClientName).ConfigurePrimaryHttpMessageHandler(() => Gotenberg);
            services.AddHttpClient(TeamBeats.Modules.AiGateway.OpenRouter.OpenRouterProvider.ClientName).ConfigurePrimaryHttpMessageHandler(() => OpenRouter);
            services.AddHttpClient(TeamBeats.Modules.Integrations.Runtime.SafeHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => Rest);
            services.AddHttpClient(TeamBeats.Modules.Integrations.WebPush.WebPushClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => Push);
        });
    }

    public HttpClient ClientFor(string host, string? sessionToken = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"https://{host}"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        if (sessionToken is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"tb_session={sessionToken}");
        }
        return client;
    }

    public HttpClient TenantClient(string tenantKey, string? token = null) => ClientFor($"{tenantKey}.{BaseDomain}", token);

    /// <summary>
    /// A client for a person the test world seeded. Such a person stands for someone who has just signed in, so before a
    /// change the handler renews the sign-in time of their session when it is older than five minutes. Without it the
    /// sessions seeded at the start of the run fall outside the ten minute step-up window (spec 026 FR-005) once the
    /// suite runs longer than that, and step-up actions late in the run fail with 401. Only sessions made with the test
    /// method are renewed; the step-up tests use real sign-in sessions and age them on purpose.
    /// </summary>
    public HttpClient SeededUserClient(string tenantKey, Guid tenantId, string token)
    {
        var client = CreateDefaultClient(new Uri($"https://{tenantKey}.{BaseDomain}"), new FreshSignInHandler(this, tenantId, SessionTokens.Hash(token)));
        client.DefaultRequestHeaders.Add("Cookie", $"tb_session={token}");
        return client;
    }

    private sealed class FreshSignInHandler(ApiFactory factory, Guid tenantId, string tokenHash) : DelegatingHandler
    {
        private static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(5);
        private DateTimeOffset renewed = DateTimeOffset.UtcNow;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = factory.Services.GetRequiredService<IClock>().UtcNow;
            if (request.Method != HttpMethod.Get && now - renewed > RenewAfter)
            {
                await factory.Database.OwnerExecInTenantAsync(tenantId,
                    "update identity.user_session set authenticated_at = @now where token_hash = @h and method = 'test'", ("now", now), ("h", tokenHash));
                renewed = now;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    public HttpClient PlatformClient(string? token = null) => ClientFor($"platform.{BaseDomain}", token);

    public async Task<T> InScopeAsync<T>(Guid? tenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        ctx.SetTenant(tenantId);
        ctx.SetActor(ActorType.System, null);
        return await action(scope.ServiceProvider);
    }

    /// <summary>Runs code as a tenant user, as an HTTP request of theirs would.</summary>
    public async Task<T> AsUserAsync<T>(Guid tenantId, Guid userId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        ctx.SetTenant(tenantId);
        ctx.SetActor(ActorType.User, userId);
        return await action(scope.ServiceProvider);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }
}

/// <summary>Stub content resource type used to test resource-level decisions before content modules exist.</summary>
public sealed class TestDocuments
{
    public const string ResourceType = "test.document";

    private readonly ConcurrentDictionary<Guid, (Guid TenantId, ResourceDescriptor Descriptor, string Title, string? TargetUnit)> _docs = new();

    public Guid Add(Guid tenantId, Guid? owningUnit, ResourceVisibility visibility, string title, string? targetedTo = null, string? classification = null)
    {
        var id = Guid.NewGuid();
        _docs[id] = (tenantId, new ResourceDescriptor(ResourceType, id, owningUnit, visibility, classification), title, targetedTo);
        return id;
    }

    public ResourceDescriptor? Describe(Guid tenantId, Guid id) =>
        _docs.TryGetValue(id, out var d) && d.TenantId == tenantId ? d.Descriptor : null;

    public string? TargetOf(Guid id) => _docs.TryGetValue(id, out var d) ? d.TargetUnit : null;
}

public sealed class TestDocumentProvider(TestDocuments docs, ITenantContext context) : IResourceDescriptorProvider
{
    public string ResourceType => TestDocuments.ResourceType;

    public string ManagePermission => Permissions.ContentEdit;

    public Task<ResourceDescriptor?> DescribeAsync(Guid resourceId, CancellationToken ct) =>
        Task.FromResult(context.TenantId is { } t ? docs.Describe(t, resourceId) : null);
}

public static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");
        }
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public static async Task<HttpResponseMessage> EnsureOk(this Task<HttpResponseMessage> pending)
    {
        var response = await pending;
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");
        }
        return response;
    }
}

/// <summary>
/// Stand-in for a directory or HR attribute source (spec 004 section 6), so condition tests can use attributes such as
/// job grade before the HR integration exists. Attributes are per tenant and user.
/// </summary>
public sealed class TestUserAttributes
{
    private readonly ConcurrentDictionary<(Guid TenantId, Guid UserId), ConcurrentDictionary<string, string>> _values = new();

    public void Set(Guid tenantId, Guid userId, string key, string? value)
    {
        var map = _values.GetOrAdd((tenantId, userId), _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        if (value is null)
        {
            map.TryRemove(key, out _);
        }
        else
        {
            map[key] = value;
        }
    }

    public IReadOnlyDictionary<string, string> Get(Guid tenantId, Guid userId) =>
        _values.TryGetValue((tenantId, userId), out var map) ? new Dictionary<string, string>(map) : new Dictionary<string, string>();
}

public sealed class TestUserAttributeProvider(TestUserAttributes attributes, ITenantContext context) : IUserAttributeProvider
{
    public Task<IReadOnlyDictionary<string, string>> GetAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult(context.TenantId is { } t ? attributes.Get(t, userId) : new Dictionary<string, string>());
}
