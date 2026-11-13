using System.Collections.Concurrent;
using System.Net.Sockets;
using TeamBeats.Modules.Integrations.Siem;

namespace TeamBeats.Testing;

/// <summary>
/// Stands in for syslog over TLS receivers in tests: records each message per host, or fails like a receiver that is
/// down when the host is marked down. The TLS sender itself is tested in <c>TlsSyslogSenderTests</c>.
/// </summary>
public sealed class FakeSyslog : ISyslogSender
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> received = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> down = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ReceivedBy(string host) => received.TryGetValue(host, out var q) ? q.ToList() : [];

    public void SetDown(string host, bool isDown) => down[host] = isDown;

    public Task SendAsync(string host, int port, IReadOnlyList<string> messages, CancellationToken ct)
    {
        if (down.GetValueOrDefault(host))
        {
            throw new SocketException((int)SocketError.ConnectionRefused);
        }
        var queue = received.GetOrAdd(host, _ => new ConcurrentQueue<string>());
        foreach (var m in messages)
        {
            queue.Enqueue(m);
        }
        return Task.CompletedTask;
    }
}
