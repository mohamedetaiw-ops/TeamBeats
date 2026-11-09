using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TeamBeats.Modules.Identity.DevAuth;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Staging demo upkeep (docs/testing/fake-systems.md): the fakes live in the development host and start again on every
/// redeploy, and the test admin's Outlook is connected at sign-in, so demos keep their mail and calendar.
/// </summary>
public sealed class FakeRestartTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task A_mailbox_token_issued_before_a_restart_still_reads_the_mailbox()
    {
        var directory = Guid.NewGuid();
        var upn = "admin@restart.example";
        var token = $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"tid\":\"{directory}\",\"oid\":\"x\",\"scp\":\"Mail.ReadBasic\",\"upn\":\"{upn}\"}}")}.sig-{directory}~x~Mail.ReadBasic";
        // A new instance stands for the restarted host: it never issued this token, but the mailbox is in its scenario.
        var restarted = new FakeMicrosoftGraph();
        restarted.MailboxFor(upn).UnreadCount = 3;
        using var client = new HttpClient(restarted);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/mailFolders/inbox");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"unreadItemCount\":3", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<(int Users, string DeltaLink)> DeltaAsync(FakeMicrosoftGraph graph, Guid directory, string url)
    {
        using var client = new HttpClient(graph);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"{B64("{\"alg\":\"none\"}")}.{B64("{}")}.sig-{directory:D}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("value").GetArrayLength(), doc.RootElement.GetProperty("@odata.deltaLink").GetString()!);
    }

    [Fact]
    public async Task A_delta_link_from_before_a_restart_reads_the_whole_directory_again()
    {
        // The fake's versions start again when the host restarts, so an old delta link would miss scenario edits.
        var directory = Guid.NewGuid();
        FakeMicrosoftGraph Start()
        {
            var graph = new FakeMicrosoftGraph();
            graph.Consent(directory, ["User.Read.All"]);
            graph.Data(directory).AddUser(Guid.NewGuid().ToString("D"), "Alice Hassan", "alice@restart.example");
            return graph;
        }
        var before = Start();
        var (first, link) = await DeltaAsync(before, directory, "https://graph.microsoft.com/v1.0/users/delta");
        Assert.Equal(1, first);
        Assert.Equal(0, (await DeltaAsync(before, directory, link)).Users);

        var after = Start();
        Assert.Equal(1, (await DeltaAsync(after, directory, link)).Users);
    }

    [Theory]
    [InlineData("/api/v1/me/outlook/connect?silent=true&returnTo=%2F", true)]
    [InlineData("/", true)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("/x?u=https://evil.example", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_page_after_test_sign_in_is_only_a_local_path(string? next, bool kept)
    {
        Assert.Equal(kept ? next : null, DevAuthModule.LocalPath(next));
    }
}
