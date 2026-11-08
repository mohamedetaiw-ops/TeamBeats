using TeamBeats.Persistence;

namespace TeamBeats.Modules.Services;

/// <summary>Approver rule kinds of R1: named users, or holders of a permission at the requester's unit ("role at unit").</summary>
public static class ApproverKinds
{
    public const string Users = "users";
    public const string PermissionAtRequesterUnit = "permission_at_unit";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Users, PermissionAtRequesterUnit };
}

public sealed record ApproverRule(string Kind, IReadOnlyList<Guid>? Users = null, string? Permission = null);

public sealed record ApprovalStep(IReadOnlyDictionary<string, string>? Names, ApproverRule Approvers, int? DueWorkingDays = null);

/// <summary>A service's route. R1 has none or one approval step (US8: approval-only routes in R1).</summary>
public sealed record ServiceRoute(ApprovalStep? Approval = null)
{
    public static readonly ServiceRoute None = new();
}

public sealed record VersionRow(
    int Version,
    string State,
    FormDefinition Form,
    ServiceRoute Route,
    Guid? FulfilmentQueueGroupId,
    bool AllowOnBehalf,
    int MaxAttachments,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt);

internal static class Versions
{
    private const string Columns = "version, state, form::text, route::text, fulfilment_queue_group_id, allow_on_behalf, max_attachments, updated_at, published_at";

    private static VersionRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetInt32(0), r.GetString(1), Catalogue.Json<FormDefinition>(r.GetString(2)), Catalogue.Json<ServiceRoute>(r.GetString(3)),
        r.Get<Guid?>("fulfilment_queue_group_id"), r.GetBoolean(5), r.GetInt32(6), r.GetFieldValue<DateTimeOffset>(7), r.Get<DateTimeOffset?>("published_at"));

    public static Task<VersionRow?> GetAsync(IDbSession session, Guid serviceId, int version, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from services.service_version where service_id = @serviceId and version = @version",
            new { serviceId, version }, Read, ct);

    public static Task<VersionRow?> LatestAsync(IDbSession session, Guid serviceId, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            $"select {Columns} from services.service_version where service_id = @serviceId order by version desc limit 1" + (forUpdate ? " for update" : ""),
            new { serviceId }, Read, ct);
}
