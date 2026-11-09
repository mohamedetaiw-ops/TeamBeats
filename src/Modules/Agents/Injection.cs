using System.Text.RegularExpressions;
using TeamBeats.Modules.Agents.Contracts;

namespace TeamBeats.Modules.Agents;

/// <summary>A rule that matched untrusted content, with the item it came from when that is known.</summary>
internal sealed record InjectionFinding(string Rule, string? Source, Guid? ItemId);

/// <summary>
/// The injection detector of spec 027 US5 (plan "Untrusted content"): it flags instruction-like text in retrieved
/// passages and tool results, in English and Arabic, so the run records <c>agent.injection_detected</c> with the source
/// item. Detection never decides what the agent may do: the content still goes to the model only as marked data, and a
/// tool call still needs a model turn checked by the tool gateway. The rules are deliberately narrow, so ordinary
/// documents that talk about instructions are not flagged.
/// </summary>
internal static partial class InjectionDetector
{
    public const string IgnoreInstructions = "ignore_instructions";
    public const string RoleOverride = "role_override";
    public const string PromptDisclosure = "prompt_disclosure";
    public const string ToolCallText = "tool_call_text";

    [GeneratedRegex(@"\b(ignore|disregard|forget|override)\s+(all\s+|any\s+|the\s+|your\s+)?(previous|prior|above|earlier|preceding|system|original)\s+(instructions|rules|prompts?|messages?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IgnoreEnglish();

    // Arabic: تجاهل / انس / تخط followed within a few words by التعليمات / الأوامر / القواعد.
    [GeneratedRegex(@"(تجاهل|تجاهلي|تجاهلوا|انس|انسى|تخط|تخطى|اهمل|أهمل)\s+(\S+\s+){0,3}?(ال)?(تعليمات|أوامر|اوامر|قواعد|توجيهات)")]
    private static partial Regex IgnoreArabic();

    [GeneratedRegex(@"\b(you\s+are\s+now|from\s+now\s+on\s+you\s+are|act\s+as\s+(the\s+)?(system|administrator|admin|developer))\b|\bnew\s+instructions\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleEnglish();

    [GeneratedRegex(@"\b(reveal|print|show|repeat|output)\s+(me\s+)?(your|the)\s+(system\s+prompt|hidden\s+instructions|instructions\s+above)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DisclosureEnglish();

    [GeneratedRegex(@"(""tool_calls""|""function_call""|<\s*tool_call|<\s*function_calls?|""name""\s*:\s*""[a-z_]+(\.|__)[a-z_]+""\s*,\s*""arguments"")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolCall();

    /// <summary>The first rule the text breaks, or null. Markup escaped for the envelope is read back first.</summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var plain = text.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
        if (IgnoreEnglish().IsMatch(plain) || IgnoreArabic().IsMatch(plain))
        {
            return IgnoreInstructions;
        }
        if (RoleEnglish().IsMatch(plain))
        {
            return RoleOverride;
        }
        if (DisclosureEnglish().IsMatch(plain))
        {
            return PromptDisclosure;
        }
        return ToolCall().IsMatch(plain) ? ToolCallText : null;
    }

    /// <summary>
    /// Findings in a tool result. A result of numbered passages is checked passage by passage, so each finding names its
    /// source item when the tool gave one source per passage; anything else is checked as a whole and names the sources
    /// when there is exactly one, or none.
    /// </summary>
    public static IReadOnlyList<InjectionFinding> Scan(ToolResult result)
    {
        if (!result.Ok)
        {
            return [];
        }
        var passages = PassageTexts(result.Content);
        if (passages.Count > 0 && passages.Count == result.Sources.Count)
        {
            var found = new List<InjectionFinding>();
            for (var i = 0; i < passages.Count; i++)
            {
                if (Detect(passages[i]) is { } rule)
                {
                    found.Add(new InjectionFinding(rule, result.Sources[i].Source, result.Sources[i].ItemId));
                }
            }
            return found;
        }
        if (Detect(result.Content) is not { } whole)
        {
            return [];
        }
        return result.Sources.Count == 1 ? [new InjectionFinding(whole, result.Sources[0].Source, result.Sources[0].ItemId)] : [new InjectionFinding(whole, null, null)];
    }

    [GeneratedRegex(@"<passage\b[^>]*>(.*?)</passage>", RegexOptions.Singleline)]
    private static partial Regex Passage();

    private static List<string> PassageTexts(string content) => Passage().Matches(content).Select(m => m.Groups[1].Value).ToList();

    [GeneratedRegex(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    /// <summary>Whether an answer names a web address (US5 scenario 3): answers are shown as text, so such a link is never opened or loaded.</summary>
    public static bool HasLinks(string? answer) => answer is not null && Link().IsMatch(answer);
}
