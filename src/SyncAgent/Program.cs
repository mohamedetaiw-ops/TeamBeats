using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TeamBeats.SyncAgent;

// TeamBeats on-premises sync agent (spec 017 US6).
//   enrol --server https://acme.teambeats.example --code XXXXX-XXXXX-XXXXX-XXXXX --name AGENT-01
//   run   (the default; as a Windows service, a systemd service or a container)
// Settings come from appsettings.json next to the program and from TEAMBEATS_AGENT_ environment variables, for example
// TEAMBEATS_AGENT_Agent__LdapUser and TEAMBEATS_AGENT_Agent__LdapPassword.
var builder = Host.CreateApplicationBuilder(args.SkipWhile(a => a is "enrol" or "run").ToArray());
builder.Configuration.AddEnvironmentVariables("TEAMBEATS_AGENT_");
builder.Services.AddWindowsService(o => o.ServiceName = "TeamBeats Sync Agent");
builder.Services.AddSystemd();
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new AgentState(sp.GetRequiredService<IOptions<AgentOptions>>().Value.StateFolder));
builder.Services.AddHttpClient<AgentClient>(c => c.Timeout = TimeSpan.FromSeconds(100));
builder.Services.AddSingleton<ILdapDirectoryFactory, LdapDirectoryFactory>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService<AgentWorker>();
using var host = builder.Build();

if (args.FirstOrDefault() == "enrol")
{
    var server = builder.Configuration["server"];
    var code = builder.Configuration["code"];
    var name = builder.Configuration["name"] ?? Environment.MachineName;
    if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(code))
    {
        Console.Error.WriteLine("Usage: enrol --server https://<tenant address> --code <one-time code> [--name <agent name>]");
        return 2;
    }
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var client = host.Services.GetRequiredService<AgentClient>();
    try
    {
        var enrolled = await client.EnrolAsync(uri, code.Trim(), name, key, AgentWorker.Version, CancellationToken.None);
        host.Services.GetRequiredService<AgentState>().Save(enrolled.AgentId, enrolled.ConnectionId, uri, key);
        Console.WriteLine($"Enrolled as agent {enrolled.AgentId:D}. Start the service to begin.");
        return 0;
    }
    catch (AgentRefusedException ex)
    {
        Console.Error.WriteLine($"Enrolment was refused ({(int)ex.Status}). Ask the administrator for a new code; codes work once and expire after 24 hours.");
        return 1;
    }
}

await host.RunAsync();
return 0;
