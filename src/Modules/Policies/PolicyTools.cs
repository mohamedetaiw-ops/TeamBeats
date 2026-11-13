using System.Text.Json;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Content.Contracts;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// <c>get_policy</c> (spec 035 section 6, scope <c>policies:read</c>), exposable to external AI clients: the version in
/// force of a published policy that applies to the employee, clause by clause in their language or the binding one, as
/// data with its classification. Attesting stays a human action inside TeamBeats.
/// </summary>
public sealed class GetPolicyTool(PolicyRegister register, TeamBeats.Localization.RequestLocale locale) : IAgentTool
{
    public const string Name = "get_policy";

    public ToolDefinition Definition { get; } = new(
        Name, "policies", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolArgs.Schema("""{"type":"object","additionalProperties":false,"required":["id"],"properties":{"id":{"type":"string","minLength":36,"maxLength":36}}}"""),
        "restricted", Idempotent: true, TimeoutSeconds: 10,
        "Read one published policy that applies to the employee, by its ID: code, name, version, effective date and the text of each clause.",
        TeamBeats.Entitlements.Contracts.Capabilities.PolicyCore, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (ToolArgs.Id(arguments, "id") is not { } id)
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var reading = await register.CurrentAsync(id, ct);
        var wanted = reading.Version.Locales.Contains(locale.Locale) ? locale.Locale : reading.BindingLocale ?? reading.Version.Locales.FirstOrDefault() ?? locale.Locale;
        var name = reading.Policy.Names.TryGetValue(wanted, out var n) ? n : reading.Policy.Names.Values.FirstOrDefault() ?? reading.Policy.Code;
        var link = $"/policies/{reading.Policy.Id}";
        var content = ToolArgs.Json(new
        {
            id = reading.Policy.Id, code = reading.Policy.Code, name, classification = reading.Policy.Classification, version = reading.Version.Number,
            effectiveFrom = reading.Version.EffectiveFrom, locale = wanted, bindingLocale = reading.BindingLocale, link,
            clauses = reading.Version.Clauses.Select(c => new
            {
                number = c.Number, title = c.Titles.TryGetValue(wanted, out var t) ? t : c.Titles.Values.FirstOrDefault(),
                text = c.Bodies.TryGetValue(wanted, out var b) ? BodyValidator.PlainText(b) : c.Bodies.Values.FirstOrDefault() is { } any ? BodyValidator.PlainText(any) : string.Empty,
            }),
        });
        return new ToolResult(true, content, reading.Policy.Classification,
            [new ToolSource("policies", reading.Policy.Id, name, link, reading.Policy.Classification)]);
    }
}
