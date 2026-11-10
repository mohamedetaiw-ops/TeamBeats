using System.Security.Cryptography;
using System.Text.Json;

namespace TeamBeats.SyncAgent;

/// <summary>
/// What the agent keeps on disk in its state folder: its ID and tenant host (<c>agent.json</c>), its private key
/// (<c>agent.key</c>, PKCS#8 PEM, readable by the service account only on Linux). The folder belongs to the service
/// account; on Windows the installer puts it under ProgramData with an access list for that account only.
/// </summary>
public sealed class AgentState(string folder)
{
    private sealed record Saved(Guid AgentId, string Server, Guid ConnectionId);

    public string Folder { get; } = folder;

    private string IdentityPath => Path.Combine(Folder, "agent.json");
    private string KeyPath => Path.Combine(Folder, "agent.key");

    public bool IsEnrolled => File.Exists(IdentityPath) && File.Exists(KeyPath);

    public void Save(Guid agentId, Guid connectionId, Uri server, ECDsa key)
    {
        Directory.CreateDirectory(Folder);
        WritePrivate(KeyPath, key.ExportPkcs8PrivateKeyPem());
        WritePrivate(IdentityPath, JsonSerializer.Serialize(new Saved(agentId, server.ToString(), connectionId), AgentClient.Json));
    }

    public AgentIdentity Load()
    {
        var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(IdentityPath), AgentClient.Json)
            ?? throw new InvalidOperationException("The agent state is unreadable. Enrol the agent again.");
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(KeyPath));
        return new AgentIdentity(saved.AgentId, new Uri(saved.Server), key);
    }

    private static void WritePrivate(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temp, path, true);
    }
}
