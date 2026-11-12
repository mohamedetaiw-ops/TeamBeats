using System.Security.Cryptography;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Kiosk;

/// <summary>A kiosk as its administrators see it (spec 030 US4). Status is waiting (code not used yet), enrolled or revoked.</summary>
public sealed record KioskDeviceView(Guid Id, string Name, Guid OrgUnitId, string? Location, IReadOnlyList<string> Features, int IdleMinutes,
    bool AllowOtherUnits, string Status, DateTimeOffset? EnrolExpiresAt, DateTimeOffset? EnrolledAt, DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt,
    int Version);

public sealed record KioskDeviceRequest(string? Name, Guid? OrgUnitId, string? Location, IReadOnlyList<string>? Features, int? IdleMinutes,
    bool? AllowOtherUnits, int? Version);

/// <summary>A new kiosk or a new enrolment code: the code is shown once and works once, before it expires.</summary>
public sealed record KioskEnrolment(KioskDeviceView Device, string Code, DateTimeOffset ExpiresAt);

public sealed record KioskEnrolRequest(string? Code);

/// <summary>What the tablet itself may know about its enrolment: nothing about the people who use it.</summary>
public sealed record KioskDeviceInfo(bool Enrolled, string? Name, string? Location, int IdleMinutes);

public sealed record KioskSignInRequest(string? Badge, string? Pin, bool SetPin = false);

public sealed record BadgeView(Guid UserId, bool Issued, bool PinSet, DateTimeOffset? IssuedAt, DateTimeOffset? LockedUntil);

/// <summary>A badge just issued: the code and its QR image, shown this once for printing.</summary>
public sealed record BadgeIssued(Guid UserId, string Code, string QrSvg);

public sealed record KioskPinRequest(string? Pin);

public static class KioskStatuses
{
    public const string Waiting = "waiting";
    public const string Enrolled = "enrolled";
    public const string Revoked = "revoked";
}

/// <summary>Defaults and limits of spec 030 section 6.</summary>
public static class KioskRules
{
    public const string DeviceCookie = "tb_kiosk";
    public const string Method = "kiosk";
    public const int DefaultIdleMinutes = 2;
    public const int MinIdleMinutes = 1;
    public const int MaxIdleMinutes = 15;
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan EnrolCodeLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DeviceCookieLifetime = TimeSpan.FromDays(400);

    /// <summary>A kiosk session's use is recorded this often, so a short idle timeout is measured closely.</summary>
    public static readonly TimeSpan TouchEvery = TimeSpan.FromSeconds(15);

    public const string Revoked = "kiosk_revoked";
    public const string WrongDevice = "kiosk_wrong_device";
    public const string NextPerson = "kiosk_next_person";

    public static bool ValidPin(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    /// <summary>Why a kiosk session has ended, or null while it may go on.</summary>
    public static string? Ended(bool deviceActive, bool sameDevice, int idleMinutes, DateTimeOffset lastSeen, DateTimeOffset now) =>
        !deviceActive ? Revoked
        : !sameDevice ? WrongDevice
        : now - lastSeen > TimeSpan.FromMinutes(idleMinutes) ? Identity.SessionRules.IdleTimeout
        : null;
}

/// <summary>
/// Codes for kiosks and badges. Both are random and stored only as hashes. They use letters and digits that cannot be
/// misread (Crockford base 32), so they can be typed from a printed label or sent by a badge scanner that types.
/// </summary>
public static class KioskCodes
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A badge code: 20 characters (100 bits), so it cannot be guessed. It never holds the employee number.</summary>
    public static string NewBadgeCode() => "TB" + Random(20);

    /// <summary>An enrolment code: 12 characters in three groups, valid for 30 minutes and once.</summary>
    public static string NewEnrolCode()
    {
        var raw = Random(12);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
    }

    /// <summary>Upper case without spaces or dashes, with the letters people confuse mapped as Crockford base 32 does.</summary>
    public static string Normalize(string? code) =>
        new((code ?? "").Trim().ToUpperInvariant().Where(c => c is not ('-' or ' ')).Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray());

    public static string Hash(string code) => SessionTokens.Hash("kiosk:" + Normalize(code));

    private static string Random(int length) => new(RandomNumberGenerator.GetBytes(length).Select(b => Alphabet[b & 31]).ToArray());
}

/// <summary>PINs are kept as salted PBKDF2 hashes; the comparison takes the same time whatever the input.</summary>
public static class KioskPins
{
    private const int Iterations = 100_000;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

internal static class KioskValidation
{
    public static Dictionary<string, string[]> Check(KioskDeviceRequest request, bool create)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim();
        if ((create || request.Name is not null) && string.IsNullOrEmpty(name))
        {
            errors["name"] = ["validation.required"];
        }
        else if (name is { Length: > 100 })
        {
            errors["name"] = ["validation.kiosk.name_length"];
        }
        if (create && request.OrgUnitId is null)
        {
            errors["orgUnitId"] = ["validation.required"];
        }
        if (request.Location?.Trim() is { Length: > 100 })
        {
            errors["location"] = ["validation.kiosk.location_length"];
        }
        if (request.IdleMinutes is { } idle && idle is < KioskRules.MinIdleMinutes or > KioskRules.MaxIdleMinutes)
        {
            errors["idleMinutes"] = ["validation.kiosk.idle_range"];
        }
        if (request.Features is { } features && features.Any(f => !SharedDeviceRules.Features.Contains(f)))
        {
            errors["features"] = ["validation.kiosk.feature_unknown"];
        }
        return errors;
    }

    public static void Throw(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw AppException.Validation(errors);
        }
    }
}
