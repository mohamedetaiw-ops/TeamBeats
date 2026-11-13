using Microsoft.Extensions.Configuration;
using TeamBeats.Api;
using TeamBeats.Modules.Identity.Sso;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 003 T017 (open redirect) and FR-002 (secret references scoped to their tenant).</summary>
public sealed class SignInRulesTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/news?tab=1#top", "/news?tab=1#top")]
    [InlineData("/ar/directory", "/ar/directory")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/x\\y", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/news\r\nSet-Cookie: x=1", "/")]
    [InlineData("/api/v1/tenant/settings", "/")]
    [InlineData("/API/v1/me", "/")]
    public void Return_path_is_same_origin_relative_only(string? input, string expected) =>
        Assert.Equal(expected, ReturnPaths.Sanitize(input));

    [Fact]
    public void Overlong_return_path_falls_back_to_home() =>
        Assert.Equal("/", ReturnPaths.Sanitize("/" + new string('a', 2048)));

    [Fact]
    public void An_app_authorization_request_survives_the_sign_in()
    {
        // Spec 035 FR-003: the authorization endpoint sends the browser to sign in and back with its whole query.
        var request = "/oauth/authorize?client_id=tb_x&response_type=code&redirect_uri=https%3A%2F%2Fapp.example%2Fcb&scope=profile%3Aread&code_challenge="
            + new string('c', 43) + "&code_challenge_method=S256&state=" + new string('s', 400) + "&resource=https%3A%2F%2Facme.example%2Fmcp";
        Assert.True(request.Length > 512);
        Assert.Equal(request, ReturnPaths.Sanitize(request));
    }

    [Fact]
    public void Secret_reference_belongs_to_one_tenant()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var reference = SecretReferences.For(a, "entra-client");
        Assert.Equal("entra-client", SecretReferences.NameFor(a, reference));
        Assert.Null(SecretReferences.NameFor(b, reference));
        Assert.Null(SecretReferences.NameFor(a, "secret://tenants/" + a + "/../" + b + "/x"));
        Assert.Null(SecretReferences.NameFor(a, "entra-client"));
        Assert.Null(SecretReferences.NameFor(a, null));
    }

    [Fact]
    public async Task Configuration_secret_store_resolves_only_the_owning_tenant()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Secrets:tenants:{a:D}:entra-client"] = "fictional-value",
        }).Build();
        var store = new ConfigurationSecretStore(configuration);
        Assert.Equal("fictional-value", await store.GetTenantSecretAsync(a, SecretReferences.For(a, "entra-client")));
        Assert.Null(await store.GetTenantSecretAsync(b, SecretReferences.For(a, "entra-client")));
        Assert.Null(await store.GetTenantSecretAsync(a, SecretReferences.For(a, "missing")));
    }
}
