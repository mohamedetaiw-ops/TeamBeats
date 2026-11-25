using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using TeamBeats.Localization;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi;

/// <summary>One prompt of the catalogue: its name, the tools it relies on and its arguments (required ones first).</summary>
internal sealed record McpPrompt(string Name, IReadOnlyList<string> Tools, IReadOnlyList<(string Name, bool Required)> Arguments)
{
    public string Key(string part) => $"mcp.prompt.{Name}.{part}";
}

/// <summary>
/// The MCP prompts of the tenant's server (spec 035 R3, T022): a small catalogue of tasks an employee starts from their
/// AI client, each built on TeamBeats tools. A prompt is listed and served only when the tool gateway offers the caller
/// every tool it names, so prompts follow the scopes, the package and the external AI client policy exactly as tools do.
/// Titles, descriptions and texts are localized by the request's language; arguments are short text, put in the prompt
/// in quotes as the employee's words. A prompt reads nothing itself and returns no TeamBeats data.
/// </summary>
internal static class McpPrompts
{
    public const int MaxArgumentLength = 300;

    public static readonly IReadOnlyList<McpPrompt> Catalogue =
    [
        new("catch_up_on_news", ["list_news"], []),
        new("find_a_policy", ["search", "get_policy"], [("topic", true)]),
        new("who_can_help", ["find_person"], [("need", true)]),
        new("my_open_items", ["list_my_requests", "list_my_tasks"], []),
        new("request_a_service", ["list_services", "submit_service_request"], [("need", true)]),
    ];

    private static string Locale(HttpContext http) => http.RequestServices.GetService<RequestLocale>()?.Locale ?? Locales.Fallback;

    /// <summary>The prompts whose every tool the gateway offers this caller now.</summary>
    private static async Task<IReadOnlyList<McpPrompt>> OfferedAsync(HttpContext http, ExternalCaller caller, IReadOnlySet<string> scoped, CancellationToken ct)
    {
        var needed = Catalogue.SelectMany(p => p.Tools).Where(scoped.Contains).ToHashSet(StringComparer.Ordinal);
        var offered = (await http.RequestServices.GetRequiredService<IExternalToolGateway>().ToolsAsync(caller, needed, ct))
            .Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return Catalogue.Where(p => p.Tools.All(offered.Contains)).ToList();
    }

    public static async ValueTask<ListPromptsResult> ListAsync(HttpContext http, ExternalCaller caller, IReadOnlySet<string> scoped, CancellationToken ct)
    {
        var localizer = http.RequestServices.GetRequiredService<ILocalizer>();
        var locale = Locale(http);
        return new ListPromptsResult
        {
            Prompts = (await OfferedAsync(http, caller, scoped, ct)).Select(p => new Prompt
            {
                Name = p.Name,
                Title = localizer.Get(p.Key("title"), locale),
                Description = localizer.Get(p.Key("description"), locale),
                Arguments = p.Arguments.Select(a => new PromptArgument
                {
                    Name = a.Name, Required = a.Required, Description = localizer.Get(p.Key("arg." + a.Name), locale),
                }).ToList(),
            }).ToList(),
        };
    }

    public static async ValueTask<GetPromptResult> GetAsync(HttpContext http, ExternalCaller caller, IReadOnlySet<string> scoped, GetPromptRequestParams? request,
        CancellationToken ct)
    {
        var prompt = (await OfferedAsync(http, caller, scoped, ct)).FirstOrDefault(p => p.Name == request?.Name)
            ?? throw new McpProtocolException("The prompt is not known.", McpErrorCode.InvalidParams);
        var given = request?.Arguments ?? new Dictionary<string, JsonElement>();
        if (given.Keys.Any(k => prompt.Arguments.All(a => a.Name != k)))
        {
            throw new McpProtocolException("The prompt does not take that argument.", McpErrorCode.InvalidParams);
        }
        var localizer = http.RequestServices.GetRequiredService<ILocalizer>();
        var locale = Locale(http);
        var text = localizer.Get(prompt.Key("text"), locale);
        foreach (var (name, required) in prompt.Arguments)
        {
            var value = given.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;
            if (string.IsNullOrEmpty(value))
            {
                if (required)
                {
                    throw new McpProtocolException($"The argument {name} is required.", McpErrorCode.InvalidParams);
                }
                value = "";
            }
            if (value.Length > MaxArgumentLength)
            {
                throw new McpProtocolException($"The argument {name} is longer than {MaxArgumentLength} characters.", McpErrorCode.InvalidParams);
            }
            // The employee's words go in quotes, with quotes and line breaks taken out, so they read as one value.
            var quoted = new string(value.Select(c => c is '"' or '“' or '”' ? '\'' : char.IsControl(c) ? ' ' : c).ToArray());
            text = text.Replace("{" + name + "}", quoted, StringComparison.Ordinal);
        }
        return new GetPromptResult
        {
            Description = localizer.Get(prompt.Key("description"), locale),
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
        };
    }
}
