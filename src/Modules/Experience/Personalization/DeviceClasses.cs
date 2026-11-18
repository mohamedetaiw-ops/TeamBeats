using Microsoft.AspNetCore.Http;

namespace TeamBeats.Modules.Experience.Personalization;

/// <summary>
/// Device classes for device-specific choices (spec 008 FR-032, FR-036). The web app keeps the class of the screen in
/// the <c>tb_device</c> cookie; anything else is no device, so cross-device choices apply. A device class is never
/// trusted for access: it only picks which of the person's own choices to show.
/// </summary>
public static class DeviceClasses
{
    public const string Any = "any";
    public const string Desktop = "desktop";
    public const string Tablet = "tablet";
    public const string Phone = "phone";
    public const string Cookie = "tb_device";

    public static readonly IReadOnlyList<string> All = [Desktop, Tablet, Phone];

    public static string? From(HttpRequest request) =>
        request.Cookies.TryGetValue(Cookie, out var value) && All.Contains(value) ? value : null;
}
