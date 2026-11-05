using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// OpenAPI diff gate (r0-solution-structure section 8). The committed snapshot is the reviewed contract; any change
/// to it shows up in the pull request diff. Regenerate with TEAMBEATS_UPDATE_OPENAPI=1.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class OpenApiContractTests(TenantWorld world)
{
    private static string SnapshotPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TeamBeats.slnx")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "docs", "api", "openapi.v1.json");
    }

    [Fact]
    public async Task OpenApi_document_matches_committed_snapshot()
    {
        var json = await world.Factory.TenantClient(world.A.Key).GetStringAsync("/openapi/v1.json");
        var node = JsonNode.Parse(json)!;
        node["servers"]?.AsArray().Clear();
        var normalized = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var path = SnapshotPath();

        if (Environment.GetEnvironmentVariable("TEAMBEATS_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, normalized);
        }

        Assert.True(File.Exists(path), "Missing docs/api/openapi.v1.json. Run the tests with TEAMBEATS_UPDATE_OPENAPI=1 and commit the file.");
        Assert.True(normalized == (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n"),
            "The API contract changed. Review the change, regenerate with TEAMBEATS_UPDATE_OPENAPI=1 and commit docs/api/openapi.v1.json.");
        Assert.DoesNotContain("/dev/", normalized);
    }
}
