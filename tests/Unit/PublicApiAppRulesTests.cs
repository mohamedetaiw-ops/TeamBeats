using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Authorization;
using TeamBeats.Modules.PublicApi;
using TeamBeats.Modules.PublicApi.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 035 T005: scopes only narrow (FR-009), roles apps may hold carry no administration permission (FR-011), and
/// only public keys of the allowed kinds are registered for private key JWT (FR-005).
/// </summary>
public sealed class PublicApiAppRulesTests
{
    [Fact]
    public void Adding_a_scope_never_grants_a_decision_the_roles_do_not()
    {
        var random = new Random(35);
        var units = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var permissions = new[] { Permissions.ContentView, Permissions.DocView, Permissions.PeopleView, Permissions.PageView, Permissions.OrgUnitView };
        var scopes = ScopeCatalogue.Keys.ToArray();
        for (var i = 0; i < 500; i++)
        {
            var grants = Enumerable.Range(0, random.Next(4)).Select(_ =>
            {
                var unit = units[random.Next(units.Length)];
                var tenantWide = random.Next(4) == 0;
                return new Grant(Guid.NewGuid(), BuiltInRoles.AppReader, tenantWide ? ScopeType.Tenant : ScopeType.OrgUnit, tenantWide ? null : unit, null,
                    permissions.Where(_ => random.Next(2) == 0).ToHashSet(StringComparer.Ordinal), tenantWide ? new HashSet<Guid>() : new HashSet<Guid> { unit }, "app");
            }).ToList();
            var app = new EffectiveGrants(Guid.NewGuid(), true, grants, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), []);
            var held = scopes.Where(_ => random.Next(3) == 0).ToHashSet(StringComparer.Ordinal);
            var more = held.Union(scopes.Where(_ => random.Next(2) == 0)).ToHashSet(StringComparer.Ordinal);
            var narrow = new PublicCaller(app.UserId, OAuthServer.Application, null, held, Guid.NewGuid());
            var wide = narrow with { Scopes = more };
            AuthzTarget target = random.Next(2) == 0 ? AuthzTarget.Tenant : AuthzTarget.OrgUnit(units[random.Next(units.Length)]);
            foreach (var scope in scopes)
            {
                foreach (var permission in permissions)
                {
                    var decision = Evaluator.Evaluate(app, permission, target, []);
                    if (wide.May(scope, decision))
                    {
                        Assert.True(decision.Allowed, $"iteration {i}");
                    }
                    if (narrow.May(scope, decision))
                    {
                        Assert.True(wide.May(scope, decision), $"iteration {i}");
                    }
                    Assert.False(narrow.May(scope, decision with { Allowed = false }));
                }
            }
        }
    }

    [Theory]
    [InlineData("authz.roles_assign")]
    [InlineData("identity.users_manage")]
    [InlineData("security.manage")]
    [InlineData("api.manage")]
    [InlineData("api.view")]
    [InlineData("audit.export")]
    [InlineData("ai.policy_manage")]
    public void Administration_permissions_are_excluded_from_apps(string permission) => Assert.True(Permissions.ExcludedFromApps(permission));

    [Fact]
    public void Roles_apps_may_hold_carry_no_excluded_permission()
    {
        Assert.Contains(Permissions.All, p => !Permissions.ExcludedFromApps(p));
        Assert.False(Permissions.ExcludedFromApps(Permissions.ContentView));
        Assert.False(Permissions.ExcludedFromApps(Permissions.AuditView));
        var forApps = BuiltInRoleDefinitions.All.Where(r => r.AssignableToApps).ToList();
        Assert.Equal([BuiltInRoles.AppReader], forApps.Select(r => r.Key));
        Assert.All(forApps, r => Assert.DoesNotContain(r.Permissions, Permissions.ExcludedFromApps));
        Assert.All(forApps, r => Assert.DoesNotContain(r.Permissions, Permissions.ResourceOnly.Contains));
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Only_public_ec_p256_and_long_rsa_keys_are_registered()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(true);
        var x = Base64UrlEncoder.Encode(p.Q.X);
        var y = Base64UrlEncoder.Encode(p.Q.Y);
        var parsed = ClientKeys.Parse(Json(new { kty = "EC", crv = "P-256", x, y, kid = "mine", use = "sig" }), out var error);
        Assert.Null(error);
        Assert.NotNull(parsed);
        // Only the public parameters are kept; the thumbprint follows RFC 7638 and names the key.
        Assert.DoesNotContain("kid", parsed!.Value.Json, StringComparison.Ordinal);
        Assert.Equal(Base64UrlEncoder.Encode(new JsonWebKey(parsed.Value.Json).ComputeJwkThumbprint()), parsed.Value.Thumbprint);

        Assert.Null(ClientKeys.Parse(Json(new { kty = "EC", crv = "P-256", x, y, d = Base64UrlEncoder.Encode(p.D) }), out error));
        Assert.Equal("validation.api.public_key_private", error);
        Assert.Null(ClientKeys.Parse(Json(new { kty = "oct", k = "c2VjcmV0" }), out error));
        Assert.Equal("validation.api.public_key_private", error);
        Assert.Null(ClientKeys.Parse(Json(new { kty = "EC", crv = "P-384", x, y }), out error));
        Assert.Equal("validation.api.public_key_invalid", error);
        Assert.Null(ClientKeys.Parse(Json(new { kty = "EC", crv = "P-256", x, y, alg = "RS256" }), out _));
        Assert.Null(ClientKeys.Parse(Json(new { kty = "EC", crv = "P-256", x = "%%", y }), out _));
        Assert.Null(ClientKeys.Parse(Json("not an object"), out _));
        Assert.Null(ClientKeys.Parse(null, out _));

        using var shortRsa = RSA.Create(1024);
        var s = shortRsa.ExportParameters(false);
        Assert.Null(ClientKeys.Parse(Json(new { kty = "RSA", n = Base64UrlEncoder.Encode(s.Modulus), e = Base64UrlEncoder.Encode(s.Exponent) }), out _));
        using var rsa = RSA.Create(2048);
        var r = rsa.ExportParameters(false);
        Assert.NotNull(ClientKeys.Parse(Json(new { kty = "RSA", n = Base64UrlEncoder.Encode(r.Modulus), e = Base64UrlEncoder.Encode(r.Exponent), alg = "PS256" }), out _));
    }
}
