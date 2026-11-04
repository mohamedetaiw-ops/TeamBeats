namespace TeamBeats.Primitives;

public static class Ids
{
    /// <summary>Time-ordered identifiers keep B-tree inserts cheap.</summary>
    public static Guid New() => Guid.CreateVersion7();
}
