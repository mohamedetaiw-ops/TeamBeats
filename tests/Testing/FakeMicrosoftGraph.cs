using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TeamBeats.Testing;

/// <summary>
/// In-memory Microsoft identity platform and Graph for the TeamBeats application: client credentials tokens for
/// directories that granted admin consent, and object counts. All directories and numbers are fictional.
/// </summary>
public sealed class FakeMicrosoftGraph : HttpMessageHandler
{
    public const string ClientId = "0b1e2f3a-4c5d-4e6f-8a9b-0c1d2e3f4a5b";
    public const string ClientSecret = "fictional-platform-app-secret";

    private readonly ConcurrentDictionary<string, Directory> _directories = new(StringComparer.Ordinal);

    public bool Unavailable { get; set; }

    public sealed record Directory(IReadOnlyList<string> Roles, long Users, long Groups);

    /// <summary>The customer's admin granted these application permissions to the TeamBeats application.</summary>
    public void Consent(Guid directoryId, IReadOnlyList<string> roles, long users = 3400, long groups = 120) =>
        _directories[directoryId.ToString("D")] = new Directory(roles, users, groups);

    public void Revoke(Guid directoryId) => _directories.TryRemove(directoryId.ToString("D"), out _);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        var uri = request.RequestUri!;
        if (uri.Host == "login.microsoftonline.com" && uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
        {
            var directory = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            var form = FakeIdentityProvider.ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken));
            if (form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret
                || form.GetValueOrDefault("grant_type") != "client_credentials" || !_directories.TryGetValue(directory, out var d))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_client\"}") };
            }
            var payload = Base64Url(JsonSerializer.Serialize(new { tid = directory, roles = d.Roles }));
            var token = $"{Base64Url("{\"alg\":\"none\"}")}.{payload}.sig-{directory}";
            return Json(new { access_token = token, token_type = "Bearer", expires_in = 3600 });
        }
        if (uri.Host == "graph.microsoft.com" && uri.AbsolutePath.EndsWith("/$count", StringComparison.Ordinal))
        {
            var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
            var directory = bearer.Split(".sig-").ElementAtOrDefault(1) ?? string.Empty;
            if (!_directories.TryGetValue(directory, out var d))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            var count = uri.AbsolutePath.Contains("/users/", StringComparison.Ordinal) ? d.Users : d.Groups;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(count.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
