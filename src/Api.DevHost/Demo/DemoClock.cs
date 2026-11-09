using TeamBeats.Primitives;

namespace TeamBeats.Api.DevHost.Demo;

/// <summary>
/// The development host's clock: the system time, except inside <see cref="Back"/>, where it runs the given number of
/// days behind for the calls in that flow only. The demo data seeder uses it so news, requests and decisions carry
/// dates spread over the past weeks instead of all showing the minute the host started. Nothing else moves it.
/// </summary>
public sealed class DemoClock : IClock
{
    private static readonly AsyncLocal<TimeSpan?> Offset = new();

    public DateTimeOffset UtcNow => Offset.Value is { } offset ? DateTimeOffset.UtcNow - offset : DateTimeOffset.UtcNow;

    /// <summary>Runs the work with the clock set back by the given days and hours; the clock is back to normal afterwards.</summary>
    public static async Task<T> Back<T>(double days, Func<Task<T>> work)
    {
        var before = Offset.Value;
        Offset.Value = days > 0 ? TimeSpan.FromDays(days) : null;
        try
        {
            return await work();
        }
        finally
        {
            Offset.Value = before;
        }
    }
}
