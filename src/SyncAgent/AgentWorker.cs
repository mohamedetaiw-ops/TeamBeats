using System.DirectoryServices.Protocols;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.SyncAgent;

/// <summary>Agent settings. The service account and its password stay here and are never sent to TeamBeats.</summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    public string StateFolder { get; set; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TeamBeats", "SyncAgent")
        : "/var/lib/teambeats-agent";

    /// <summary>The service account, for example <c>svc-teambeats@corp.example</c>. Read-only rights are enough.</summary>
    public string? LdapUser { get; set; }

    /// <summary>Its password, from the environment or a secret store on this machine. Never logged.</summary>
    public string? LdapPassword { get; set; }

    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan HeartbeatEvery { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The agent's loop: a heartbeat every minute, a pull every 30 seconds, and for each job a directory read over LDAPS
/// whose records go back in batches of 1,000 before the job is completed. Failures are reported as plain error classes;
/// directory content never goes to the log.
/// </summary>
public sealed class AgentWorker(AgentState state, AgentClient client, JobRunner runner, IOptions<AgentOptions> options, TimeProvider time,
    ILogger<AgentWorker> logger) : BackgroundService
{
    public static readonly string Version = typeof(AgentWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!state.IsEnrolled)
        {
            logger.LogError("The agent is not enrolled. Run it once with: enrol --server <tenant address> --code <code> --name <name>");
            return;
        }
        var agent = state.Load();
        var lastHeartbeat = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = options.Value.PollEvery;
            try
            {
                if (time.GetUtcNow() - lastHeartbeat >= options.Value.HeartbeatEvery)
                {
                    await client.HeartbeatAsync(agent, Version, stoppingToken);
                    lastHeartbeat = time.GetUtcNow();
                }
                if (await client.PullAsync(agent, stoppingToken) is { } job)
                {
                    await runner.RunAsync(agent, job, stoppingToken);
                    wait = TimeSpan.Zero;
                }
            }
            catch (AgentRefusedException ex) when (ex.Status == HttpStatusCode.Unauthorized)
            {
                logger.LogError("TeamBeats refused the agent. It may have been revoked, or this machine's clock is more than 5 minutes off.");
                wait = TimeSpan.FromMinutes(5);
            }
            catch (Exception ex) when (ex is HttpRequestException or AgentRefusedException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("TeamBeats could not be reached: {Reason}", ex.GetType().Name);
            }
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, stoppingToken);
            }
        }
    }
}

/// <summary>Runs one job: reads the directory and returns the records, then completes it.</summary>
public sealed class JobRunner(AgentClient client, ILdapDirectoryFactory directories, IOptions<AgentOptions> options, ILogger<JobRunner> logger)
{
    public const int BatchSize = 1000;

    public async Task RunAsync(AgentIdentity agent, AgentJobView job, CancellationToken ct)
    {
        if (job.Kind != "directory_sync")
        {
            await client.CompleteAsync(agent, job.Id, new AgentJobResult(false, "unknown_action"), ct);
            return;
        }
        DirectoryReadResult result;
        try
        {
            var target = Target(job.Settings);
            using var directory = directories.Open(target);
            result = DirectoryRead.Run(directory, target, job.UsersCursor, job.GroupsCursor);
        }
        catch (Exception ex) when (ErrorClass(ex) is { } errorClass)
        {
            logger.LogWarning("The directory read failed: {ErrorClass}", errorClass);
            await client.CompleteAsync(agent, job.Id, new AgentJobResult(false, errorClass), ct);
            return;
        }

        for (var i = 0; i < Math.Max(result.Users.Count, result.Groups.Count); i += BatchSize)
        {
            await client.RecordsAsync(agent, job.Id,
                new AgentRecordBatch(result.Users.Skip(i).Take(BatchSize).ToList(), result.Groups.Skip(i).Take(BatchSize).ToList()), ct);
        }
        await client.CompleteAsync(agent, job.Id, new AgentJobResult(true, null, result.UsersCursor, result.GroupsCursor), ct);
        logger.LogInformation("Directory read sent: {Users} users and {Groups} groups", result.Users.Count, result.Groups.Count);
    }

    private LdapTarget Target(JsonElement settings)
    {
        string? Text(string name) => settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var host = Text("host") ?? throw new ArgumentException("settings");
        var port = settings.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : 636;
        var o = options.Value;
        if (string.IsNullOrEmpty(o.LdapUser) || string.IsNullOrEmpty(o.LdapPassword))
        {
            throw new InvalidOperationException("credential_missing");
        }
        return new LdapTarget(host, port, Text("baseDn") ?? throw new ArgumentException("settings"), Text("userFilter"), Text("groupFilter"),
            new NetworkCredential(o.LdapUser, o.LdapPassword));
    }

    /// <summary>The plain error class of a failed read (spec 017 section 10), or null for anything unexpected.</summary>
    public static string? ErrorClass(Exception ex) => ex switch
    {
        InvalidOperationException { Message: "credential_missing" } => "credential_missing",
        LdapException { ErrorCode: 49 } => "credential_refused",
        LdapException { ErrorCode: 81 or 85 or 91 } => "not_responding",
        LdapException or DirectoryOperationException => "server_error",
        ArgumentException => "invalid_response",
        _ => "unexpected",
    };
}
