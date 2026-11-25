using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>
/// The named HR packs of spec 019 R3 (US8, FR-019): Oracle HCM Cloud and SAP SuccessFactors as presets of the HR REST
/// pack. The administrator gives the address, the sign-in and the schedule; the preset gives the paths, the paging, the
/// status words and the field mapping, and one field at a time may be overridden (an empty source removes it). What is
/// stored is a full HR REST settings document checked by the REST pack, so sync, live reads, payslips, leave and data
/// changes all run through one code path and HR DTOs never leave the pack.
/// </summary>
public abstract class HrNamedPack(HrRestPack rest) : IConnectorPack
{
    public abstract string Kind { get; }

    public int Version => 1;

    public string CapabilityKey => Capabilities.HrNamedPacks;

    public IReadOnlyList<ConnectorActionDefinition> Actions => rest.Actions;

    /// <summary>The preset: every <see cref="HrRestSettings"/> value a tenant does not choose itself.</summary>
    protected abstract HrRestSettings Preset { get; }

    public Task<IReadOnlyList<ConnectorActionDefinition>> ActionsForAsync(PackConnection connection, CancellationToken ct) => rest.ActionsForAsync(connection, ct);

    public Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct) => rest.CallAsync(call, ct);

    public DateOnly? CredentialExpiry(JsonElement settings) => rest.CredentialExpiry(settings);

    public Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct) => rest.TestAsync(connection, ct);

    public Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct) => rest.ValidateSettingsAsync(Apply(settings), ct);

    /// <summary>
    /// The tenant's settings over the preset. The paths and paging always come from the preset; the address, sign-in,
    /// schedule, date format, page size, status words and the optional payslip, leave and data change settings come from
    /// the tenant when given; fields are merged one by one.
    /// </summary>
    public JsonElement Apply(JsonElement settings)
    {
        var preset = JsonSerializer.SerializeToNode(Preset, JsonDefaults.Options)!.AsObject();
        if (settings.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.SerializeToElement(preset);
        }
        var given = JsonNode.Parse(settings.GetRawText())!.AsObject();
        foreach (var (name, value) in given)
        {
            if (Fixed.Contains(name) || value is null)
            {
                continue;
            }
            if (name == "fields" && value is JsonObject fields)
            {
                var merged = preset["fields"]?.AsObject() ?? [];
                foreach (var (field, source) in fields)
                {
                    var text = source is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : null;
                    if (string.IsNullOrEmpty(text))
                    {
                        merged.Remove(field);
                    }
                    else
                    {
                        merged[field] = text;
                    }
                }
                preset["fields"] = merged.DeepClone();
                continue;
            }
            preset[name] = value.DeepClone();
        }
        return JsonSerializer.SerializeToElement(preset);
    }

    /// <summary>What the preset decides alone, so a tenant cannot point a named pack at another address shape.</summary>
    private static readonly HashSet<string> Fixed = new(StringComparer.Ordinal)
    {
        "employeesPath", "recordsPath", "paging", "pageParam", "pageSizeParam", "nextPath", "employeePath", "employeeRecordPath",
    };

    /// <summary>The named kinds and the generic REST kind: every kind whose settings are HR REST settings.</summary>
    public static readonly IReadOnlyList<string> RestKinds = [HrRestPack.KindName, HrOracleHcmPack.KindName, HrSuccessFactorsPack.KindName];

    public static bool IsRest(string kind) => RestKinds.Contains(kind);
}

/// <summary>
/// Oracle HCM Cloud: the <c>workers</c> resource of the HCM REST API with names, emails, work relationships, assignments
/// and their managers expanded, read with offset and limit. The first of each list is used (Oracle lists the primary
/// first); the record ID is the person ID and the employee number the person number, and the manager is named by person
/// number, so manager chains match. Inactive assignments are read too, so leavers are seen (FR-020).
/// </summary>
public sealed class HrOracleHcmPack(HrRestPack rest) : HrNamedPack(rest)
{
    public const string KindName = "hr_oracle_hcm";
    public const string Resource = "/hcmRestApi/resources/11.13.18.05/workers";
    private const string Expand = "onlyData=true&expand=names,emails,workRelationships.assignments.managers";
    private const string Assignment = "$.workRelationships[0].assignments[0]";

    public override string Kind => KindName;

    protected override HrRestSettings Preset { get; } = new(
        BaseUrl: null,
        Auth: null,
        EmployeesPath: $"{Resource}?{Expand}",
        RecordsPath: "$.items",
        Paging: "offset",
        PageParam: "offset",
        PageSizeParam: "limit",
        PageSize: 200,
        NextPath: null,
        Fields: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HrFields.RecordId] = "$.PersonId",
            [HrFields.EmployeeNumber] = "$.PersonNumber",
            [HrFields.WorkEmail] = "$.emails[0].EmailAddress",
            [HrFields.NameEn] = "$.names[0].DisplayName",
            [HrFields.Status] = $"{Assignment}.AssignmentStatusType",
            [HrFields.HireDate] = "$.workRelationships[0].StartDate",
            [HrFields.TerminationDate] = "$.workRelationships[0].TerminationDate",
            [HrFields.EmploymentType] = "$.workRelationships[0].WorkerType",
            [HrFields.JobTitle] = $"{Assignment}.AssignmentName",
            [HrFields.Grade] = $"{Assignment}.GradeCode",
            [HrFields.Location] = $"{Assignment}.LocationCode",
            [HrFields.OrgUnitCode] = $"{Assignment}.DepartmentName",
            [HrFields.AssignmentDate] = $"{Assignment}.EffectiveStartDate",
            [HrFields.ManagerEmployeeNumber] = $"{Assignment}.managers[0].ManagerPersonNumber",
        },
        StatusValues: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            // A suspended assignment is still employment.
            [HrEmployeeStatuses.Active] = ["ACTIVE", "SUSPENDED"],
            [HrEmployeeStatuses.Terminated] = ["INACTIVE"],
        },
        DateFormat: null,
        SyncAt: null,
        TimeZone: null,
        EmployeePath: $"{Resource}?{Expand}&q=PersonId={{record_id}}",
        EmployeeRecordPath: "$.items[0]");
}

/// <summary>
/// SAP SuccessFactors: the <c>User</c> entity of the OData v2 API in JSON, with the manager expanded, read with the next
/// links SuccessFactors returns. The filter asks for inactive users as well as active ones, which SuccessFactors leaves out
/// by default (FR-020). Dates come as <c>/Date(ms)/</c> and are read by <see cref="HrMapping"/>. Compensation comes from
/// <c>EmpPayCompRecurring</c>. Oracle HCM has no preset for compensation: salaries there follow each tenant's salary
/// basis, so a tenant gives the compensation settings itself.
/// </summary>
public sealed class HrSuccessFactorsPack(HrRestPack rest) : HrNamedPack(rest)
{
    public const string KindName = "hr_sap_successfactors";
    private const string Select =
        "$format=json&$expand=manager&$select=userId,empId,email,displayName,status,hireDate,title,department,location,division,manager/empId";

    public override string Kind => KindName;

    protected override HrRestSettings Preset { get; } = new(
        BaseUrl: null,
        Auth: null,
        EmployeesPath: $"/odata/v2/User?{Select}&$filter=status%20in%20't','f','T','F'",
        RecordsPath: "$.d.results",
        Paging: "next",
        PageParam: null,
        PageSizeParam: null,
        PageSize: null,
        NextPath: "$.d.__next",
        Fields: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HrFields.RecordId] = "$.userId",
            [HrFields.EmployeeNumber] = "$.empId",
            [HrFields.WorkEmail] = "$.email",
            [HrFields.NameEn] = "$.displayName",
            [HrFields.Status] = "$.status",
            [HrFields.HireDate] = "$.hireDate",
            [HrFields.JobTitle] = "$.title",
            [HrFields.OrgUnitCode] = "$.department",
            [HrFields.Location] = "$.location",
            [HrFields.CostCentre] = "$.division",
            [HrFields.ManagerEmployeeNumber] = "$.manager.empId",
        },
        StatusValues: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [HrEmployeeStatuses.Active] = ["t", "active"],
            [HrEmployeeStatuses.Terminated] = ["f", "inactive"],
        },
        DateFormat: null,
        SyncAt: null,
        TimeZone: null,
        EmployeePath: $"/odata/v2/User('{{record_id}}')?{Select}",
        EmployeeRecordPath: "$.d",
        // Recurring pay components as of today (US10); the tenant names its basic salary code.
        CompensationPath: "/odata/v2/EmpPayCompRecurring?$format=json&$filter=userId%20eq%20'{record_id}'",
        CompensationRecordsPath: "$.d.results",
        CompensationFields: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = "$.payComponent", ["amount"] = "$.paycompvalue", ["currency"] = "$.currencyCode", ["frequency"] = "$.frequency", ["start_date"] = "$.startDate",
        });
}
