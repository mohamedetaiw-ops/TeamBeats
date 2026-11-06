using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamBeats.DevFakes;

/// <summary>
/// A fictional world for the fakes, read from a JSON scenario file (for example
/// <c>src/Api.DevHost/scenarios/staging-demo.json</c>): Entra ID directories with their people, groups, administrative
/// units, rooms, mail and calendar, and how the seeded tenant is connected to them. Times are relative to the day the
/// host starts, so the calendar always has meetings today and this week. Fictional data only (CLAUDE.md rule 9).
/// </summary>
public sealed class FakeScenario
{
    public string Name { get; set; } = string.Empty;
    public List<ScenarioDirectory> Directories { get; set; } = [];
    public ScenarioTenant? Tenant { get; set; }

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static FakeScenario Load(string path)
    {
        var scenario = JsonSerializer.Deserialize<FakeScenario>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException($"Scenario file {path} is empty.");
        scenario.Validate();
        return scenario;
    }

    public ScenarioDirectory? FindDirectory(string id) =>
        Directories.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fails fast on a broken scenario, and on anything that looks like real data.</summary>
    public void Validate()
    {
        foreach (var d in Directories)
        {
            if (!Guid.TryParse(d.Id, out _))
            {
                throw new InvalidOperationException($"Scenario directory id '{d.Id}' must be a GUID.");
            }
            var ids = d.Users.Select(u => u.Id).Concat(d.Groups.Select(g => g.Id)).Concat(d.AdminUnits.Select(g => g.Id)).ToList();
            if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
            {
                throw new InvalidOperationException($"Scenario directory {d.Id} has duplicate object IDs.");
            }
            foreach (var mail in d.Users.Select(u => u.Mail).Concat(d.Rooms.Select(r => r.EmailAddress)))
            {
                // Reserved example domains only (RFC 2606), so a scenario can never hold a real person's address.
                if (!mail.EndsWith(".example", StringComparison.OrdinalIgnoreCase) && !mail.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Scenario address {mail} is not on a reserved example domain.");
                }
            }
            var known = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var u in d.Users)
            {
                foreach (var g in u.Groups.Concat(u.AdminUnits).Where(g => !known.Contains(g)))
                {
                    throw new InvalidOperationException($"Scenario user {u.Mail} is a member of unknown group or unit {g}.");
                }
            }
        }
        if (Tenant is { } t && FindDirectory(t.Directory) is null)
        {
            throw new InvalidOperationException($"Scenario tenant points at unknown directory {t.Directory}.");
        }
    }

    /// <summary>Loads the directories into the fake Graph: people, groups, units, consent, rooms, mail and calendar.</summary>
    public void ApplyTo(FakeMicrosoftGraph graph, DateTimeOffset now)
    {
        foreach (var d in Directories)
        {
            var directoryId = Guid.Parse(d.Id);
            var data = graph.Data(directoryId);
            foreach (var g in d.Groups)
            {
                data.AddGroup(g.Id, g.DisplayName);
            }
            foreach (var g in d.AdminUnits)
            {
                data.AddAdminUnit(g.Id, g.DisplayName);
            }
            foreach (var u in d.Users)
            {
                data.AddUser(u.Id, u.DisplayName, u.Mail, u.Enabled, u.JobTitle);
                data.SetOrg(u.Id, u.CompanyName, u.Department, u.Division);
                data.SetProperties(u.Id, new Dictionary<string, object?>
                {
                    ["givenName"] = u.GivenName,
                    ["surname"] = u.Surname,
                    ["officeLocation"] = u.OfficeLocation,
                    ["mobilePhone"] = u.MobilePhone,
                    ["businessPhones"] = u.MobilePhone is null ? Array.Empty<string>() : new[] { u.MobilePhone },
                    ["employeeId"] = u.EmployeeId,
                    ["preferredLanguage"] = u.PreferredLanguage,
                });
                foreach (var g in u.Groups.Concat(u.AdminUnits))
                {
                    data.AddMember(g, u.Id);
                }
            }
            if (d.Consented)
            {
                graph.Consent(directoryId, d.GrantedRoles, d.Users.Count, d.Groups.Count);
                graph.AddDelegatedConsent(directoryId, d.GrantedDelegated.ToArray());
            }
            foreach (var r in d.Rooms)
            {
                graph.AddRoom(directoryId, new FakeMicrosoftGraph.Room(r.Id, r.DisplayName, r.EmailAddress, r.Capacity, r.Building, r.FloorLabel));
            }

            var offset = TimeSpan.Parse(d.UtcOffset.TrimStart('+'), CultureInfo.InvariantCulture);
            var today = new DateTimeOffset(now.ToOffset(offset).Date, offset);
            var byMail = d.Users.ToDictionary(u => u.Mail, StringComparer.OrdinalIgnoreCase);
            var n = 0;
            foreach (var m in d.Messages)
            {
                var to = byMail[m.To];
                var from = byMail.GetValueOrDefault(m.From);
                graph.MailboxOf(directoryId, to.Id).Messages.Add(new FakeMicrosoftGraph.Message($"msg-{++n:D4}", m.Subject, from?.DisplayName ?? m.FromName ?? m.From,
                    m.From, now - TimeSpan.FromHours(m.HoursAgo), m.Preview, m.IsRead, m.Importance));
            }
            foreach (var e in d.Events)
            {
                var organizer = byMail[e.Organizer];
                var start = today.AddDays(e.DayOffset) + TimeSpan.Parse(e.Start, CultureInfo.InvariantCulture);
                var ev = new FakeMicrosoftGraph.Event(e.Id, e.Subject, start, start.AddMinutes(e.Minutes), e.Location, organizer.DisplayName, organizer.Mail,
                    e.Attendees, e.Online, e.ShowAs);
                graph.MailboxOf(directoryId, organizer.Id).Events.Add(ev);
            }
        }
    }

    /// <summary>The users the fake Entra ID sign-in page offers for a directory: enabled accounts, as they are now.</summary>
    public static IReadOnlyList<FakeUser> SignInUsers(FakeMicrosoftGraph graph, Guid directoryId, IReadOnlyDictionary<string, IReadOnlyList<string>> groupsByUser) =>
        graph.Data(directoryId).CurrentUsers()
            .Where(u => u.Properties.GetValueOrDefault("accountEnabled") is not false && u.Properties.GetValueOrDefault("mail") is string)
            .Select(u => new FakeUser(u.Id, (string)u.Properties["mail"]!, u.Properties.GetValueOrDefault("displayName") as string ?? (string)u.Properties["mail"]!)
            {
                Groups = groupsByUser.GetValueOrDefault(u.Id, []),
            })
            .ToList();
}

public sealed class ScenarioDirectory
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Offset used to place calendar times, for example "+04:00".</summary>
    public string UtcOffset { get; set; } = "+00:00";

    /// <summary>When true, the directory's admin has already granted the TeamBeats application these roles and delegated permissions.</summary>
    public bool Consented { get; set; } = true;

    public List<string> GrantedRoles { get; set; } = ["User.Read.All", "Group.Read.All", "AdministrativeUnit.Read.All"];

    /// <summary>Delegated permissions the directory's admin has consented for the TeamBeats application (spec 018).</summary>
    public List<string> GrantedDelegated { get; set; } = ["User.Read", "Mail.ReadBasic", "Mail.Read", "Calendars.ReadBasic"];

    /// <summary>The tenant's own app registration for single sign-on in this directory (fictional values).</summary>
    public ScenarioSsoApp? SsoApp { get; set; }

    public List<ScenarioUser> Users { get; set; } = [];
    public List<ScenarioGroup> Groups { get; set; } = [];
    public List<ScenarioGroup> AdminUnits { get; set; } = [];
    public List<ScenarioRoom> Rooms { get; set; } = [];
    public List<ScenarioMessage> Messages { get; set; } = [];
    public List<ScenarioEvent> Events { get; set; } = [];
}

public sealed class ScenarioSsoApp
{
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Name of the tenant secret reference (<c>secret://tenants/{id}/{name}</c>) the seeder configures.</summary>
    public string SecretName { get; set; } = "fake-entra-sso";

    /// <summary>A fictional client secret that only the fake Entra ID accepts.</summary>
    public string ClientSecret { get; set; } = string.Empty;
}

public sealed class ScenarioUser
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? GivenName { get; set; }
    public string? Surname { get; set; }
    public string Mail { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string? CompanyName { get; set; }
    public string? Department { get; set; }
    public string? Division { get; set; }
    public string? OfficeLocation { get; set; }
    public string? MobilePhone { get; set; }
    public string? EmployeeId { get; set; }
    public string? PreferredLanguage { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> Groups { get; set; } = [];
    public List<string> AdminUnits { get; set; } = [];
}

public sealed class ScenarioGroup
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ScenarioRoom
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public string? Building { get; set; }
    public string? FloorLabel { get; set; }
}

public sealed class ScenarioMessage
{
    public string To { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    public double HoursAgo { get; set; }
    public bool IsRead { get; set; }
    public string Importance { get; set; } = "normal";
}

public sealed class ScenarioEvent
{
    public string Id { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Organizer { get; set; } = string.Empty;
    public List<string> Attendees { get; set; } = [];
    public int DayOffset { get; set; }

    /// <summary>Local start time in the directory's offset, for example "09:30".</summary>
    public string Start { get; set; } = "09:00";

    public int Minutes { get; set; } = 30;
    public string? Location { get; set; }
    public bool Online { get; set; }
    public string ShowAs { get; set; } = "busy";
}

/// <summary>How the seeded tenant uses the scenario: single sign-on, the Microsoft 365 connection and directory sync.</summary>
public sealed class ScenarioTenant
{
    public string Directory { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = "Microsoft 365 (fake)";
    public List<string> Features { get; set; } = ["directory_sync"];
    public bool ConfigureSso { get; set; } = true;
    public string? SsoDisplayName { get; set; }
    public List<string> JitDomains { get; set; } = [];

    /// <summary>Entra group object ID to built-in role key at tenant scope (spec 003 US2 scenario 6).</summary>
    public Dictionary<string, string> GroupRoles { get; set; } = [];

    public bool RunInitialSync { get; set; } = true;

    /// <summary>Deletion threshold for the seeded sync settings; 100 lets testers disable many fake users at once.</summary>
    public int DeletionThresholdPct { get; set; } = 100;
}
