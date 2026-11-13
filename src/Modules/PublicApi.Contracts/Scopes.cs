using TeamBeats.Entitlements.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi.Contracts;

/// <summary>Who may hold a scope: an app acting for an employee, an app acting as itself, or both.</summary>
public enum ScopeHolder
{
    Delegated,
    Application,
    Both,
}

/// <summary>
/// One scope of the catalogue (spec 035 section 6). A scope only narrows (FR-009): it never grants access by itself, the
/// operation still asks the central authorization service. <paramref name="UserConsent"/> is the catalogue default for
/// whether an employee may consent alone; the tenant may change it.
/// </summary>
/// <remarks><paramref name="Tools"/> are the MCP tools the scope covers (section 6, MCP surface).</remarks>
public sealed record ScopeDefinition(string Key, IReadOnlyList<string> Capabilities, bool UserConsent, ScopeHolder Holder, IReadOnlyList<string>? Tools = null,
    IReadOnlyList<string>? Resources = null)
{
    public bool AllowsDelegated => Holder is ScopeHolder.Delegated or ScopeHolder.Both;

    public bool AllowsApplication => Holder is ScopeHolder.Application or ScopeHolder.Both;

    /// <summary>The resource key of the scope's description on consent screens.</summary>
    public string DescriptionKey => "apiAccess.scope." + Key.Replace(':', '_');
}

/// <summary>The scope catalogue of spec 035 section 6, the only list of scopes (FR-008).</summary>
public static class ScopeCatalogue
{
    public const string ProfileRead = "profile:read";
    public const string ContentRead = "content:read";
    public const string ContentDraft = "content:draft";
    public const string DocumentsRead = "documents:read";
    public const string DocumentsWrite = "documents:write";
    public const string PeopleRead = "people:read";
    public const string OrgRead = "org:read";
    public const string PoliciesRead = "policies:read";
    public const string SearchQuery = "search:query";
    public const string AssistantAsk = "assistant:ask";
    public const string ServicesRead = "services:read";
    public const string RequestsRead = "requests:read";
    public const string RequestsSubmit = "requests:submit";
    public const string TasksRead = "tasks:read";
    public const string EventsSubscribe = "events:subscribe";

    public static readonly IReadOnlyList<ScopeDefinition> All =
    [
        new(ProfileRead, [Capabilities.ApiPublic], true, ScopeHolder.Delegated, ["whoami"]),
        new(ContentRead, [Capabilities.ApiPublic], true, ScopeHolder.Both, ["get_page", "list_news"], [McpResources.Pages]),
        new(ContentDraft, [Capabilities.ApiWriteScopes], false, ScopeHolder.Delegated, ["draft_news"]),
        new(DocumentsRead, [Capabilities.ApiPublic], true, ScopeHolder.Both, ["get_document"], [McpResources.Documents]),
        new(DocumentsWrite, [Capabilities.ApiWriteScopes], false, ScopeHolder.Both),
        new(PeopleRead, [Capabilities.ApiPublic], true, ScopeHolder.Both, ["find_person"], [McpResources.People]),
        new(OrgRead, [Capabilities.ApiPublic], true, ScopeHolder.Both),
        new(PoliciesRead, [Capabilities.ApiPublic], true, ScopeHolder.Both, ["get_policy"], [McpResources.Policies]),
        new(SearchQuery, [Capabilities.ApiPublic], true, ScopeHolder.Both, ["search"]),
        new(AssistantAsk, [Capabilities.ApiPublic, Capabilities.AiAssistant], true, ScopeHolder.Delegated, ["ask_teambeats"]),
        new(ServicesRead, [Capabilities.ApiPublic], true, ScopeHolder.Both, ["list_services"]),
        new(RequestsRead, [Capabilities.ApiPublic], true, ScopeHolder.Both, ["list_my_requests"]),
        new(RequestsSubmit, [Capabilities.ApiWriteScopes], false, ScopeHolder.Both, ["submit_service_request"]),
        new(TasksRead, [Capabilities.ApiPublic], true, ScopeHolder.Delegated, ["list_my_tasks"]),
        new(EventsSubscribe, [Capabilities.ApiEventSubscriptions], false, ScopeHolder.Application),
    ];

    private static readonly Dictionary<string, ScopeDefinition> ByKey = All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    public static ScopeDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    public static IReadOnlyList<string> Keys { get; } = All.Select(s => s.Key).ToList();

    /// <summary>The MCP tools a set of granted scopes covers; a tool no scope names is never offered.</summary>
    public static IReadOnlySet<string> ToolsFor(IEnumerable<string> scopes) =>
        scopes.Select(Find).Where(s => s is not null).SelectMany(s => s!.Tools ?? []).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// The MCP resources of spec 035 section 6: pages, documents, policies and people by <c>teambeats://{kind}/{id}</c>.
/// Each kind is read through a tool of the owning module, so the same gateway checks and classification cap apply.
/// </summary>
public static class McpResources
{
    public const string Scheme = "teambeats";
    public const string Pages = "pages";
    public const string Documents = "documents";
    public const string Policies = "policies";
    public const string People = "people";

    /// <summary>The tool that reads each kind.</summary>
    public static readonly IReadOnlyDictionary<string, string> Readers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Pages] = "get_page", [Documents] = "get_document", [Policies] = "get_policy", [People] = "get_person",
    };

    public static string Uri(string kind, Guid id) => $"{Scheme}://{kind}/{id}";

    /// <summary>The kind and ID of a resource URI, or null when it is not one of ours.</summary>
    public static (string Kind, Guid Id)? Parse(string? uri)
    {
        var prefix = Scheme + "://";
        if (uri is null || !uri.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var parts = uri[prefix.Length..].Split('/');
        return parts.Length == 2 && Readers.ContainsKey(parts[0]) && Guid.TryParse(parts[1], out var id) && parts[1].Length == 36 ? (parts[0], id) : null;
    }
}

/// <summary>The kinds of app registration (FR-004).</summary>
public static class AppKinds
{
    public const string Application = "application";
    public const string DelegatedWeb = "delegated_web";
    public const string DelegatedPublic = "delegated_public";
    public const string McpClient = "mcp_client";

    public static readonly IReadOnlyList<string> All = [Application, DelegatedWeb, DelegatedPublic, McpClient];

    /// <summary>Apps that keep a secret and authenticate at the token endpoint.</summary>
    public static bool IsConfidential(string kind) => kind is Application or DelegatedWeb;

    /// <summary>Apps that act for a signed-in employee through the authorization code flow.</summary>
    public static bool IsDelegated(string kind) => kind is DelegatedWeb or DelegatedPublic or McpClient;
}

/// <summary>An app was registered (spec 035 section 12).</summary>
public sealed record AppRegistered(Guid AppId, string Kind) : IIntegrationEvent;

/// <summary>An app was disabled: its tokens are refused from now on.</summary>
public sealed record AppDisabled(Guid AppId) : IIntegrationEvent;

/// <summary>A credential of an app was revoked.</summary>
public sealed record AppCredentialRevoked(Guid AppId, Guid CredentialId) : IIntegrationEvent;

/// <summary>A person, or an API administrator for the whole tenant (null user), consented to an app.</summary>
public sealed record AppConsentGranted(Guid AppId, Guid? UserId, IReadOnlyList<string> Scopes) : IIntegrationEvent;

/// <summary>A consent ended, and with it every token family it backed.</summary>
public sealed record AppConsentRevoked(Guid AppId, Guid? UserId) : IIntegrationEvent;

/// <summary>A token family was revoked, for example after a refresh token was used twice.</summary>
public sealed record TokenFamilyRevoked(Guid FamilyId, Guid AppId, Guid UserId, string Reason) : IIntegrationEvent;

/// <summary>Why a token family ended, as <see cref="TokenFamilyRevoked"/> carries it.</summary>
public static class TokenFamilyReasons
{
    /// <summary>A refresh token or code came back a second time (US6 scenario 2).</summary>
    public const string ReuseDetected = "reuse_detected";
}

/// <summary>
/// An app passed the tenant's threshold of refused calls (authorization denials) within 10 minutes (spec 026 US5
/// scenario 5). Published once when the count reaches the threshold; WindowStart is the start of the 10 minutes.
/// UserId is the person whose calls were refused most often in the window, null for an application acting as itself.
/// </summary>
public sealed record ApiCallsDenied(Guid AppId, string AppName, Guid? UserId, int Count, DateTimeOffset WindowStart) : IIntegrationEvent;

/// <summary>
/// An app's calls in one hour were more than the tenant's factor times its hourly average over the last 30 days.
/// UserId is the person who made most of the hour's calls, null for an application acting as itself.
/// </summary>
public sealed record ApiVolumeSpike(Guid AppId, string AppName, Guid? UserId, long Calls, double HourlyAverage, DateTimeOffset HourStart) : IIntegrationEvent;
