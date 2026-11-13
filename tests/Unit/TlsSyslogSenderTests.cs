using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Integrations.Siem;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 026 US4: syslog over TLS sends RFC 5425 octet counted frames over TLS 1.2 or later, refuses private addresses
/// unless the installation allows them, and refuses a server whose certificate is not trusted.
/// </summary>
public sealed class TlsSyslogSenderTests
{
    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    /// <summary>Accepts one TLS connection and reads it to the end.</summary>
    private static async Task<string> ReceiveAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsServerAsync(certificate);
        using var buffer = new MemoryStream();
        await tls.CopyToAsync(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Fact]
    public async Task Messages_go_out_as_octet_counted_frames_over_tls()
    {
        using var certificate = Certificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var received = ReceiveAsync(listener, certificate);
            var sender = new TlsSyslogSender(allowPrivate: true, (_, cert, _, _) => cert?.GetCertHashString() == certificate.GetCertHashString());
            string[] messages = ["<110>1 2026-11-13T10:00:00Z teambeats teambeats - auth.sign_in_succeeded - {\"a\":\"ü\"}", "<108>1 - teambeats teambeats - x - {}"];
            await sender.SendAsync("localhost", port, messages, CancellationToken.None);
            var text = await received.WaitAsync(TimeSpan.FromSeconds(10));
            var expected = string.Concat(messages.Select(m => $"{Encoding.UTF8.GetByteCount(m)} {m}"));
            Assert.Equal(expected, text);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_private_address_or_an_untrusted_certificate_is_refused()
    {
        var guarded = new TlsSyslogSender(allowPrivate: false, null);
        await Assert.ThrowsAsync<BlockedAddressException>(() => guarded.SendAsync("127.0.0.1", 6514, ["m"], CancellationToken.None));

        using var certificate = Certificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = ReceiveAsync(listener, certificate);
            var strict = new TlsSyslogSender(allowPrivate: true, null);
            await Assert.ThrowsAnyAsync<System.Security.Authentication.AuthenticationException>(() => strict.SendAsync("localhost", port, ["m"], CancellationToken.None));
            // With TLS 1.3 the server can finish its side before the client refuses the certificate, so the server
            // either fails or reads nothing; it never receives a message.
            try
            {
                Assert.Equal(string.Empty, await server.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or SocketException)
            {
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void A_message_has_the_audit_facility_the_severity_of_its_outcome_and_the_event_as_json()
    {
        var denied = JsonSerializer.SerializeToElement(new { time = "2026-11-13T10:00:00Z", action = "authz.denied with space", outcome = "denied" });
        Assert.Equal("<108>1 2026-11-13T10:00:00Z teambeats teambeats - authz.deniedwithspace - " + denied.GetRawText(), SiemPack.SyslogMessage(denied));
        var ok = JsonSerializer.SerializeToElement(new { time = "2026-11-13T10:00:00Z", action = new string('a', 40), outcome = "success" });
        Assert.StartsWith("<110>1 2026-11-13T10:00:00Z teambeats teambeats - " + new string('a', 32) + " - {", SiemPack.SyslogMessage(ok), StringComparison.Ordinal);
    }
}
