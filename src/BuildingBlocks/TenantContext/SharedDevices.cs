namespace TeamBeats.TenantContext;

/// <summary>A session opened on an enrolled kiosk device (spec 030 US4): the device and the features its administrator allowed.</summary>
public sealed record SharedDeviceSession(Guid DeviceId, IReadOnlySet<string> Features, int IdleMinutes);

/// <summary>
/// What a session on a shared kiosk device may reach (spec 030 FR-017). It is an allow list: an API route that is not
/// listed here is refused on a kiosk, whatever the person's roles, so a new feature stays off shared devices until it
/// is added on purpose. Payslips, mail, documents, notifications, push and offline reading are never listed.
/// Routes are the endpoint's route template, so a page reached by typing its address is refused the same way.
/// </summary>
public static class SharedDeviceRules
{
    public const string Announcements = "announcements";
    public const string Policies = "policies";
    public const string Leave = "leave";
    public const string Shifts = "shifts";
    public const string Services = "services";
    public const string Booking = "booking";

    /// <summary>The features an administrator can allow on a kiosk, in display order. Home and sign-out are always there.</summary>
    public static readonly IReadOnlyList<string> Features = [Announcements, Policies, Leave, Shifts, Services, Booking];

    private const string Core = "core";

    /// <summary>Feature, HTTP method and route template of every route a kiosk session may call.</summary>
    public static readonly IReadOnlyList<(string Feature, string Method, string Route)> Routes =
    [
        (Core, "GET", "/api/v1/me/"),
        (Core, "PUT", "/api/v1/me/preferences"),
        (Core, "POST", "/api/v1/auth/signout"),
        (Core, "GET", "/api/v1/kiosk/device"),
        (Core, "POST", "/api/v1/kiosk/signin"),
        (Core, "GET", "/api/v1/experience/shell"),
        (Core, "GET", "/api/v1/experience/theme"),
        (Core, "GET", "/api/v1/experience/theme.css"),
        (Core, "GET", "/api/v1/experience/tokens"),
        (Core, "GET", "/api/v1/experience/manifest.webmanifest"),
        (Core, "GET", "/api/v1/experience/app-icon.svg"),
        (Core, "GET", "/api/v1/experience/assets/{id:guid}"),
        (Core, "GET", "/api/v1/experience/render"),
        (Core, "GET", "/api/v1/experience/resolve"),
        (Core, "GET", "/api/v1/experience/quick-nav"),
        (Core, "POST", "/api/v1/experience/telemetry"),
        (Announcements, "GET", "/api/v1/content/feed"),
        (Announcements, "GET", "/api/v1/content/items/{id:guid}/view"),
        (Announcements, "GET", "/api/v1/content/critical/pending"),
        (Announcements, "POST", "/api/v1/content/items/{id:guid}/acknowledge"),
        (Announcements, "POST", "/api/v1/content/items/{id:guid}/remind-later"),
        (Announcements, "POST", "/api/v1/content/items/{id:guid}/dismiss"),
        (Policies, "GET", "/api/v1/me/policies"),
        (Policies, "GET", "/api/v1/policies/{id:guid}/current"),
        (Policies, "GET", "/api/v1/policies/{id:guid}/attestation"),
        (Policies, "POST", "/api/v1/policies/{id:guid}/attest"),
        (Leave, "GET", "/api/v1/me/leave"),
        (Leave, "GET", "/api/v1/hr/leave/types"),
        (Leave, "POST", "/api/v1/hr/leave/preview"),
        (Leave, "POST", "/api/v1/hr/leave/requests"),
        (Leave, "GET", "/api/v1/hr/leave/requests/{id:guid}"),
        (Leave, "POST", "/api/v1/hr/leave/requests/{id:guid}:cancel"),
        (Shifts, "GET", "/api/v1/me/attendance"),
        (Services, "GET", "/api/v1/services"),
        (Services, "GET", "/api/v1/services/{id:guid}"),
        (Services, "GET", "/api/v1/services/{id:guid}/prefill"),
        (Services, "POST", "/api/v1/services/{id:guid}/used"),
        (Services, "GET", "/api/v1/requests"),
        (Services, "POST", "/api/v1/requests"),
        (Services, "GET", "/api/v1/requests/{id:guid}"),
        (Booking, "GET", "/api/v1/booking/rights"),
        (Booking, "GET", "/api/v1/booking/bookings/mine"),
        (Booking, "POST", "/api/v1/booking/bookings/{id:guid}:check-in"),
        (Booking, "GET", "/api/v1/booking/scan/{code}"),
        (Booking, "POST", "/api/v1/booking/scan/{code}:check-in"),
        (Booking, "GET", "/api/v1/booking/sites/{id:guid}/kiosk"),
    ];

    /// <summary>The pages of each feature in the web app, for the navigation a kiosk shows.</summary>
    public static readonly IReadOnlyList<(string Feature, string Prefix)> Pages =
    [
        (Core, "/"), (Core, "/kiosk"), (Core, "/p/"),
        (Announcements, "/content/read"), (Policies, "/policies"), (Leave, "/leave"), (Shifts, "/attendance"),
        (Services, "/services"), (Services, "/requests"), (Booking, "/booking/scan"), (Booking, "/booking/kiosk"),
    ];

    private static readonly Dictionary<(string Method, string Route), string> ByRoute =
        Routes.ToDictionary(r => (r.Method, Normalize(r.Route)), r => r.Feature);

    /// <summary>Whether a kiosk session with these features may call the route.</summary>
    public static bool AllowsRoute(string method, string? route, IReadOnlySet<string> features) =>
        route is not null && ByRoute.TryGetValue((method.ToUpperInvariant(), Normalize(route)), out var feature) && (feature == Core || features.Contains(feature));

    /// <summary>Whether a kiosk session with these features shows a link to the page.</summary>
    public static bool AllowsPage(string href, IReadOnlySet<string> features)
    {
        var path = href.Split('?', '#')[0];
        return Pages.Any(p => (p.Feature == Core || features.Contains(p.Feature))
            && (p.Prefix == "/" ? path == "/" : path == p.Prefix.TrimEnd('/') || path.StartsWith(p.Prefix.TrimEnd('/') + "/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Whether an API path a component reads (its declared data source) is reachable: the same path as an allowed GET
    /// route, or, for a route with parameters, a path under its literal start.
    /// </summary>
    public static bool AllowsDataPath(string path, IReadOnlySet<string> features)
    {
        var wanted = Normalize(path.Split('?')[0]);
        return Routes.Any(r => r.Method == "GET" && (r.Feature == Core || features.Contains(r.Feature)) && (r.Route.Contains('{', StringComparison.Ordinal)
            ? wanted.StartsWith(r.Route.Split('{')[0], StringComparison.Ordinal)
            : wanted == Normalize(r.Route)));
    }

    private static string Normalize(string route) => route.Length > 1 ? route.TrimEnd('/') : route;
}
