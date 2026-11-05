using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Api;
using TeamBeats.Authorization.Contracts;
using TeamBeats.TenantContext;

namespace TeamBeats.Testing;

/// <summary>
/// The production API composition (ADR-0004: tests use the real session pipeline with sessions created by the
/// harness, not development sign-in) on a fresh database.
/// </summary>
public sealed class ApiFactory(TestDatabase database) : WebApplicationFactory<ApiProgram>
{
    public const string BaseDomain = "teambeats.test";

    public TestDatabase Database { get; } = database;

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "tb-storage-" + Guid.NewGuid().ToString("N")[..8]);

    public TestDocuments Documents { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("ConnectionStrings:App", Database.AppConnectionString);
        builder.UseSetting("Tenancy:Hosts:BaseDomain", BaseDomain);
        builder.UseSetting("Storage:Root", StorageRoot);
        builder.UseSetting("Messaging:Transport", "InMemory");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Documents);
            services.AddScoped<IResourceDescriptorProvider, TestDocumentProvider>();
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

    public HttpClient PlatformClient(string? token = null) => ClientFor($"platform.{BaseDomain}", token);

    public async Task<T> InScopeAsync<T>(Guid? tenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        ctx.SetTenant(tenantId);
        ctx.SetActor(ActorType.System, null);
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

    public Guid Add(Guid tenantId, Guid? owningUnit, ResourceVisibility visibility, string title, string? targetedTo = null)
    {
        var id = Guid.NewGuid();
        _docs[id] = (tenantId, new ResourceDescriptor(ResourceType, id, owningUnit, visibility), title, targetedTo);
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
