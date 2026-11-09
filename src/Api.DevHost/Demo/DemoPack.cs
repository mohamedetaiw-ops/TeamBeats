using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBeats.Modules.Services;

namespace TeamBeats.Api.DevHost.Demo;

/// <summary>
/// The demo data package (docs/testing/demo-data.md): fictional, bilingual content for every in-app area of the
/// intranet, read from the JSON files in one folder and merged. People are named by persona key (see
/// <see cref="People"/>) so the files never repeat an address; <c>admin</c> is always the first configured test
/// administrator. Text is given per locale; a body is a list of lines where <c>## </c> starts a heading and
/// <c>- </c> a list item. Times are relative to the seeding day (<c>DaysAgo</c>).
/// </summary>
public sealed class DemoPack
{
    [JsonPropertyName("$comment")]
    public string? Comment { get; init; }

    /// <summary>Persona key to email address. Configuration can override any of them (Dev:SeedDemo:People:key).</summary>
    public Dictionary<string, string> People { get; init; } = [];

    public DemoOrgMapping? OrgMapping { get; init; }

    public List<DemoProfile> Profiles { get; init; } = [];

    public List<DemoRole> Roles { get; init; } = [];

    public List<DemoAssignment> Assignments { get; init; } = [];

    public List<DemoArticle> News { get; init; } = [];

    public List<DemoArticle> Knowledge { get; init; } = [];

    public List<DemoCategory> ServiceCategories { get; init; } = [];

    public List<DemoService> Services { get; init; } = [];

    public List<DemoRequest> Requests { get; init; } = [];

    public List<DemoLibrary> Libraries { get; init; } = [];

    public List<DemoCategory> PolicyCategories { get; init; } = [];

    public List<DemoPolicy> Policies { get; init; } = [];

    public List<DemoCalendar> Calendars { get; init; } = [];

    public DemoSearch? Search { get; init; }

    public List<DemoConversation> Assistant { get; init; } = [];

    /// <summary>Adds the lists of another file to this one; people and single sections from the later file win.</summary>
    public DemoPack Merge(DemoPack other) => new()
    {
        People = People.Concat(other.People).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value),
        OrgMapping = other.OrgMapping ?? OrgMapping,
        Profiles = [.. Profiles, .. other.Profiles],
        Roles = [.. Roles, .. other.Roles],
        Assignments = [.. Assignments, .. other.Assignments],
        News = [.. News, .. other.News],
        Knowledge = [.. Knowledge, .. other.Knowledge],
        ServiceCategories = [.. ServiceCategories, .. other.ServiceCategories],
        Services = [.. Services, .. other.Services],
        Requests = [.. Requests, .. other.Requests],
        Libraries = [.. Libraries, .. other.Libraries],
        PolicyCategories = [.. PolicyCategories, .. other.PolicyCategories],
        Policies = [.. Policies, .. other.Policies],
        Calendars = [.. Calendars, .. other.Calendars],
        Search = other.Search ?? Search,
        Assistant = [.. Assistant, .. other.Assistant],
    };
}

/// <summary>
/// Builds the organization units from the directory's attributes, top level first (spec 002 US5), with aliases for
/// values spelled two ways and the other-locale names of the units by their directory name.
/// </summary>
public sealed record DemoOrgMapping(List<string> Levels, Dictionary<string, string>? Aliases, Dictionary<string, Dictionary<string, string>>? UnitNames);

/// <summary>A person's names per locale (so Arabic pages show Arabic names) and own time zone.</summary>
public sealed record DemoProfile(string Person, Dictionary<string, string>? Names, string? TimeZone);

/// <summary>A tenant role the demo needs that is not built in, such as policy owners who may author policies.</summary>
public sealed record DemoRole(string Key, Dictionary<string, string> Names, List<string> Permissions);

/// <summary>A role given to a person for the whole tenant, so they can publish, own policies and so on as themselves.</summary>
public sealed record DemoAssignment(string Person, string Role);

public sealed record DemoReaction(string Person, string Kind);

public sealed record DemoComment(string Person, string Text);

/// <summary>A news item, announcement or knowledge article, found again by its English title.</summary>
public sealed record DemoArticle(
    string Key,
    string Author,
    Dictionary<string, string> Title,
    Dictionary<string, string>? Summary,
    Dictionary<string, List<string>> Body,
    string Type = "news",
    string? Owner = null,
    double DaysAgo = 0,
    bool Pinned = false,
    bool Featured = false,
    List<DemoReaction>? Reactions = null,
    List<DemoComment>? Comments = null);

public sealed record DemoCategory(string Key, Dictionary<string, string> Names);

public sealed record DemoApproval(Dictionary<string, string>? Names, List<string>? Approvers, bool LineManager = false, int? DueWorkingDays = null);

/// <summary>A form service or a linked application, found again by its English name.</summary>
public sealed record DemoService(
    string Key,
    string Category,
    Dictionary<string, string> Names,
    Dictionary<string, string>? Descriptions,
    Dictionary<string, List<string>>? Synonyms,
    FormDefinition? Form,
    DemoApproval? Approval = null,
    string Kind = "form",
    string? Url = null,
    bool AllowOnBehalf = false,
    List<string>? FavouriteOf = null);

/// <summary>What happens to a request after it is submitted: approve, reject or return, by whom and when.</summary>
public sealed record DemoDecision(string By, string Outcome, string? Comment, double DaysAgo = 0);

/// <summary>
/// A request someone submitted. Values follow the form's field types; a date may be written relative to the day it
/// was submitted, as <c>+5d</c> or <c>-2d</c>. It is submitted once, by its key.
/// </summary>
public sealed record DemoRequest(string Key, string Service, string Requester, Dictionary<string, JsonElement> Data, double DaysAgo = 0,
    List<DemoDecision>? Decisions = null);

public sealed record DemoDocument(
    string Name,
    string Author,
    Dictionary<string, string> Title,
    List<string> Lines,
    string? Folder = null,
    string Classification = "internal");

public sealed record DemoLibrary(string Key, Dictionary<string, string> Names, List<string>? Folders, List<DemoDocument> Documents, string Owner = "admin");

public sealed record DemoClause(int Level, Dictionary<string, string> Title, Dictionary<string, string> Body);

public sealed record DemoCampaign(Dictionary<string, string> Text, int DeadlineDays, List<string>? AttestedBy);

/// <summary>A policy published through its approval route, found again by its code.</summary>
public sealed record DemoPolicy(
    string Code,
    string Category,
    Dictionary<string, string> Names,
    string Owner,
    string Approver,
    List<DemoClause> Clauses,
    double DaysAgo = 0,
    DemoCampaign? Campaign = null);

public sealed record DemoHoliday(string Date, Dictionary<string, string> Names, bool HalfDay = false);

public sealed record DemoCalendar(string Key, Dictionary<string, string> Names, string Country, List<int> WeekDays, int Year, List<DemoHoliday> Holidays);

/// <summary>A promoted result: <c>Ref</c> names a seeded item as <c>news:key</c>, <c>kb:key</c>, <c>service:key</c> or <c>policy:code</c>.</summary>
public sealed record DemoBestBet(string Query, string? Locale, string Ref);

public sealed record DemoSynonyms(string? Locale, List<string> Terms);

public sealed record DemoSearch(List<DemoBestBet>? BestBets, List<DemoSynonyms>? Synonyms);

public sealed record DemoConversation(string Person, List<string> Questions);
