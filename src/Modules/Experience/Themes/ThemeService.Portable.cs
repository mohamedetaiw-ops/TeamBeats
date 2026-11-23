using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Themes;

public sealed partial class ThemeService
{
    /// <summary>
    /// The brand's tokens as a DTCG 2025.10 token file (spec 033 R2 T022, FR-014): the draft when one is kept, otherwise
    /// the version in force, which is what the brand manager shows.
    /// </summary>
    public async Task<JsonObject> ExportTokensAsync(Guid id, CancellationToken ct)
    {
        var draft = await GetDraftAsync(id, ct);
        return DtcgTokens.Export(draft.Tokens, draft.Name);
    }

    /// <summary>
    /// Reads a token file into the brand's draft, replacing its tokens; the name, assets and contrast setting stay. Every value
    /// is checked as in the editor, so nothing outside the token types (and no free CSS) gets in, and the draft goes live
    /// only through the usual publish, approval or schedule.
    /// </summary>
    public async Task<BrandDraftView> ImportTokensAsync(Guid id, JsonNode? file, CancellationToken ct)
    {
        var current = await GetDraftAsync(id, ct);
        var (tokens, problems) = DtcgTokens.Import(file);
        if (problems.Count > 0)
        {
            var errors = new ValidationErrors();
            foreach (var (path, key) in problems)
            {
                errors.Add(path == "file" ? "file" : $"tokens.{path}", key);
            }
            errors.ThrowIfAny();
        }
        var draft = await SaveDraftAsync(id, new BrandRequest(current.Name, tokens, current.Assets, current.StrictContrast), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ThemeTokensImported, "brand", id.ToString(),
            Changes: new { brand = id, tokens = tokens.Count }), ct);
        return draft;
    }
}
