namespace TeamBeats.Primitives;

/// <summary>
/// An expected failure that maps to a Problem Details response. Messages are resource keys,
/// never user-facing text (constitution XXIII).
/// </summary>
public sealed class AppException : Exception
{
    public AppException(string code, int status, string? detailKey = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
        : base(code)
    {
        Code = code;
        Status = status;
        DetailKey = detailKey;
        Errors = errors;
        Extensions = extensions;
    }

    public string Code { get; }
    public int Status { get; }
    public string? DetailKey { get; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; }
    public IReadOnlyDictionary<string, object?>? Extensions { get; }

    /// <summary>Not found. Also used for resources that exist but are not visible, so existence is never revealed.</summary>
    public static AppException NotFound() => new(ProblemCodes.NotFound, 404);

    public static AppException Unauthenticated() => new(ProblemCodes.Unauthenticated, 401);

    public static AppException Denied(string? detailKey = null) => new(ProblemCodes.AuthzDenied, 403, detailKey);

    public static AppException Conflict(string detailKey) => new(ProblemCodes.Conflict, 409, detailKey);

    public static AppException NotInPackage(string capabilityKey) =>
        new(ProblemCodes.EntitlementNotInPackage, 403, null, null,
            new Dictionary<string, object?> { ["capability"] = capabilityKey });

    public static AppException Validation(string field, string errorKey) =>
        new(ProblemCodes.ValidationFailed, 400, null,
            new Dictionary<string, string[]> { [field] = [errorKey] });

    public static AppException Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(ProblemCodes.ValidationFailed, 400, null, errors);
}

/// <summary>Collects validation errors as resource keys.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public void Add(string field, string key)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            list = [];
            _errors[field] = list;
        }
        list.Add(key);
    }

    public bool Any => _errors.Count > 0;

    /// <summary>How many errors were added, over all fields.</summary>
    public int Count => _errors.Values.Sum(v => v.Count);

    /// <summary>Every error as a field and resource key, in the order fields were first added.</summary>
    public IEnumerable<(string Field, string Key)> Entries => _errors.SelectMany(e => e.Value.Select(k => (e.Key, k)));

    public void ThrowIfAny()
    {
        if (Any)
        {
            throw AppException.Validation(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }
}
