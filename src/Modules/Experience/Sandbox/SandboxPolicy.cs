namespace TeamBeats.Modules.Experience.Sandbox;

/// <summary>
/// The response policy of a tenant component's document (spec 032 FR-031, ADR-0012 point 6). <c>sandbox allow-scripts</c>
/// gives the document an opaque origin even when it is opened on its own, so it has no cookies, session, storage or
/// access to the page or the tenant's API. Script and style must be inline; requests and images go only to the declared
/// HTTPS hosts; only this tenant's pages may frame it; nothing else loads.
/// </summary>
public static class SandboxPolicy
{
    public static string For(IReadOnlyList<string> hosts)
    {
        var declared = hosts.Count == 0 ? "'none'" : string.Join(' ', hosts);
        var images = hosts.Count == 0 ? "data:" : "data: " + declared;
        return string.Join("; ",
            "sandbox allow-scripts",
            "default-src 'none'",
            "script-src 'unsafe-inline'",
            "style-src 'unsafe-inline'",
            $"img-src {images}",
            $"connect-src {declared}",
            "font-src 'none'",
            "form-action 'none'",
            "base-uri 'none'",
            "frame-ancestors 'self'");
    }
}
