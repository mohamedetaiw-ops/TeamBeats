using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy;

public sealed record TenantSettingsView(
    string DisplayName,
    string DefaultLocale,
    IReadOnlyList<string> EnabledLocales,
    string TimeZone,
    string NumberingSystem,
    string? PrimaryColor,
    bool HasLogo,
    int RowVersion);

public sealed record UpdateTenantSettingsRequest(
    string DefaultLocale,
    IReadOnlyList<string> EnabledLocales,
    string TimeZone,
    string? NumberingSystem,
    string? PrimaryColor,
    int RowVersion);

public sealed record AppointAdminRequest(string Email);

public sealed record AdminAppointmentView(Guid Id, string Email, string Status, Guid? UserId, DateTimeOffset CreatedAt);

/// <summary>Tenant admin use cases (spec 001 US2). The tenant always comes from the session; no tenant ID in any path.</summary>
public sealed class TenantSettingsService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationProvisioning authzProvisioning,
    IUserDirectory users,
    TenantObjectStorage storage,
    IAuditWriter audit,
    IOutbox outbox) : ITenantSettingsReader
{
    public async Task<TenantSettingsView> GetAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            """
            select t.display_name, s.default_locale, s.enabled_locales, s.time_zone, s.numbering_system, s.primary_color,
                   s.logo_object_key is not null as has_logo, s.row_version
            from tenancy.tenant_settings s join tenancy.tenant t on t.id = s.tenant_id
            """, null,
            r => new TenantSettingsView(r.GetString(0), r.GetString(1), r.GetFieldValue<string[]>(2), r.GetString(3), r.GetString(4),
                r.Get<string>("primary_color"), r.GetBoolean(6), r.GetInt32(7)), ct)
        ?? throw AppException.NotFound();

    public async Task<TenantLocaleSettings> GetLocaleSettingsAsync(CancellationToken ct = default)
    {
        var s = await GetAsync(ct);
        return new TenantLocaleSettings(s.DefaultLocale, s.EnabledLocales, s.TimeZone, s.NumberingSystem);
    }

    public async Task<TenantSettingsView> UpdateAsync(UpdateTenantSettingsRequest request, CancellationToken ct)
    {
        var current = await GetAsync(ct);
        var numbering = request.NumberingSystem ?? current.NumberingSystem;
        var errors = new ValidationErrors();
        TenantRules.ValidateLocales(request.DefaultLocale, request.EnabledLocales, errors);
        TenantRules.ValidateTimeZone(request.TimeZone, errors);
        TenantRules.ValidateColor(request.PrimaryColor, errors);
        if (numbering is not ("latn" or "arab"))
        {
            errors.Add("numberingSystem", "validation.tenant.numbering_system");
        }
        errors.ThrowIfAny();

        var changed = new List<string>();
        if (request.DefaultLocale != current.DefaultLocale) changed.Add("defaultLocale");
        if (!request.EnabledLocales.OrderBy(x => x).SequenceEqual(current.EnabledLocales.OrderBy(x => x))) changed.Add("enabledLocales");
        if (request.TimeZone != current.TimeZone) changed.Add("timeZone");
        if (numbering != current.NumberingSystem) changed.Add("numberingSystem");
        if (request.PrimaryColor != current.PrimaryColor) changed.Add("primaryColor");

        var updated = await session.ExecAsync(
            """
            update tenancy.tenant_settings
            set default_locale = @locale, enabled_locales = @enabled, time_zone = @tz, numbering_system = @num,
                primary_color = @color, row_version = row_version + 1
            where row_version = @version
            """,
            new
            {
                locale = request.DefaultLocale,
                enabled = request.EnabledLocales.ToArray(),
                tz = request.TimeZone,
                num = numbering,
                color = request.PrimaryColor,
                version = request.RowVersion,
            }, ct);
        if (updated == 0)
        {
            throw AppException.Conflict("conflict.stale_version");
        }

        if (changed.Count > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.TenantSettingsChanged, "tenant_settings", context.TenantId.ToString(),
                Changes: new
                {
                    fields = changed,
                    defaultLocale = request.DefaultLocale,
                    enabledLocales = request.EnabledLocales,
                    timeZone = request.TimeZone,
                    numberingSystem = numbering,
                    primaryColor = request.PrimaryColor,
                }), ct);
            await outbox.EnqueueAsync(new TenantSettingsChanged(context.RequireTenant(), changed), ct);
        }

        return await GetAsync(ct);
    }

    public async Task SetLogoAsync(string? contentType, byte[] data, CancellationToken ct)
    {
        var extension = LogoValidator.Validate(contentType, data);
        var relative = $"branding/logo-{Ids.New():N}.{extension}";
        using var stream = new MemoryStream(data);
        var mime = extension switch { "png" => "image/png", "jpg" => "image/jpeg", _ => "image/svg+xml" };
        await storage.PutAsync(relative, stream, mime, ct);
        await session.ExecAsync(
            "update tenancy.tenant_settings set logo_object_key = @key, logo_content_type = @mime, row_version = row_version + 1",
            new { key = relative, mime }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.TenantSettingsChanged, "tenant_settings", context.TenantId.ToString(),
            Changes: new { fields = new[] { "logo" }, contentType = mime, bytes = data.Length }), ct);
        await outbox.EnqueueAsync(new TenantSettingsChanged(context.RequireTenant(), ["logo"]), ct);
    }

    public async Task<(Stream Content, string ContentType)?> GetLogoAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select logo_object_key, logo_content_type from tenancy.tenant_settings where logo_object_key is not null", null,
            r => (Key: r.GetString(0), Type: r.GetString(1)), ct);
        if (row == default)
        {
            return null;
        }
        var stream = await storage.OpenReadAsync(row.Key, ct);
        return stream is null ? null : (stream, row.Type);
    }

    public async Task<IReadOnlyList<AdminAppointmentView>> ListAdminsAsync(CancellationToken ct) =>
        await session.QueryAsync(
            "select id, invited_email, status, user_id, created_at from tenancy.tenant_admin_appointment order by created_at",
            null, r => new AdminAppointmentView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Get<Guid?>("user_id"), r.GetFieldValue<DateTimeOffset>(4)), ct);

    /// <summary>Appoints a further tenant admin. Existing users get the role now; others are activated on first sign-in.</summary>
    public async Task<AdminAppointmentView> AppointAdminAsync(string email, CancellationToken ct)
    {
        var normalized = TenantRules.NormalizeEmail(email ?? string.Empty);
        if (!TenantRules.LooksLikeEmail(normalized))
        {
            throw AppException.Validation("email", "validation.email");
        }

        var exists = await session.ScalarAsync<bool>(
            "select exists (select 1 from tenancy.tenant_admin_appointment where invited_email = @e and status <> 'revoked')", new { e = normalized }, ct);
        if (exists)
        {
            throw AppException.Conflict("conflict.tenant.admin_already_appointed");
        }

        var user = await users.FindByEmailAsync(normalized, ct);
        var id = Ids.New();
        var status = "pending";
        if (user is not null)
        {
            var active = await session.ScalarAsync<bool>(
                "select exists (select 1 from tenancy.tenant_membership where user_id = @u and status = 'active')", new { u = user.Id }, ct);
            if (active)
            {
                await authzProvisioning.AssignBuiltInRoleAsync(user.Id, BuiltInRoles.TenantAdministrator, ScopeType.Tenant, null, context.ActorId, ct);
                status = "active";
            }
        }

        await session.ExecAsync(
            """
            insert into tenancy.tenant_admin_appointment (tenant_id, id, invited_email, user_id, status, appointed_by, activated_at)
            values (@t, @id, @email, @user, @status, @by, case when @status = 'active' then now() end)
            """,
            new { t = context.RequireTenant(), id, email = normalized, user = status == "active" ? user?.Id : null, status, by = context.ActorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.TenantAdminAppointed, "tenant_admin_appointment", id.ToString(),
            Changes: new { status, userId = status == "active" ? user?.Id : null }), ct);
        return new AdminAppointmentView(id, normalized, status, status == "active" ? user?.Id : null, DateTimeOffset.UtcNow);
    }
}
