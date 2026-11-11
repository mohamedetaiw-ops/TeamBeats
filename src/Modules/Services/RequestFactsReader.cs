using System.Text.Json;
using TeamBeats.Modules.Services.Contracts;

namespace TeamBeats.Modules.Services;

/// <summary>The request facts other modules read when a request ends (spec 022 FR-014), from the request row in the current tenant.</summary>
public sealed class RequestFactsReader(Requests requests) : IRequestFacts
{
    public async Task<RequestFacts?> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        if (await requests.RowAsync(requestId, false, ct) is not { } row)
        {
            return null;
        }
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Data) ?? [];
        return new RequestFacts(row.Id, row.ServiceId, row.SubjectId, row.OrgUnitId, row.Status, data);
    }
}
