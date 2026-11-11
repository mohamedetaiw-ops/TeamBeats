using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Renci.SshNet;
using Renci.SshNet.Common;
using TeamBeats.Modules.Integrations.Runtime;

namespace TeamBeats.Modules.Integrations.ClockData;

/// <summary>Where and how to read clock data files over SFTP. <see cref="Secret"/> is the password or private key, read from the secret manager.</summary>
public sealed record SftpTarget(string Host, int Port, string Username, string AuthType, string Secret, string HostKeySha256, string Directory);

public sealed record RemoteFile(string Name, long Size, DateTimeOffset Modified);

public sealed record FetchedFile(RemoteFile File, byte[] Content);

/// <summary>The files listed and the ones read, or the plain error class of what failed (spec 017 section 10).</summary>
public sealed record SftpFetch(IReadOnlyList<RemoteFile> Listed, IReadOnlyList<FetchedFile> Files, string? ErrorClass)
{
    public static SftpFetch Failed(string errorClass) => new([], [], errorClass);
}

/// <summary>
/// The port to an SFTP server (ADR-0013): list a directory and read the files wanted, in one session. Read only:
/// TeamBeats never writes, moves or deletes files on the customer's server.
/// </summary>
public interface IClockFileSource
{
    Task<SftpFetch> FetchAsync(SftpTarget target, Func<RemoteFile, bool> wanted, int maxFiles, long maxBytes, CancellationToken ct);
}

/// <summary>
/// SFTP with SSH.NET. The host is resolved once and every address checked as <see cref="SafeHttp"/> does for HTTP, so
/// a name that points inside the network is refused; the session connects to the checked address. The server's host
/// key must match the SHA-256 fingerprint pinned in the settings, so a server that is not the one configured is never
/// sent the credential.
/// </summary>
public sealed class SshNetClockFileSource(IConfiguration configuration) : IClockFileSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<SftpFetch> FetchAsync(SftpTarget target, Func<RemoteFile, bool> wanted, int maxFiles, long maxBytes, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(target.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(target.Host, ct);
        }
        catch (SocketException)
        {
            return SftpFetch.Failed(ConnectorErrorClasses.NotResponding);
        }
        if (addresses.Length == 0)
        {
            return SftpFetch.Failed(ConnectorErrorClasses.NotResponding);
        }
        if (!configuration.GetValue<bool>(SafeHttp.AllowPrivateSetting) && addresses.Any(a => !SafeHttp.IsPublic(a)))
        {
            return SftpFetch.Failed(ConnectorErrorClasses.BlockedAddress);
        }

        AuthenticationMethod method;
        try
        {
            method = target.AuthType == "private_key"
                ? new PrivateKeyAuthenticationMethod(target.Username, new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(target.Secret))))
                : new PasswordAuthenticationMethod(target.Username, target.Secret);
        }
        catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException)
        {
            return SftpFetch.Failed(ConnectorErrorClasses.CredentialRefused);
        }

        var info = new ConnectionInfo(addresses[0].ToString(), target.Port, target.Username, method) { Timeout = Timeout };
        using var client = new SftpClient(info) { OperationTimeout = Timeout };
        var keyMatched = true;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = string.Equals(e.FingerPrintSHA256, target.HostKeySha256, StringComparison.Ordinal);
            keyMatched = e.CanTrust;
        };
        try
        {
            await client.ConnectAsync(ct);
            var listed = new List<RemoteFile>();
            await foreach (var f in client.ListDirectoryAsync(target.Directory, ct))
            {
                if (f.IsRegularFile)
                {
                    listed.Add(new RemoteFile(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)));
                }
            }
            var files = new List<FetchedFile>();
            foreach (var f in listed.Where(wanted).OrderBy(f => f.Modified).ThenBy(f => f.Name, StringComparer.Ordinal).Take(maxFiles))
            {
                if (f.Size > maxBytes)
                {
                    files.Add(new FetchedFile(f, []));
                    continue;
                }
                using var buffer = new MemoryStream();
                await client.DownloadFileAsync(target.Directory.TrimEnd('/') + "/" + f.Name, buffer, ct);
                files.Add(new FetchedFile(f, buffer.Length > maxBytes ? [] : buffer.ToArray()));
            }
            return new SftpFetch(listed, files, null);
        }
        catch (SshConnectionException) when (!keyMatched)
        {
            return SftpFetch.Failed(ConnectorErrorClasses.HostKeyMismatch);
        }
        catch (SshAuthenticationException)
        {
            return SftpFetch.Failed(ConnectorErrorClasses.CredentialRefused);
        }
        catch (Exception ex) when (ex is SftpPathNotFoundException or SftpPermissionDeniedException)
        {
            return SftpFetch.Failed(ConnectorErrorClasses.RequestRefused);
        }
        catch (Exception ex) when (ex is SshConnectionException or SshOperationTimeoutException or SocketException or TimeoutException or ProxyException)
        {
            return SftpFetch.Failed(keyMatched ? ConnectorErrorClasses.NotResponding : ConnectorErrorClasses.HostKeyMismatch);
        }
        catch (SshException)
        {
            return SftpFetch.Failed(keyMatched ? ConnectorErrorClasses.InvalidResponse : ConnectorErrorClasses.HostKeyMismatch);
        }
    }
}
