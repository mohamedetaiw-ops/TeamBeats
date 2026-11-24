using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Modules.Identity.Sso;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 003 R2 (US4, US5): provider settings per kind and the subject each token links under (FR-003).</summary>
public sealed class SignInProviderRulesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly HashSet<string> NoRoles = [];

    private static IdentityProviderConfig Valid(string kind, string issuer, string clientId, IReadOnlyList<string>? hosted = null, Guid? connection = null) =>
        IdentityProviderStore.Validate(Tenant,
            new IdentityProviderRequest(kind, issuer, clientId, SecretReferences.For(Tenant, "sso"), null, false, [], [], true, hosted, connection), NoRoles);

    private static string[] Errors(Action act)
    {
        var ex = Assert.Throws<AppException>(act);
        return ex.Errors!.SelectMany(e => e.Value.Select(v => e.Key + ":" + v)).ToArray();
    }

    [Fact]
    public void Google_keeps_its_hosted_domains_in_lower_case_and_drops_a_connection()
    {
        var config = Valid("google", IdentityProviderStore.GoogleIssuer, "1-abc.apps.googleusercontent.com", [" Corp.Example ", "corp.example"], Guid.NewGuid());
        Assert.Equal(["corp.example"], config.HostedDomains);
        Assert.Null(config.DirectoryConnectionId);
    }

    [Fact]
    public void Adfs_needs_a_connection_and_an_https_adfs_issuer_and_drops_hosted_domains()
    {
        var connection = Guid.NewGuid();
        var config = Valid("ADFS", "https://adfs.corp.local/adfs", "my-client", ["corp.example"], connection);
        Assert.Equal("adfs", config.Kind);
        Assert.Equal(connection, config.DirectoryConnectionId);
        Assert.Empty(config.HostedDomains!);
        Assert.Contains("directoryConnectionId:validation.idp.directory_connection_required", Errors(() => Valid("adfs", "https://adfs.corp.local/adfs", "c")));
        Assert.Contains("issuer:validation.idp.issuer_invalid", Errors(() => Valid("adfs", "https://adfs.corp.local/adfs/?x=1", "c", connection: connection)));
        Assert.Contains("issuer:validation.idp.issuer_invalid", Errors(() => Valid("adfs", "https://user@adfs.corp.local/adfs", "c", connection: connection)));
        Assert.Contains("clientId:validation.idp.client_id_invalid", Errors(() => Valid("adfs", "https://adfs.corp.local/adfs", "a b", connection: connection)));
    }

    [Fact]
    public void Entra_rules_stay_as_they_were()
    {
        Assert.Contains("issuer:validation.idp.issuer_invalid", Errors(() => Valid("entra", "https://login.microsoftonline.com/common/v2.0", Guid.NewGuid().ToString())));
        Assert.Contains("issuer:validation.idp.issuer_invalid", Errors(() => Valid("entra", IdentityProviderStore.GoogleIssuer, Guid.NewGuid().ToString())));
    }

    private static JsonWebToken Token(Dictionary<string, object> claims) =>
        new(new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Claims = claims }));

    private static IdentityProviderConfig Config(string kind, IReadOnlyList<string>? hosted = null, Guid? connection = null) =>
        new(Guid.NewGuid(), kind, "https://issuer.example", "client", "ref", null, false, [], [], true, hosted, connection);

    [Fact]
    public void Each_kind_links_on_its_immutable_subject()
    {
        var oid = Guid.NewGuid().ToString("D").ToUpperInvariant();
        Assert.Equal(oid.ToLowerInvariant(), SsoService.LinkFor(Config("entra"), Token(new() { ["oid"] = oid, ["sub"] = "pairwise" })).Subject);

        var google = SsoService.LinkFor(Config("google", ["corp.example"]), Token(new() { ["sub"] = "1098", ["hd"] = "CORP.example" }));
        Assert.Equal(("https://issuer.example", "1098"), (google.Issuer, google.Subject));
        Assert.Equal("hosted_domain_mismatch", SsoService.LinkFor(Config("google", ["corp.example"]), Token(new() { ["sub"] = "1098" })).Problem);
        Assert.Equal("hosted_domain_mismatch", SsoService.LinkFor(Config("google", ["corp.example"]), Token(new() { ["sub"] = "1098", ["hd"] = "other.example" })).Problem);

        var connection = Guid.NewGuid();
        var guid = Guid.NewGuid();
        var adfs = SsoService.LinkFor(Config("adfs", connection: connection), Token(new() { ["objectGUID"] = Convert.ToBase64String(guid.ToByteArray()), ["sub"] = "x" }));
        // Linked under the AD DS connection's issuer, never the AD FS issuer, so it meets that connection's synced users only.
        Assert.Equal((DirectorySyncService.OnPremIssuer(connection), guid.ToString("D")), (adfs.Issuer, adfs.Subject));
        Assert.Equal("object_guid_missing", SsoService.LinkFor(Config("adfs", connection: connection), Token(new() { ["sub"] = "x" })).Problem);
        Assert.Equal("kind_unsupported", SsoService.LinkFor(Config("generic"), Token(new() { ["sub"] = "x" })).Problem);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("AAAA")]
    [InlineData("")]
    public void Object_guid_refuses_anything_but_sixteen_bytes_or_a_guid(string value) => Assert.Null(SsoService.ObjectGuid(value));

    [Fact]
    public void Object_guid_reads_base64_bytes_like_the_sync_agent_and_the_text_form()
    {
        var guid = Guid.NewGuid();
        Assert.Equal(guid.ToString("D"), SsoService.ObjectGuid(Convert.ToBase64String(guid.ToByteArray())));
        Assert.Equal(guid.ToString("D"), SsoService.ObjectGuid(guid.ToString("D").ToUpperInvariant()));
    }
}
