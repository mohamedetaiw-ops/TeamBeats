namespace TeamBeats.Primitives;

/// <summary>Ambient correlation ID that spans HTTP, messages and audit (constitution XIX).</summary>
public static class Correlation
{
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>The request ID of the public API, echoed in every public answer and the same as the correlation ID.</summary>
    public const string RequestIdHeader = "X-Request-Id";
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Id
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");
}
