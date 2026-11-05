namespace TeamBeats.Primitives;

/// <summary>Ambient correlation ID that spans HTTP, messages and audit (constitution XIX).</summary>
public static class Correlation
{
    public const string HeaderName = "X-Correlation-Id";
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Id
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");
}
