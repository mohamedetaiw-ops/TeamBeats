using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

public sealed record SignageScreenRequest(string? Name, Guid? OrgUnitId, string? Location);

public sealed record SignageScreenView(Guid Id, string Name, Guid OrgUnitId, string? Location, DateTimeOffset CreatedAt, DateTimeOffset? RotatedAt,
    DateTimeOffset? LastSeenAt);

/// <summary>A screen with its address. The address is shown only when the screen is registered or its address replaced.</summary>
public sealed record SignageScreenAddress(SignageScreenView Screen, string Path);

public sealed record LocationSignageFeed(string Name, int RefreshSeconds, IReadOnlyList<SignageAnnouncement> Items);

/// <summary>
/// Signage screens of a location (spec 015 US7, FR-021). A location administrator registers a screen at an org unit,
/// optionally naming the location people's profiles use, and gets a signed address once. The screen reads its feed
/// without signing in: published Public announcements marked for signage that target that place (Content decides). The
/// feed is read live on every request and the screen asks again every 30 seconds, so a withdrawn announcement or a
/// revoked or replaced address is gone from the screen within a minute.
/// </summary>
public sealed class SignageScreens(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ISignageAnnouncements announcements,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxScreens = 500;
    public const int RefreshSeconds = 30;

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string PathOf(string token) => $"/screens/{token}";

    private const string Columns = "id, name, org_unit_id, location, created_at, rotated_at, last_seen_at";

    private static SignageScreenView Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.Get<string?>("location"), r.GetFieldValue<DateTimeOffset>(4), r.Get<DateTimeOffset?>("rotated_at"),
            r.Get<DateTimeOffset?>("last_seen_at"));

    /// <summary>The screens of the units the caller manages signage for.</summary>
    public async Task<IReadOnlyList<SignageScreenView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.NotificationsSignage, ct);
        var coverage = await authz.CoverageAsync(Permissions.NotificationsSignageManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        var all = await session.QueryAsync($"select {Columns} from notifications.signage_screen order by name, id", null, Map, ct);
        return all.Where(s => coverage.Covers(s.OrgUnitId)).ToList();
    }

    private async Task<(string Name, Guid Unit, string? Location)> ValidAsync(SignageScreenRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            errors.Add("name", "validation.notifications.screen_name");
        }
        var location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim();
        if (location is { Length: > 100 })
        {
            errors.Add("location", "validation.notifications.screen_location");
        }
        if (request.OrgUnitId is not { } unit || !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("orgUnitId", "validation.notifications.screen_unit");
        }
        errors.ThrowIfAny();
        return (name!, request.OrgUnitId!.Value, location);
    }

    public async Task<SignageScreenAddress> CreateAsync(SignageScreenRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.NotificationsSignage, ct);
        var (name, unit, location) = await ValidAsync(request, ct);
        await authz.DemandAsync(Permissions.NotificationsSignageManage, AuthzTarget.OrgUnit(unit), ct);
        if (await session.ScalarAsync<int>("select count(*)::int from notifications.signage_screen", null, ct) >= MaxScreens)
        {
            throw AppException.Conflict("conflict.notifications.screen_limit");
        }
        var id = Ids.New();
        var token = NewToken();
        await session.ExecAsync(
            """
            insert into notifications.signage_screen (tenant_id, id, name, org_unit_id, location, token_hash, created_by, created_at)
            values (platform.current_tenant_id(), @id, @name, @unit, @location, @hash, @user, @now)
            """, new { id, name, unit, location, hash = Hash(token), user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.NotificationSignageScreenRegistered, "signage_screen", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { name, location }), ct);
        return new SignageScreenAddress(await ScreenAsync(id, ct), PathOf(token));
    }

    private async Task<SignageScreenView> ScreenAsync(Guid id, CancellationToken ct) =>
        (await session.QueryAsync($"select {Columns} from notifications.signage_screen where id = @id", new { id }, Map, ct)).Single();

    /// <summary>A screen the caller manages, locked for the change. Another unit's screen reads as not found.</summary>
    private async Task<SignageScreenView> ManagedAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.NotificationsSignage, ct);
        context.RequireUser();
        var screen = (await session.QueryAsync($"select {Columns} from notifications.signage_screen where id = @id for update", new { id }, Map, ct))
            .FirstOrDefault() ?? throw AppException.NotFound();
        if (!(await authz.CheckAsync(Permissions.NotificationsSignageManage, AuthzTarget.OrgUnit(screen.OrgUnitId), ct)).Allowed)
        {
            throw AppException.NotFound();
        }
        return screen;
    }

    public async Task<SignageScreenView> UpdateAsync(Guid id, SignageScreenRequest request, CancellationToken ct)
    {
        var screen = await ManagedAsync(id, ct);
        var (name, unit, location) = await ValidAsync(request, ct);
        if (unit != screen.OrgUnitId)
        {
            await authz.DemandAsync(Permissions.NotificationsSignageManage, AuthzTarget.OrgUnit(unit), ct);
        }
        await session.ExecAsync("update notifications.signage_screen set name = @name, org_unit_id = @unit, location = @location where id = @id",
            new { id, name, unit, location }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.NotificationSignageScreenRegistered, "signage_screen", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { name, location, orgUnitId = unit }), ct);
        return await ScreenAsync(id, ct);
    }

    /// <summary>Replaces the address: the old one stops at once, and the screen needs the new one.</summary>
    public async Task<SignageScreenAddress> RotateAsync(Guid id, CancellationToken ct)
    {
        var screen = await ManagedAsync(id, ct);
        var token = NewToken();
        await session.ExecAsync("update notifications.signage_screen set token_hash = @hash, rotated_at = @now where id = @id",
            new { id, hash = Hash(token), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.NotificationSignageScreenRotated, "signage_screen", id.ToString(), TargetOrgUnitId: screen.OrgUnitId), ct);
        return new SignageScreenAddress(await ScreenAsync(id, ct), PathOf(token));
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct)
    {
        var screen = await ManagedAsync(id, ct);
        await session.ExecAsync("delete from notifications.signage_screen where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.NotificationSignageScreenRevoked, "signage_screen", id.ToString(), TargetOrgUnitId: screen.OrgUnitId,
            Changes: new { name = screen.Name }), ct);
    }

    /// <summary>
    /// The feed of a screen, read without a person: the tenant comes from the host, the token finds the screen. An unknown,
    /// replaced or revoked token and a plan without signage all read as not found.
    /// </summary>
    public async Task<LocationSignageFeed> FeedAsync(string token, CancellationToken ct)
    {
        if (token.Length is < 20 or > 100 || !await entitlements.IsEnabledAsync(Capabilities.NotificationsSignage, ct))
        {
            throw AppException.NotFound();
        }
        var screen = (await session.QueryAsync($"select {Columns} from notifications.signage_screen where token_hash = @hash", new { hash = Hash(token) }, Map, ct))
            .FirstOrDefault() ?? throw AppException.NotFound();
        var now = clock.UtcNow;
        // How recently the screen asked is shown to its administrators; it is written at most once a minute.
        if (screen.LastSeenAt is not { } seen || now - seen > TimeSpan.FromMinutes(1))
        {
            await session.ExecAsync("update notifications.signage_screen set last_seen_at = @now where id = @id", new { id = screen.Id, now }, ct);
        }
        var items = await announcements.ForPlaceAsync(screen.OrgUnitId, screen.Location, 20, ct);
        return new LocationSignageFeed(screen.Name, RefreshSeconds, items);
    }
}
