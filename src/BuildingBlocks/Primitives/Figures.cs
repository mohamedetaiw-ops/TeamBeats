namespace TeamBeats.Primitives;

/// <summary>
/// A counted group a module hands to analytics (spec 021 FR-002): its key (an ISO week start, a stable key or an ID), the
/// count, and how many distinct people it covers, so analytics can hide it behind the minimum group size. Names are in
/// each language when the group has them, or one <see cref="Label"/> in the language it was written in, such as a survey
/// title; <see cref="Amount"/> carries a second figure such as days of leave.
/// </summary>
public sealed record GroupFigure(string Key, long Count, long People, IReadOnlyDictionary<string, string>? Names = null, double? Amount = null,
    string? Label = null);
