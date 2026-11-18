using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 008 T020 (FR-008, US4 scenario 8): the command entry point lists only commands whose capability is on for the
/// tenant and whose permission the viewer holds. Booking a room needs the booking capability and booking.book; asking the
/// assistant, requesting leave, a new request and my tasks need only their capability.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ShellCommandTests(TenantWorld world)
{
    private static async Task<string[]> KeysAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/experience/commands")).EnumerateArray().Select(c => c.GetProperty("key").GetString()!).ToArray();

    [Fact]
    public async Task Commands_need_their_capability_and_permission()
    {
        var plus = await world.CreateTenantAsync("cmd-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        (await plus["admin"].Client.PutAsJsonAsync("/api/v1/tenant/membership-policies/contractor", new { defaultEmployeeRole = false })).EnsureSuccessStatusCode();
        await world.SeedUserAsync(plus, "contractor", "contractor");

        var admin = await KeysAsync(plus["admin"].Client);
        Assert.Equal(["ask_assistant", "book_room", "my_tasks", "new_request", "open_analytics", "request_leave"], admin.Order(StringComparer.Ordinal));
        var first = (await plus["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/commands")).EnumerateArray().Single(c => c.GetProperty("key").GetString() == "book_room");
        Assert.Equal("/booking", first.GetProperty("href").GetString());

        // A contractor without booking.book never sees the booking command.
        var contractor = await KeysAsync(plus["contractor"].Client);
        Assert.DoesNotContain("book_room", contractor);
        Assert.DoesNotContain("open_analytics", contractor); // Analytics needs analytics.view (spec 021 T010).
        Assert.Contains("ask_assistant", contractor);

        // Workplace has no booking, so even an administrator holding every permission does not see it.
        var basic = await world.CreateTenantAsync("cmd-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var keys = await KeysAsync(basic["admin"].Client);
        Assert.DoesNotContain("book_room", keys);
        Assert.Contains("request_leave", keys);
    }
}
