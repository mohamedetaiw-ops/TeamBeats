using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 R2 slice 2 (T021, T022, US3): the tenant's guest domain rules decide which addresses a space owner may
/// invite; <c>security.max_guest_spaces</c> limits how many spaces have guests; a guest's PDF downloads carry the guest's
/// address and the time. Rules of one tenant never apply in another. Names and addresses are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecurityGuestTests(TenantWorld world) : IAsyncLifetime
{
    private const string Spaces = "/api/v1/spaces";
    private const string Policy = "/api/v1/security/policy";
    private DevHostFactory _dev = null!;

    public Task InitializeAsync()
    {
        _dev = new DevHostFactory(world.Database);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dev.DisposeAsync();

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return problem.GetProperty("detailKey").GetString()!;
    }

    /// <summary>Omar owns the spaces; guests are allowed in the spaces rules.</summary>
    private async Task<TestTenant> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sgd-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "omar", MembershipTypes.Employee);
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Spaces}/manage/settings", new { creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, version = 0 }));
        return t;
    }

    private static async Task<JsonElement> SpaceAsync(TestTenant t, string name) =>
        await OkAsync(t["omar"].Client.PostAsJsonAsync(Spaces, new { name, description = $"Work on {name}.", visibility = "private", template = "project" }),
            HttpStatusCode.Created);

    private static Task<HttpResponseMessage> InviteAsync(TestTenant t, Guid space, string email) =>
        t["omar"].Client.PostAsJsonAsync($"{Spaces}/{space}/guests", new { email });

    private static async Task SetDomainsAsync(TestTenant t, string[] allow, string[] block)
    {
        var current = await t["admin"].Client.GetAsync(Policy);
        var etag = current.Headers.ETag?.ToString();
        using var request = new HttpRequestMessage(HttpMethod.Put, Policy) { Content = JsonContent.Create(new { guestAllowDomains = allow, guestBlockDomains = block }) };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        await OkAsync(t["admin"].Client.SendAsync(request));
    }

    private async Task<HttpClient> SignInAsync(TestTenant t, string email)
    {
        var response = await _dev.Tenant(t.Key).PostAsJsonAsync("/dev/signin", new { email });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return _dev.Tenant(t.Key, DevHostFactory.SessionToken(response));
    }

    private static byte[] SamplePdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static string PageText(byte[] pdf)
    {
        using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var text = new StringBuilder();
        foreach (var page in document.Pages)
        {
            for (var i = 0; i < page.Contents.Elements.Count; i++)
            {
                text.Append(Encoding.ASCII.GetString(page.Contents.Elements.GetDictionary(i)!.Stream.UnfilteredValue));
            }
        }
        return text.ToString();
    }

    [Fact]
    public async Task Guest_domain_rules_decide_which_addresses_an_owner_may_invite()
    {
        var t = await SetupAsync();
        var other = await SetupAsync();
        var space = (await SpaceAsync(t, "Vendor rollout")).GetProperty("id").GetGuid();

        // The policy keeps domains in lower case without duplicates and refuses what is not a domain name.
        Assert.Equal("validation.security.domain", await ProblemKeyAsync(
            t["admin"].Client.PutAsJsonAsync(Policy, new { guestBlockDomains = new[] { "not a domain" } }), HttpStatusCode.BadRequest));
        await SetDomainsAsync(t, ["Partner.example", "partner.example"], ["@mail.example"]);
        var policy = await OkAsync(t["admin"].Client.GetAsync(Policy));
        Assert.Equal(["partner.example"], policy.GetProperty("guestAllowDomains").EnumerateArray().Select(d => d.GetString()!).ToArray());
        Assert.Equal(["mail.example"], policy.GetProperty("guestBlockDomains").EnumerateArray().Select(d => d.GetString()!).ToArray());
        var changed = await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from audit.audit_event where action = '{AuditActions.SecurityPolicyChanged}' and changes::text like '%guestAllowDomains%'");
        Assert.Equal(1, changed);

        // An allowed domain and its subdomains are invited; others are refused; blocked wins over allowed.
        await OkAsync(InviteAsync(t, space, "rania@partner.example"));
        await OkAsync(InviteAsync(t, space, "sami@eu.partner.example"));
        Assert.Equal("validation.spaces.guest_domain_not_allowed", await ProblemKeyAsync(InviteAsync(t, space, "nadia@vendor.example"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.guest_domain_not_allowed", await ProblemKeyAsync(InviteAsync(t, space, "fake@notpartner.example"), HttpStatusCode.BadRequest));
        await SetDomainsAsync(t, ["partner.example", "mail.example"], ["mail.example"]);
        Assert.Equal("validation.spaces.guest_domain_blocked", await ProblemKeyAsync(InviteAsync(t, space, "someone@mail.example"), HttpStatusCode.BadRequest));

        // With no allowed list, every domain not blocked is allowed.
        await SetDomainsAsync(t, [], ["mail.example"]);
        await OkAsync(InviteAsync(t, space, "nadia@vendor.example"));
        Assert.Equal("validation.spaces.guest_domain_blocked", await ProblemKeyAsync(InviteAsync(t, space, "x@team.mail.example"), HttpStatusCode.BadRequest));

        // Another tenant's rules are its own.
        var elsewhere = (await SpaceAsync(other, "Elsewhere")).GetProperty("id").GetGuid();
        await OkAsync(InviteAsync(other, elsewhere, "someone@mail.example"));

        // Only security administrators change the rules.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.PutAsJsonAsync(Policy, new { guestBlockDomains = Array.Empty<string>() })).StatusCode);
    }

    [Fact]
    public async Task The_package_limits_how_many_spaces_have_guests()
    {
        var t = await SetupAsync();
        var spaces = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            spaces.Add((await SpaceAsync(t, $"Project {i}")).GetProperty("id").GetGuid());
        }

        // Workplace Plus: five spaces may have guests or open invitations.
        for (var i = 0; i < 5; i++)
        {
            await OkAsync(InviteAsync(t, spaces[i], $"guest{i}@partner.example"));
        }
        Assert.Equal("validation.spaces.guest_space_limit", await ProblemKeyAsync(InviteAsync(t, spaces[5], "late@partner.example"), HttpStatusCode.Forbidden));
        // A space that already has guests takes more.
        await OkAsync(InviteAsync(t, spaces[0], "second@partner.example"));

        // Cancelling a space's only invitation frees its place.
        var invitation = (await OkAsync(t["omar"].Client.GetAsync($"{Spaces}/{spaces[4]}/guests"))).GetProperty("invitations")[0].GetProperty("id").GetGuid();
        await OkAsync(t["omar"].Client.DeleteAsync($"{Spaces}/{spaces[4]}/guests/invitations/{invitation}"));
        await OkAsync(InviteAsync(t, spaces[5], "late@partner.example"));

        // Enterprise has no limit.
        var big = await SetupAsync("enterprise_gov");
        for (var i = 0; i < 6; i++)
        {
            var id = (await SpaceAsync(big, $"Project {i}")).GetProperty("id").GetGuid();
            await OkAsync(InviteAsync(big, id, $"guest{i}@partner.example"));
        }
    }

    [Fact]
    public async Task A_guests_pdf_download_carries_their_address_and_the_time()
    {
        var t = await SetupAsync();
        var created = await SpaceAsync(t, "Audit 2027");
        var space = created.GetProperty("id").GetGuid();
        var library = created.GetProperty("libraryId").GetGuid();
        var pdf = SamplePdf();
        async Task<Guid> Upload(string name, byte[] content) =>
            (await OkAsync(t["omar"].Client.PostAsync($"/api/v1/documents/libraries/{library}/files?name={name}&publish=true&classification=internal",
                new ByteArrayContent(content)), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var report = await Upload("report.pdf", pdf);
        var notes = await Upload("notes.txt", Encoding.UTF8.GetBytes("Plain notes."));
        // Karim is already a guest of the tenant, so the invitation adds him at once.
        await world.SeedUserAsync(t, "karim", MembershipTypes.Guest);
        var address = t["karim"].Email;
        await OkAsync(InviteAsync(t, space, address));
        var karim = t["karim"].Client;

        async Task<byte[]> Download(HttpClient client, Guid id)
        {
            var token = await OkAsync(client.GetAsync($"/api/v1/documents/{id}/download-token"));
            var response = await client.GetAsync(token.GetProperty("url").GetString());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync();
        }

        // The guest's PDF is stamped with their address and today's date; other files come as they are.
        var stamped = PageText(await Download(karim, report));
        Assert.Contains(address, stamped, StringComparison.Ordinal);
        Assert.Contains(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), stamped, StringComparison.Ordinal);
        Assert.Equal("Plain notes.", Encoding.UTF8.GetString(await Download(karim, notes)));
        // Every guest download is audited.
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from audit.audit_event where action = '{AuditActions.DocDownloaded}'"));

        // Members get the file as it was uploaded.
        Assert.Equal(pdf, await Download(t["omar"].Client, report));

        // A PDF that cannot carry the stamp is not handed to a guest.
        var broken = await Upload("broken.pdf", "%PDF-1.7 not really a document"u8.ToArray());
        var token = await OkAsync(karim.GetAsync($"/api/v1/documents/{broken}/download-token"));
        Assert.Equal("conflict.documents.guest_pdf_unmarkable", await ProblemKeyAsync(karim.GetAsync(token.GetProperty("url").GetString()), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_guest_gets_the_reduced_shell_and_a_home_listing_their_spaces()
    {
        var t = await SetupAsync();
        var first = (await SpaceAsync(t, "Vendor rollout")).GetProperty("id").GetGuid();
        var second = (await SpaceAsync(t, "Audit 2027")).GetProperty("id").GetGuid();
        await SpaceAsync(t, "Budget 2027");
        await world.SeedUserAsync(t, "karim", MembershipTypes.Guest);
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Spaces}/{first}/guests", new { email = t["karim"].Email, days = 30 }));
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Spaces}/{second}/guests", new { email = t["karim"].Email, role = "member", days = 10 }));
        var karim = t["karim"].Client;

        // The shell has no navigation, search, assistant or home page, and says the person is a guest.
        var shell = await OkAsync(karim.GetAsync("/api/v1/experience/shell"));
        Assert.True(shell.GetProperty("guest").GetBoolean());
        Assert.Equal(0, shell.GetProperty("globalMenu").GetArrayLength());
        Assert.False(shell.GetProperty("assistant").GetBoolean());
        Assert.Equal(JsonValueKind.Null, shell.GetProperty("home").ValueKind);
        var regions = new[] { "start", "center", "end", "below" }.SelectMany(r => shell.GetProperty("layout").GetProperty(r).EnumerateArray().Select(x => x.GetString())).ToArray();
        Assert.DoesNotContain("search", regions);
        Assert.DoesNotContain("global_navigation", regions);
        Assert.Contains("settings", regions);

        // Members keep the full shell.
        var full = await OkAsync(t["omar"].Client.GetAsync("/api/v1/experience/shell"));
        Assert.False(full.GetProperty("guest").GetBoolean());

        // The guest home lists their spaces, soonest end first, with the sponsor and the end date.
        var mine = (await OkAsync(karim.GetAsync($"{Spaces}/as-guest"))).EnumerateArray().ToArray();
        Assert.Equal([second, first], mine.Select(s => s.GetProperty("spaceId").GetGuid()).ToArray());
        Assert.Equal(("member", $"omar ({t.Key})"), (mine[0].GetProperty("role").GetString(), mine[0].GetProperty("sponsorName").GetString()));
        Assert.True(mine[0].GetProperty("expiresAt").GetDateTimeOffset() < mine[1].GetProperty("expiresAt").GetDateTimeOffset());
        // A member has no guest spaces.
        Assert.Equal(0, (await OkAsync(t["omar"].Client.GetAsync($"{Spaces}/as-guest"))).GetArrayLength());
    }
}
