using System.Collections.Concurrent;
using TeamBeats.Modules.Integrations.ClockData;
using TeamBeats.Modules.Integrations.Runtime;

namespace TeamBeats.Testing;

/// <summary>
/// SFTP servers for clock data tests, by host: the pinned host key, the credential, and the files in each folder. A
/// server that is down answers "not responding". Every session is recorded with the credential it was given.
/// </summary>
public sealed class FakeSftp : IClockFileSource
{
    public sealed class Server
    {
        public string HostKeySha256 { get; set; } = "";
        public string Username { get; set; } = "";
        public string Secret { get; set; } = "";
        public bool Down { get; set; }
        public ConcurrentDictionary<string, ConcurrentDictionary<string, (byte[] Content, DateTimeOffset Modified)>> Folders { get; } = new();

        public void Put(string folder, string name, string content, DateTimeOffset modified) =>
            Folders.GetOrAdd(folder, _ => new())[name] = (System.Text.Encoding.UTF8.GetBytes(content), modified);
    }

    private readonly ConcurrentDictionary<string, Server> _servers = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<(string Host, string Username, string Secret)> Sessions { get; } = new();

    public Server On(string host, Server server) => _servers[host] = server;

    public Task<SftpFetch> FetchAsync(SftpTarget target, Func<RemoteFile, bool> wanted, int maxFiles, long maxBytes, CancellationToken ct)
    {
        if (!_servers.TryGetValue(target.Host, out var s) || s.Down)
        {
            return Task.FromResult(SftpFetch.Failed(ConnectorErrorClasses.NotResponding));
        }
        if (s.HostKeySha256 != target.HostKeySha256)
        {
            return Task.FromResult(SftpFetch.Failed(ConnectorErrorClasses.HostKeyMismatch));
        }
        Sessions.Enqueue((target.Host, target.Username, target.Secret));
        if (s.Username != target.Username || s.Secret != target.Secret)
        {
            return Task.FromResult(SftpFetch.Failed(ConnectorErrorClasses.CredentialRefused));
        }
        if (!s.Folders.TryGetValue(target.Directory, out var folder))
        {
            return Task.FromResult(SftpFetch.Failed(ConnectorErrorClasses.RequestRefused));
        }
        var listed = folder.Select(f => new RemoteFile(f.Key, f.Value.Content.Length, f.Value.Modified)).ToList();
        var files = listed.Where(wanted).OrderBy(f => f.Modified).ThenBy(f => f.Name, StringComparer.Ordinal).Take(maxFiles)
            .Select(f => new FetchedFile(f, f.Size > maxBytes ? [] : folder[f.Name].Content)).ToList();
        return Task.FromResult(new SftpFetch(listed, files, null));
    }
}
