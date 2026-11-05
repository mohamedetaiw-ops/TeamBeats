using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Identity;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Testing;

public sealed record TestUser(string Name, Guid Id, string Email, string Token, HttpClient Client);

public sealed class TestTenant(string key, Guid id)
{
    public string Key { get; } = key;
    public Guid Id { get; } = id;
    public Dictionary<string, Guid> Units { get; } = [];
    public Dictionary<string, TestUser> Users { get; } = [];

    public TestUser this[string user] => Users[user];
    public Guid Unit(string name) => Units[name];
}

/// <summary>
/// The shared R0 test world (r0-solution-structure section 7): tenants A ("alpha", Workplace Plus) and B ("bravo",
/// Workplace), each with a three-level tree (Holding > Gulf, Africa > departments) and one user per role.
/// Everything is created through the public API except users and sessions, which have no API until R1 sign-in.
/// </summary>
public sealed class TenantWorld : IAsyncLifetime
{
    public TestDatabase Database { get; private set; } = null!;
    public ApiFactory Factory { get; private set; } = null!;
    public HttpClient Platform { get; private set; } = null!;
    public Guid PlatformOperatorId { get; private set; }
    public TestTenant A { get; private set; } = null!;
    public TestTenant B { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Database = await TestDatabase.CreateAsync();
        Factory = new ApiFactory(Database);
        (PlatformOperatorId, var token) = await CreatePlatformOperatorAsync("operator@platform.example");
        Platform = Factory.PlatformClient(token);
        A = await BuildTenantAsync("alpha", "workplace_plus");
        B = await BuildTenantAsync("bravo", "workplace");
    }

    public async Task<(Guid Id, string Token)> CreatePlatformOperatorAsync(string email)
    {
        var id = Guid.NewGuid();
        var token = SessionTokens.NewToken();
        await Database.OwnerExecAsync("insert into identity.platform_operator (id, email, display_name, status) values (@id, @e, @e, 'active')",
            ("id", id), ("e", email));
        await Database.OwnerExecAsync(
            "insert into identity.platform_session (id, operator_id, token_hash, expires_at) values (@sid, @id, @h, now() + interval '8 hours')",
            ("sid", Guid.NewGuid()), ("id", id), ("h", SessionTokens.Hash(token)));
        return (id, token);
    }

    public async Task<TestTenant> CreateTenantAsync(string key, string package, string defaultLocale = "en", string[]? locales = null)
    {
        var response = await Platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            key,
            displayName = $"{key} holding",
            packageKey = package,
            defaultLocale,
            enabledLocales = locales ?? ["ar", "en"],
            timeZone = "Asia/Dubai",
            admins = new[] { $"admin@{key}.example" },
        });
        var created = await response.ReadAsync<JsonElement>();
        var tenant = new TestTenant(key, created.GetProperty("id").GetGuid());
        tenant.Users["admin"] = await SeedUserAsync(tenant, "admin", MembershipTypes.Employee);
        return tenant;
    }

    /// <summary>Creates a user with a tenant membership and a session; claims any pending admin appointment for the email.</summary>
    public async Task<TestUser> SeedUserAsync(TestTenant tenant, string name, string membershipType)
    {
        var email = $"{name}@{tenant.Key}.example";
        var (id, token) = await Factory.InScopeAsync(tenant.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            var userId = Ids.New();
            await session.ExecAsync(
                "insert into identity.user_account (tenant_id, id, display_name, email, status, created_via) values (@t, @id, @n, @e, 'active', 'admin')",
                new { t = tenant.Id, id = userId, n = $"{name} ({tenant.Key})", e = email });
            await session.ExecAsync(
                "insert into identity.external_identity (tenant_id, id, issuer, subject, user_id) values (@t, @id, 'urn:teambeats:test', @e, @u)",
                new { t = tenant.Id, id = Ids.New(), e = email, u = userId });
            var memberships = sp.GetRequiredService<ITenantMemberships>();
            await memberships.CreateAsync(userId, membershipType);
            await memberships.ClaimAdminAppointmentsAsync(email, userId);
            var tokenValue = await sp.GetRequiredService<SessionService>().CreateUserSessionAsync(userId, "test", CancellationToken.None);
            await session.CommitAsync();
            return (userId, tokenValue);
        });
        var user = new TestUser(name, id, email, token, Factory.TenantClient(tenant.Key, token));
        tenant.Users[name] = user;
        return user;
    }

    public async Task<Guid> CreateUnitAsync(TestTenant tenant, string code, Guid? parent, string type, string? asUser = null)
    {
        var client = tenant[asUser ?? "admin"].Client;
        var response = await client.PostAsJsonAsync("/api/v1/org/units", new
        {
            parentId = parent,
            typeKey = type,
            code,
            names = new Dictionary<string, string> { ["en"] = code, ["ar"] = "وحدة " + code },
        });
        var json = await response.ReadAsync<JsonElement>();
        var id = json.GetProperty("unit").GetProperty("id").GetGuid();
        tenant.Units[code] = id;
        return id;
    }

    public async Task AddMembershipAsync(TestTenant tenant, string user, string unit, string type = "primary")
    {
        var response = await tenant["admin"].Client.PostAsJsonAsync($"/api/v1/org/units/{tenant.Unit(unit)}/memberships",
            new { userId = tenant[user].Id, type });
        await response.ReadAsync<JsonElement>();
    }

    public async Task<Guid> AssignAsync(TestTenant tenant, string user, string role, string scopeType, Guid? scopeId, string asUser = "admin")
    {
        var response = await tenant[asUser].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "user", subjectId = tenant[user].Id, roleKey = role, scopeType, scopeId });
        return (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<TestTenant> BuildTenantAsync(string key, string package)
    {
        var t = await CreateTenantAsync(key, package);
        var holding = await CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await CreateUnitAsync(t, "Africa", holding, "subsidiary");
        await CreateUnitAsync(t, "GulfFinance", gulf, "department");
        await CreateUnitAsync(t, "GulfHR", gulf, "department");
        await CreateUnitAsync(t, "AfricaFinance", africa, "department");

        foreach (var name in new[] { "orgadmin", "publisher", "editor_unit", "employee_gulf", "employee_africa", "employee_holding", "security" })
        {
            await SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await SeedUserAsync(t, "contractor", MembershipTypes.Contractor);

        await AddMembershipAsync(t, "employee_gulf", "GulfFinance");
        await AddMembershipAsync(t, "employee_africa", "AfricaFinance");
        await AddMembershipAsync(t, "employee_holding", "Holding");
        await AddMembershipAsync(t, "publisher", "Gulf");
        await AddMembershipAsync(t, "orgadmin", "Gulf");

        await AssignAsync(t, "publisher", "content_publisher", "org_subtree", gulf);
        await AssignAsync(t, "editor_unit", "content_publisher", "org_unit", gulf);
        await AssignAsync(t, "security", "security_officer", "tenant", null);
        if (package != "workplace")
        {
            await AssignAsync(t, "orgadmin", "organization_administrator", "org_subtree", gulf);
        }
        return t;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
    }
}

