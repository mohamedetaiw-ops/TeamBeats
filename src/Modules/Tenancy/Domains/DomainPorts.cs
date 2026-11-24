using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Tenancy.Domains;

/// <summary>TXT records of one DNS name; null when the resolver could not answer, an empty list when there are none.</summary>
public interface IDnsTxtLookup
{
    Task<IReadOnlyList<string>?> TxtAsync(string name, CancellationToken ct);
}

/// <summary>When the certificate a host presents ends; null when no valid certificate could be read.</summary>
public interface ICertificateProbe
{
    Task<DateTimeOffset?> ExpiresAsync(string host, CancellationToken ct);
}

/// <summary>
/// TXT lookups through a DNS over HTTPS resolver in the JSON format, so no DNS library is needed (constitution: no new
/// dependency without need). The resolver is configured in <see cref="CustomDomainOptions.Resolver"/>.
/// </summary>
public sealed class DohTxtLookup(IHttpClientFactory http, IOptions<CustomDomainOptions> options, ILogger<DohTxtLookup> logger) : IDnsTxtLookup
{
    public const string ClientName = "tenancy.dns";

    private sealed record Answer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string? Data);

    private sealed record Response([property: JsonPropertyName("Status")] int Status, [property: JsonPropertyName("Answer")] List<Answer>? Answer);

    public async Task<IReadOnlyList<string>?> TxtAsync(string name, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Value.Resolver}?name={Uri.EscapeDataString(name)}&type=TXT");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await http.CreateClient(ClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var body = await response.Content.ReadFromJsonAsync<Response>(ct);
            // NOERROR (0) or NXDOMAIN (3) are answers; anything else means the resolver could not tell.
            if (body is null || body.Status is not (0 or 3))
            {
                return null;
            }
            return (body.Answer ?? []).Where(a => a.Type == 16 && a.Data is not null).Select(a => Unquote(a.Data!)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "TXT lookup failed for {Name}", name);
            return null;
        }
    }

    /// <summary>A TXT value arrives as one or more quoted strings, which together are the value.</summary>
    public static string Unquote(string data)
    {
        var parts = new List<string>();
        var text = data.Trim();
        if (!text.StartsWith('"'))
        {
            return text;
        }
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf('"', i);
            if (start < 0)
            {
                break;
            }
            var end = start + 1;
            var part = new System.Text.StringBuilder();
            while (end < text.Length && text[end] != '"')
            {
                if (text[end] == '\\' && end + 1 < text.Length)
                {
                    end++;
                }
                part.Append(text[end]);
                end++;
            }
            parts.Add(part.ToString());
            i = end + 1;
        }
        return string.Concat(parts);
    }
}

/// <summary>Reads the certificate a host presents on port 443, accepting it only when the chain and name are valid.</summary>
public sealed class TlsCertificateProbe(ILogger<TlsCertificateProbe> logger) : ICertificateProbe
{
    public async Task<DateTimeOffset?> ExpiresAsync(string host, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, 443, timeout.Token);
            X509Certificate2? seen = null;
            await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, errors) =>
            {
                if (certificate is not null)
                {
                    seen = new X509Certificate2(certificate);
                }
                return errors == SslPolicyErrors.None;
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
            return seen is null ? null : new DateTimeOffset(seen.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            logger.LogInformation(ex, "Certificate probe failed for {Host}", host);
            return null;
        }
    }
}

/// <summary>
/// Host to tenant for verified custom domains, read from <c>tenancy.domain_route</c> before a tenant is known. Answers are
/// kept for 30 seconds per process (unknown hosts too, so a flood of strange hosts does not reach the database), and a
/// route change in this process forgets the host at once.
/// </summary>
public sealed class CustomDomainResolver(IServiceScopeFactory scopes, IClock clock) : ICustomDomainResolver
{
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (Guid? Tenant, DateTimeOffset At)> known = new(StringComparer.Ordinal);

    public async Task<Guid?> TenantForHostAsync(string host, CancellationToken ct = default)
    {
        var key = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (known.TryGetValue(key, out var hit) && clock.UtcNow - hit.At < Keep)
        {
            return hit.Tenant;
        }
        await using var scope = scopes.CreateAsyncScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IDbSession>()
            .ScalarAsync<Guid?>("select tenant_id from tenancy.domain_route where host = @host", new { host = key }, ct);
        if (known.Count > 10_000)
        {
            known.Clear();
        }
        known[key] = (tenant, clock.UtcNow);
        return tenant;
    }

    public void Forget(string host) => known.TryRemove(host.Trim().TrimEnd('.').ToLowerInvariant(), out _);
}
