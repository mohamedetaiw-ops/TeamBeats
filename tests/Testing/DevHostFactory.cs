using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TeamBeats.Api.DevHost;

namespace TeamBeats.Testing;

/// <summary>
/// The development host (with development sign-in) on the shared test database. Used only by sign-in tests.
/// <paramref name="environment"/> and <paramref name="settings"/> let tests run it as the staging composition (spec 003 US7).
/// </summary>
public sealed class DevHostFactory(TestDatabase database, string environment = "Test", IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<DevHostProgram>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:App", database.AppConnectionString);
        builder.UseSetting("Tenancy:Hosts:BaseDomain", ApiFactory.BaseDomain);
        builder.UseSetting("Storage:Root", Path.Combine(Path.GetTempPath(), "tb-devhost-" + Guid.NewGuid().ToString("N")[..8]));
        builder.UseSetting("Messaging:Transport", "InMemory");
        builder.UseSetting("Auth:DevSignIn:Enabled", "true");
        // The demo data package (docs/testing/demo-data.md) is heavy; DemoDataSeedTests runs it on purpose, other host tests leave it off.
        builder.UseSetting("Dev:SeedDemo:Folder", "");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
    }

    public HttpClient Tenant(string key, string? token = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"https://{key}.{ApiFactory.BaseDomain}"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"tb_session={token}");
        }
        return client;
    }

    /// <summary>Extracts the session token from a sign-in response.</summary>
    public static string? SessionToken(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("tb_session=", StringComparison.Ordinal))?["tb_session=".Length..]
            : null;
}
