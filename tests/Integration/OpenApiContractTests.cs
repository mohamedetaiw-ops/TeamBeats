using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// OpenAPI diff gate (r0-solution-structure section 8). The committed snapshots are the reviewed contracts, one for the
/// internal web API and one for the public API (spec 035 FR-014); any change to them shows up in the pull request diff. Regenerate with TEAMBEATS_UPDATE_OPENAPI=1.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class OpenApiContractTests(TenantWorld world)
{
    private static string SnapshotPath(string file = "openapi.v1.json")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TeamBeats.slnx")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "docs", "api", file);
    }

    [Theory]
    [InlineData("v1", "openapi.v1.json")]
    [InlineData("public-v1", "openapi.public.v1.json")]
    public async Task OpenApi_document_matches_committed_snapshot(string document, string file)
    {
        var json = await world.Factory.TenantClient(world.A.Key).GetStringAsync($"/openapi/{document}.json");
        var node = JsonNode.Parse(json)!;
        node["servers"]?.AsArray().Clear();
        // WriteIndented uses the platform line ending; the snapshot is always LF.
        var normalized = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        var path = SnapshotPath(file);

        if (Environment.GetEnvironmentVariable("TEAMBEATS_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, normalized);
        }

        Assert.True(File.Exists(path), $"Missing docs/api/{file}. Run the tests with TEAMBEATS_UPDATE_OPENAPI=1 and commit the file.");
        Assert.True(normalized == (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n"),
            $"The API contract changed. Review the change, regenerate with TEAMBEATS_UPDATE_OPENAPI=1 and commit docs/api/{file}.");
        Assert.DoesNotContain("/dev/", normalized);
        // The internal web API document has no public operation, and the public document only public ones (FR-014).
        var paths = node["paths"]!.AsObject().Select(p => p.Key).ToList();
        if (document == "v1")
        {
            Assert.DoesNotContain(paths, p => p.StartsWith("/api/public", StringComparison.Ordinal));
        }
        else
        {
            Assert.NotEmpty(paths);
            Assert.All(paths, p => Assert.StartsWith("/api/public/v1/", p, StringComparison.Ordinal));
        }
    }
}
