using System.Text.RegularExpressions;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy;

/// <summary>Business and validation rules from spec 001 section 6.</summary>
public static partial class TenantRules
{
    public static readonly IReadOnlySet<string> ReservedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "admin", "api", "www", "platform", "app", "auth", "login", "signin", "static", "assets", "cdn", "mail", "smtp",
        "status", "help", "support", "docs", "dev", "test", "staging", "internal", "system", "root", "teambeats",
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    /// <summary>Keys are case-insensitive: they are normalized to lowercase before validation and storage.</summary>
    public static string NormalizeKey(string? key) => (key ?? string.Empty).Trim().ToLowerInvariant();

    public static void ValidateKey(string key, ValidationErrors errors)
    {
        if (!KeyPattern().IsMatch(key))
        {
            errors.Add("key", "validation.tenant.key_format");
        }
        else if (ReservedKeys.Contains(key))
        {
            errors.Add("key", "validation.tenant.key_reserved");
        }
    }

    public static void ValidateLocales(string? defaultLocale, IReadOnlyCollection<string>? enabled, ValidationErrors errors)
    {
        if (enabled is null || enabled.Count == 0)
        {
            errors.Add("enabledLocales", "validation.locale.at_least_one");
            return;
        }
        if (enabled.Any(l => !Locales.IsSupported(l)))
        {
            errors.Add("enabledLocales", "validation.locale.unsupported");
        }
        if (enabled.Distinct().Count() != enabled.Count)
        {
            errors.Add("enabledLocales", "validation.locale.duplicate");
        }
        if (defaultLocale is null || !enabled.Contains(defaultLocale))
        {
            errors.Add("defaultLocale", "validation.locale.default_must_be_enabled");
        }
    }

    public static void ValidateTimeZone(string? timeZone, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            errors.Add("timeZone", "validation.tenant.time_zone");
        }
    }

    public static void ValidateColor(string? color, ValidationErrors errors)
    {
        if (color is not null && !ColorPattern().IsMatch(color))
        {
            errors.Add("primaryColor", "validation.tenant.color");
        }
    }

    public static bool CanTransition(TenantLifecycleStatus from, TenantLifecycleStatus to) => (from, to) switch
    {
        (TenantLifecycleStatus.Active, TenantLifecycleStatus.Suspended) => true,
        (TenantLifecycleStatus.Suspended, TenantLifecycleStatus.Active) => true,
        (TenantLifecycleStatus.Active, TenantLifecycleStatus.Closed) => true,
        (TenantLifecycleStatus.Suspended, TenantLifecycleStatus.Closed) => true,
        (TenantLifecycleStatus.Provisioning, TenantLifecycleStatus.Active) => true,
        _ => false,
    };

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static bool LooksLikeEmail(string? email) =>
        email is { Length: > 3 and <= 254 } && email.IndexOf('@') is > 0 and var at && at < email.Length - 1 && !email.Contains(' ');
}
