using System.Text.Json;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

/// <summary>Request statuses of the shared lifecycle (spec 016 scope).</summary>
public static class RequestStatuses
{
    public const string Submitted = "submitted";
    public const string InApproval = "in_approval";
    public const string Returned = "returned";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Completed = "completed";

    /// <summary>Statuses in which the requester may still cancel (section 6: until the first approval).</summary>
    public static readonly IReadOnlySet<string> Cancellable = new HashSet<string> { Submitted, InApproval, Returned };

    /// <summary>Statuses a request ends in, as <c>RequestCompleted</c> carries them.</summary>
    public static readonly IReadOnlySet<string> Closed = new HashSet<string> { Approved, Rejected, Cancelled, Completed };
}

/// <summary>A new request. <paramref name="PreparedId"/> names the agent-prepared draft it was made from (spec 020 US8), which then leaves the inbox.</summary>
public sealed record RequestSubmit(Guid ServiceId, Dictionary<string, JsonElement>? Data, Guid? OnBehalfOf, string? IdempotencyKey, Guid? PreparedId = null);

public sealed record RequestResubmit(Dictionary<string, JsonElement>? Data);

/// <summary>Starting values of a form for one person, and which of them come from the directory or the account and stay read-only.</summary>
/// <summary>
/// Prefilled values, the fields that keep their value, and the HR fields that must be current but could not be
/// confirmed with the HR system (spec 019 US5 scenario 3).
/// </summary>
public sealed record PrefillView(IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<string> ReadOnly, IReadOnlyList<string> Unconfirmed);

public sealed record PersonRef(Guid Id, string DisplayName);

/// <summary>The latest version of a service as the owner will see it as a requester, before publishing (US7 scenario 1).</summary>
public sealed record ServicePreview(int Version, string State, FormDefinition Form, PrefillView Prefill, IReadOnlyDictionary<string, string>? ApprovalStep,
    bool AllowOnBehalf, int MaxAttachments);

public sealed record RequestSummary(
    Guid Id,
    string Number,
    Guid ServiceId,
    IReadOnlyDictionary<string, string> ServiceNames,
    string Status,
    PersonRef Requester,
    PersonRef? OnBehalfOf,
    IReadOnlyDictionary<string, string>? CurrentStep,
    IReadOnlyList<PersonRef> Assignees,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ClosedAt);

public sealed record TrackingEntry(string Event, string? Step, PersonRef? Actor, PersonRef? OnBehalfOf, string? Comment, DateTimeOffset At);

public sealed record AttachmentView(Guid Id, string FileName, long SizeBytes, string ContentType, string ScanState);

public sealed record RequestDetail(RequestSummary Request, int ServiceVersion, FormDefinition Form, IReadOnlyDictionary<string, JsonElement> Data,
    string Classification, IReadOnlyList<AttachmentView> Attachments, IReadOnlyList<TrackingEntry> Tracking, RequestAutomation? Automation = null);

/// <summary>A row of <c>services.request</c>.</summary>
internal sealed record RequestRow(
    Guid Id, string Number, Guid ServiceId, int ServiceVersion, Guid RequesterId, Guid? OnBehalfOfId, Guid? OrgUnitId, string Status,
    string Data, string Classification, Guid? InstanceId, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt, DateTimeOffset? ClosedAt, Guid? RequesterAppId = null)
{
    public Guid SubjectId => OnBehalfOfId ?? RequesterId;
}

/// <summary>
/// Requests of spec 016 US2: prefilled forms, the same validation as the builder, submit once per idempotency key,
/// My requests with status, step and assignee, Details and Tracking, and cancel. A request with an approval step starts
/// an instance on the process engine; nobody approves a request filed by or for them.
/// </summary>
public sealed class Requests(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    IProfiles profiles,
    IReportingLine reportingLine,
    IWorkingCalendar calendars,
    IProcessEngine engine,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    Catalogue catalogue,
    ServiceAdmin admin,
    Attachments attachments,
    IHrRecords hr,
    IEntitlementService entitlements)
{
    public const int MaxIdempotencyKeyLength = 100;

    /// <summary>HR values older than this are read again from the HR system when a field must be current (US5 scenario 3).</summary>
    public static readonly TimeSpan HrCurrentFor = TimeSpan.FromDays(7);

    private const string Columns =
        """
        r.id, r.number, r.service_id, r.service_version, r.requester_user_id, r.on_behalf_of_user_id, r.requester_org_unit_id, r.status, r.data::text,
        r.classification, r.process_instance_id, r.created_at, r.submitted_at, r.closed_at, r.requester_app_id
        """;

    private static RequestRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetInt32(3), r.GetGuid(4), r.Get<Guid?>("on_behalf_of_user_id"), r.Get<Guid?>("requester_org_unit_id"),
        r.GetString(7), r.GetString(8), r.GetString(9), r.Get<Guid?>("process_instance_id"), r.GetFieldValue<DateTimeOffset>(11),
        r.Get<DateTimeOffset?>("submitted_at"), r.Get<DateTimeOffset?>("closed_at"), r.Get<Guid?>("requester_app_id"));

    // ---- Prefill (FR-004) ----

    private async Task<Guid?> PrimaryUnitAsync(Guid user, CancellationToken ct)
    {
        var memberships = await hierarchy.GetActiveMembershipsAsync(user, ct);
        return (memberships.FirstOrDefault(m => m.IsPrimary) ?? memberships.FirstOrDefault())?.OrgUnitId;
    }

    /// <summary>
    /// Values from the person's account, profile, reporting line and units. Account and directory values, the manager
    /// and the units are read-only; values the person or an administrator keeps stay editable.
    /// </summary>
    public async Task<PrefillView> PrefillAsync(Guid serviceId, Guid? forUser, CancellationToken ct)
    {
        var detail = await catalogue.DetailAsync(serviceId, ct);
        var subject = await SubjectAsync(forUser, detail.AllowOnBehalf, ct);
        var p = await PrefillValuesAsync(detail.Form, subject, detail.Classification, ct);
        return new PrefillView(p.Values, p.ReadOnly.ToList(), p.Unconfirmed.ToList());
    }

    private sealed record Prefilled(Dictionary<string, JsonElement> Values, HashSet<string> ReadOnly, HashSet<string> Unconfirmed);

    private async Task<Prefilled> PrefillValuesAsync(FormDefinition form, Guid subject, string classification, CancellationToken ct)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var readOnly = new HashSet<string>(StringComparer.Ordinal);
        var unconfirmed = new HashSet<string>(StringComparer.Ordinal);
        var fields = form.Fields.Where(f => f.Prefill is not null).ToList();
        if (fields.Count == 0)
        {
            return new Prefilled(values, readOnly, unconfirmed);
        }
        var hrFields = fields.Where(f => PrefillSources.IsHr(f.Prefill)).ToList();
        foreach (var field in hrFields.Where(f => f.ReadOnly))
        {
            readOnly.Add(field.Key);
        }
        if (hrFields.Count > 0)
        {
            await HrValuesAsync(hrFields, subject, classification, values, unconfirmed, ct);
            fields = fields.Except(hrFields).ToList();
        }
        var account = await users.GetAsync(subject, ct);
        // The requester sees the subject's profile as they may see it, never more.
        var profile = (await profiles.ViewAsync(context.RequirePrincipal(), [subject], ct)).FirstOrDefault();
        Guid? unit = null;
        IReadOnlyList<Guid>? chain = null;
        foreach (var field in fields)
        {
            (object? Value, bool Locked) source = field.Prefill switch
            {
                PrefillSources.DisplayName => (account?.DisplayName, true),
                PrefillSources.Email => (account?.Email, true),
                PrefillSources.Manager => ((await reportingLine.ManagerOfAsync(subject, ct))?.ToString(), true),
                PrefillSources.PrimaryUnit => ((unit ??= await PrimaryUnitAsync(subject, ct))?.ToString(), true),
                PrefillSources.Company => await CompanyAsync(),
                _ => ProfileValue(profile, field.Prefill!),
            };
            if (source.Value is { } value)
            {
                values[field.Key] = JsonSerializer.SerializeToElement(value);
            }
            if (source.Locked)
            {
                readOnly.Add(field.Key);
            }
        }
        return new Prefilled(values, readOnly, unconfirmed);

        async Task<(object?, bool)> CompanyAsync()
        {
            unit ??= await PrimaryUnitAsync(subject, ct);
            if (unit is not { } u)
            {
                return (null, true);
            }
            chain ??= await hierarchy.GetAncestorsAndSelfAsync(u, ct); // nearest first
            return ((chain.Count > 1 ? chain[^2] : chain[0]).ToString(), true);
        }
    }

    /// <summary>
    /// HR values (spec 019 US5, FR-009). Only for the person's own form: HR fields of someone else are for HR staff in the
    /// HR system. A Confidential field fills only a Confidential or Restricted form. A field that must be current and was
    /// not confirmed by HR in the last 7 days is read now; when that fails it stays empty and is listed as unconfirmed.
    /// </summary>
    private async Task HrValuesAsync(List<FormField> fields, Guid subject, string classification, Dictionary<string, JsonElement> values,
        HashSet<string> unconfirmed, CancellationToken ct)
    {
        if (context.ActorType != ActorType.User || subject != context.ActorId || !await entitlements.IsEnabledAsync(Capabilities.HrFormPrefill, ct))
        {
            return;
        }
        var rank = ConditionAttributes.Classifications.ToList().IndexOf(classification);
        var allowed = fields.Where(f => !PrefillSources.Confidential.Contains(f.Prefill!) || rank >= 2).ToList();
        if (allowed.Count == 0)
        {
            return;
        }
        var record = (await hr.MatchedAsync([subject], ct)).FirstOrDefault();
        HrRecordRead? live = null;
        foreach (var field in allowed)
        {
            var source = record;
            if (field.MustBeCurrent && (record?.SeenAt is not { } seen || clock.UtcNow - seen > HrCurrentFor))
            {
                live ??= record is null ? new HrRecordRead(HrRecordRead.NotMatched, null) : await hr.CurrentAsync(subject, ct);
                source = live.Record;
                if (source is null)
                {
                    unconfirmed.Add(field.Key);
                    continue;
                }
            }
            if (source?.Fields.TryGetValue(PrefillSources.Hr[field.Prefill!], out var value) == true)
            {
                values[field.Key] = JsonSerializer.SerializeToElement(value);
            }
        }
    }

    private static (object? Value, bool Locked) ProfileValue(VisibleProfile? profile, string source)
    {
        var key = source switch
        {
            PrefillSources.JobTitle => "jobTitle",
            PrefillSources.Department => "department",
            PrefillSources.Office => "office",
            PrefillSources.WorkPhone => "workPhone",
            PrefillSources.MobilePhone => "mobilePhone",
            PrefillSources.EmployeeNumber => "employeeNumber",
            _ => null,
        };
        var field = key is null ? null : profile?.Fields.FirstOrDefault(f => f.Key == key);
        return (field?.Value, field?.Source == "directory");
    }

    /// <summary>
    /// Previews the latest version of a form service, draft or published, prefilled for the owner. Only owners of the
    /// service may preview it; nothing is stored.
    /// </summary>
    public async Task<ServicePreview> PreviewAsync(Guid serviceId, CancellationToken ct)
    {
        var latest = await admin.LatestManagedAsync(serviceId, ct);
        var classification = await session.ScalarAsync<string>("select classification from services.service where id = @serviceId", new { serviceId }, ct) ?? "internal";
        var p = await PrefillValuesAsync(latest.Form, context.RequireUser(), classification, ct);
        return new ServicePreview(latest.Version, latest.State, latest.Form, new PrefillView(p.Values, p.ReadOnly.ToList(), p.Unconfirmed.ToList()), latest.Route.Approval?.Names,
            latest.AllowOnBehalf, latest.MaxAttachments);
    }

    /// <summary>
    /// The person a request is for: the caller, or someone else when the service allows it and the caller holds
    /// <c>service.request_on_behalf</c> at that person's unit (section 3).
    /// </summary>
    private async Task<Guid> SubjectAsync(Guid? forUser, bool allowOnBehalf, CancellationToken ct)
    {
        var user = context.RequirePrincipal();
        if (context.ActorType == ActorType.App && forUser is null)
        {
            // An application app is nobody: it always files for a named person (spec 035 requests:submit).
            throw AppException.Validation("onBehalfOf", "validation.services.person");
        }
        if (forUser is not { } other || other == user)
        {
            return user;
        }
        if (!allowOnBehalf)
        {
            throw AppException.Validation("onBehalfOf", "validation.services.on_behalf_not_allowed");
        }
        if (await users.GetAsync(other, ct) is not { Status: "active" })
        {
            throw AppException.Validation("onBehalfOf", "validation.services.person");
        }
        var unit = await PrimaryUnitAsync(other, ct);
        await authz.DemandAsync(Permissions.ServiceRequestOnBehalf, unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct);
        return other;
    }

    // ---- Submit ----

    private async Task<string> NextNumberAsync(CancellationToken ct)
    {
        var year = clock.UtcNow.Year;
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':services.request_number', 0))", null, ct);
        var count = await session.ScalarAsync<long>("select count(*) from services.request where number like @prefix", new { prefix = $"REQ-{year}-%" }, ct);
        return $"REQ-{year}-{count + 1:D6}";
    }

    /// <summary>Submits a request; a second submit with the same idempotency key returns the first request.</summary>
    /// <remarks>
    /// An application app (spec 035 requests:submit) files for one named person, with <c>service.request_on_behalf</c> at
    /// that person's unit from its own role. The person becomes the requester, as if they had filed it, so it is theirs to
    /// follow and cancel; the request keeps the app that filed it, and the audit names the app as the actor.
    /// </remarks>
    public async Task<(RequestDetail Request, bool Created)> SubmitAsync(RequestSubmit request, CancellationToken ct)
    {
        var app = context.ActorType == ActorType.App ? context.ActorId : null;
        var user = app is null ? context.RequireUser() : Guid.Empty;
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        if (app is not null)
        {
            // The public API replays a repeated Idempotency-Key itself; the key here belongs to the person's own submits.
            key = null;
        }
        if (key is { Length: > MaxIdempotencyKeyLength })
        {
            throw AppException.Validation("idempotencyKey", "validation.services.idempotency_key");
        }
        if (key is not null && await session.ScalarAsync<Guid?>("select id from services.request where requester_user_id = @user and idempotency_key = @key",
            new { user, key }, ct) is { } existing)
        {
            return (await DetailAsync(existing, ct), false);
        }

        var (row, _) = await catalogue.VisibleAsync(request.ServiceId, ct);
        if (row.Kind != ServiceKinds.Form || row.CurrentVersion is not { } version)
        {
            throw AppException.Validation("serviceId", "validation.services.not_form");
        }
        var published = (await Versions.GetAsync(session, row.Id, version, ct))!;
        var subject = await SubjectAsync(request.OnBehalfOf, published.AllowOnBehalf, ct);
        if (subject != user && !(await catalogue.FactsAsync(subject, ct)).Matches(row.Audience))
        {
            throw AppException.Validation("onBehalfOf", "validation.services.not_in_audience");
        }
        if (app is not null)
        {
            user = subject;
        }

        var now = clock.UtcNow;
        var (data, references) = await PrepareAsync(published, subject, row.Classification, request.Data, null, ct);

        var id = Ids.New();
        var number = await NextNumberAsync(ct);
        var unit = await PrimaryUnitAsync(subject, ct);
        var inserted = await session.ScalarAsync<Guid?>(
            """
            insert into services.request (tenant_id, id, number, service_id, service_version, requester_user_id, on_behalf_of_user_id, requester_org_unit_id,
              status, data, classification, idempotency_key, created_at, submitted_at, requester_app_id)
            values (platform.current_tenant_id(), @id, @number, @service, @version, @user, @onBehalf, @unit, @status, @data, @classification, @key, @now, @now, @app)
            on conflict (tenant_id, requester_user_id, idempotency_key) where idempotency_key is not null do nothing
            returning id
            """, new
            {
                id, number, service = row.Id, version, user, onBehalf = subject == user ? (Guid?)null : subject, unit,
                status = published.Route.Approval is null && published.Route.Graph is null ? RequestStatuses.Submitted : RequestStatuses.InApproval,
                data = Jsonb.From(data), classification = row.Classification, key, now, app,
            }, ct);
        if (inserted is null)
        {
            // Two submits with one key at the same moment: the other one won.
            var winner = await session.ScalarAsync<Guid>("select id from services.request where requester_user_id = @user and idempotency_key = @key", new { user, key }, ct);
            return (await DetailAsync(winner, ct), false);
        }
        await attachments.LinkAsync(id, references.Attachments, ct);
        await EventAsync(id, "submitted", null, app is null ? user : null, subject == user ? null : subject, null, ct);
        if (published.Route.HasSteps)
        {
            await StartRouteAsync(id, row.Id, unit, user, subject, 1, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.RequestSubmitted, "request", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { service = row.Id, version, number, onBehalf = subject != user || app is not null, forUser = app is null ? (Guid?)null : subject }), ct);
        await outbox.EnqueueAsync(new RequestSubmitted(id, row.Id, number), ct);
        if (app is null)
        {
            await catalogue.MarkUsedAsync(row.Id, ct);
        }
        return (await DetailAsync(id, ct), true);
    }

    /// <summary>
    /// Validates data against a published version for the person a request is for. Read-only prefilled values always come
    /// from their source, whatever the client sent; dates compare with today on that person's calendar.
    /// </summary>
    private async Task<(Dictionary<string, JsonElement> Data, FormReferences References)> PrepareAsync(VersionRow published, Guid subject,
        string classification, Dictionary<string, JsonElement>? sent, Guid? existingRequest, CancellationToken ct)
    {
        var input = new Dictionary<string, JsonElement>(sent ?? [], StringComparer.Ordinal);
        var p = await PrefillValuesAsync(published.Form, subject, classification, ct);
        var prefilled = p.Values;
        foreach (var field in p.ReadOnly)
        {
            if (prefilled.TryGetValue(field, out var value))
            {
                input[field] = value;
            }
            else
            {
                input.Remove(field);
            }
        }

        var today = (await calendars.ForUserAsync(subject, ct)).Today(clock.UtcNow);
        var errors = new ValidationErrors();
        var (data, references) = FormValidator.ValidateData(published.Form, input, today, errors);
        // A read-only value HR could not confirm is not taken from the browser either (US5 scenario 3).
        foreach (var field in p.Unconfirmed.Where(p.ReadOnly.Contains))
        {
            errors.Add($"data.{field}", "validation.services.prefill_unconfirmed");
        }
        await CheckReferencesAsync(references, published.MaxAttachments, existingRequest, errors, ct);
        errors.ThrowIfAny();
        return (data, references);
    }

    /// <summary>
    /// Starts the approval route of a request. The business key is the request ID, with the round added after a return,
    /// so each submission has its own instance and history.
    /// </summary>
    private async Task StartRouteAsync(Guid id, Guid serviceId, Guid? unit, Guid filer, Guid subject, int round, CancellationToken ct)
    {
        var businessKey = round == 1 ? id.ToString("N") : $"{id:N}.{round}";
        // Nobody approves their own request (US4 scenario 2): the filer and the person it is for are kept off its steps.
        var instance = await engine.StartAsync(ServiceAdmin.ProcessKey(serviceId), businessKey, unit, filer, subject == filer ? [filer] : [filer, subject], subject,
            await VariablesAsync(id, subject, ct), ct);
        await session.ExecAsync("update services.request set process_instance_id = @instance where id = @id", new { id, instance = instance.Id }, ct);
    }

    /// <summary>
    /// The values a connector step may map (spec 016 FR-016): each form field as <c>form.{id}</c>, the request number,
    /// and the requester's name, email and employee ID. A step sends only what its owner mapped.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, JsonElement>> VariablesAsync(Guid id, Guid subject, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select number, data::text from services.request where id = @id", new { id },
            r => (Number: r.GetString(0), Data: r.GetString(1)), ct);
        var variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["request.number"] = JsonSerializer.SerializeToElement(row.Number),
        };
        using (var data = JsonDocument.Parse(row.Data ?? "{}"))
        {
            foreach (var field in data.RootElement.EnumerateObject())
            {
                variables["form." + field.Name] = field.Value.Clone();
            }
        }
        if (await users.GetAsync(subject, ct) is { } account)
        {
            variables["requester.name"] = JsonSerializer.SerializeToElement(account.DisplayName);
            if (account.Email is { } email)
            {
                variables["requester.email"] = JsonSerializer.SerializeToElement(email);
            }
        }
        // Step graph routes read the same values as objects (form.{field}, request.number, requester.name), so FEEL paths reach them.
        variables["form"] = JsonDocument.Parse(row.Data ?? "{}").RootElement.Clone();
        variables["request"] = JsonSerializer.SerializeToElement(new { number = row.Number });
        variables["requester"] = JsonSerializer.SerializeToElement(new Dictionary<string, string?>
        {
            ["name"] = variables.TryGetValue("requester.name", out var n) ? n.GetString() : null,
            ["email"] = variables.TryGetValue("requester.email", out var e) ? e.GetString() : null,
        });
        return variables;
    }

    // ---- Resubmit ----

    /// <summary>
    /// The person who filed a returned request changes it and submits it again (FR-005 lifecycle). It keeps its number
    /// and form version, and the approval route starts again; attachments left out of the new data are let go.
    /// </summary>
    public async Task<RequestDetail> ResubmitAsync(Guid id, RequestResubmit request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        if (row.RequesterId != user)
        {
            await VisibleRowAsync(id, ct);
            throw AppException.Denied();
        }
        if (row.Status != RequestStatuses.Returned)
        {
            throw AppException.Conflict("conflict.services.request_not_returned");
        }
        var service = await session.QuerySingleOrDefaultAsync("select status from services.service where id = @id", new { id = row.ServiceId }, r => r.GetString(0), ct);
        if (service != "published")
        {
            throw AppException.Conflict("conflict.services.service_retired");
        }
        var published = (await Versions.GetAsync(session, row.ServiceId, row.ServiceVersion, ct))!;
        var (data, references) = await PrepareAsync(published, row.SubjectId, row.Classification, request.Data, id, ct);

        await session.ExecAsync("update services.request set data = @data, status = 'in_approval', submitted_at = @now where id = @id",
            new { id, data = Jsonb.From(data), now = clock.UtcNow }, ct);
        await attachments.ReleaseAsync(id, references.Attachments, ct);
        await attachments.LinkAsync(id, references.Attachments, ct);
        await EventAsync(id, "resubmitted", null, user, row.OnBehalfOfId, null, ct);
        var round = 1 + await session.ScalarAsync<long>("select count(*) from services.request_event where request_id = @id and event = 'resubmitted'", new { id }, ct);
        await StartRouteAsync(id, row.ServiceId, row.OrgUnitId, user, row.SubjectId, (int)round, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RequestResubmitted, "request", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { service = row.ServiceId, row.Number, round }), ct);
        await outbox.EnqueueAsync(new RequestResubmitted(id, row.ServiceId, row.Number), ct);
        return await DetailAsync(id, ct);
    }

    private async Task CheckReferencesAsync(FormReferences references, int maxAttachments, Guid? existingRequest, ValidationErrors errors, CancellationToken ct)
    {
        foreach (var person in references.People)
        {
            if (await users.GetAsync(person, ct) is null)
            {
                errors.Add("data", "validation.services.person");
                break;
            }
        }
        foreach (var unit in references.Units)
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                errors.Add("data", "validation.services.unit");
                break;
            }
        }
        if (references.Attachments.Count > maxAttachments)
        {
            errors.Add("data", "validation.services.too_many_attachments");
            return;
        }
        var kept = existingRequest is { } requestId ? await attachments.LinkedAsync(requestId, references.Attachments, ct) : new HashSet<Guid>();
        var added = references.Attachments.Except(kept).ToHashSet();
        if (added.Count > 0 && await attachments.ProblemAsync(added, ct) is { } problem)
        {
            errors.Add("data", problem);
        }
    }

    internal Task EventAsync(Guid requestId, string @event, string? step, Guid? actor, Guid? onBehalfOf, string? comment, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into services.request_event (tenant_id, request_id, event, step, actor_id, on_behalf_of, detail, at)
            values (platform.current_tenant_id(), @requestId, @event, @step, @actor, @onBehalfOf, @detail, @at)
            """, new { requestId, @event, step, actor, onBehalfOf, detail = Jsonb.From(new { comment }), at = clock.UtcNow }, ct);

    // ---- Reading ----

    internal Task<RequestRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from services.request r where r.id = @id" + (forUpdate ? " for update" : ""), new { id }, Read, ct);

    /// <summary>
    /// Who may read a request: the person who filed it, the person it is for, a candidate of one of its tasks, a manager
    /// of the person it is for (Team requests, through the reporting line and never through org unit membership), and
    /// owners of its service. Everyone else, and every other tenant, gets not found.
    /// </summary>
    internal async Task<RequestRow> VisibleRowAsync(Guid id, CancellationToken ct)
    {
        if (context.ActorType == ActorType.App)
        {
            var filed = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
            return filed.RequesterAppId == context.ActorId || Reaches(await AppReachAsync(ct), filed.OrgUnitId) ? filed : throw AppException.NotFound();
        }
        var user = context.RequireUser();
        var row = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        if (row.RequesterId == user || row.OnBehalfOfId == user)
        {
            return row;
        }
        if (row.InstanceId is { } instance && (await engine.InstanceTasksAsync(instance, ct)).Any(t => t.CompletedBy == user)
            || row.InstanceId is { } open && (await engine.MyOpenTasksAsync(500, ct)).Any(t => t.InstanceId == open))
        {
            return row;
        }
        if (await reportingLine.IsManagerOfAsync(user, row.SubjectId, ct))
        {
            return row;
        }
        var owner = await session.ScalarAsync<Guid?>("select owner_org_unit_id from services.service where id = @id", new { id = row.ServiceId }, ct);
        if ((await authz.CheckAsync(Permissions.ServiceManage, owner is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct)).Allowed)
        {
            return row;
        }
        throw AppException.NotFound();
    }

    private readonly Dictionary<Guid, PersonRef> people = [];

    private async Task<PersonRef> PersonAsync(Guid id, CancellationToken ct)
    {
        if (!people.TryGetValue(id, out var person))
        {
            people[id] = person = new PersonRef(id, (await users.GetAsync(id, ct))?.DisplayName ?? string.Empty);
        }
        return person;
    }

    private async Task<RequestSummary> SummaryAsync(RequestRow row, CancellationToken ct)
    {
        var names = await session.ScalarAsync<string>("select names::text from services.service where id = @id", new { id = row.ServiceId }, ct);
        IReadOnlyDictionary<string, string>? step = null;
        var assignees = new List<PersonRef>();
        if (row.InstanceId is { } instance && row.Status == RequestStatuses.InApproval
            && (await engine.InstanceTasksAsync(instance, ct)).FirstOrDefault(t => t.Status == "open") is { } open)
        {
            var version = await Versions.GetAsync(session, row.ServiceId, row.ServiceVersion, ct);
            if (version?.Route.Graph is { } graph)
            {
                // A step graph route: the step open now (the first, when parallel steps are open) and the people it names.
                var node = graph.Step(RouteGraph.StepIdOf(open.ElementId));
                step = node?.Names is { Count: > 0 } nodeNames ? nodeNames : Catalogue.Json<Dictionary<string, string>>(names!);
                IReadOnlyList<Guid> people = node?.Assignee is { Kind: AssigneeKinds.Users, Users: { } listed } ? listed
                    : node?.Assignee is { Kind: AssigneeKinds.LineManager } && await reportingLine.ManagerOfAsync(row.SubjectId, ct) is { } boss ? [boss] : [];
                foreach (var person in people.Where(p => p != row.RequesterId && p != row.OnBehalfOfId))
                {
                    assignees.Add(await PersonAsync(person, ct));
                }
                return new RequestSummary(row.Id, row.Number, row.ServiceId, Catalogue.Json<Dictionary<string, string>>(names!), row.Status, await PersonAsync(row.RequesterId, ct),
                    row.OnBehalfOfId is { } someone ? await PersonAsync(someone, ct) : null, step, assignees, row.CreatedAt, row.SubmittedAt, row.ClosedAt);
            }
            step = version?.Route.Approval?.Names is { Count: > 0 } stepNames ? stepNames : Catalogue.Json<Dictionary<string, string>>(names!);
            if (version?.Route.Approval?.Approvers is { Kind: ApproverKinds.Users, Users: { } named })
            {
                foreach (var person in named.Where(p => p != row.RequesterId && p != row.OnBehalfOfId))
                {
                    assignees.Add(await PersonAsync(person, ct));
                }
            }
            else if (version?.Route.Approval?.Approvers is { Kind: ApproverKinds.LineManager }
                && await reportingLine.ManagerOfAsync(row.SubjectId, ct) is { } manager && manager != row.RequesterId)
            {
                assignees.Add(await PersonAsync(manager, ct));
            }
        }
        return new RequestSummary(row.Id, row.Number, row.ServiceId, Catalogue.Json<Dictionary<string, string>>(names!), row.Status, await PersonAsync(row.RequesterId, ct),
            row.OnBehalfOfId is { } other ? await PersonAsync(other, ct) : null, step, assignees, row.CreatedAt, row.SubmittedAt, row.ClosedAt);
    }

    /// <summary>
    /// Team requests (FR-005, US2 scenario 4): requests for the caller's direct and indirect reports by the reporting
    /// line, newest first. Org unit membership alone never adds anyone.
    /// </summary>
    public async Task<IReadOnlyList<RequestSummary>> TeamAsync(string? status, int limit, CancellationToken ct)
    {
        var user = context.RequireUser();
        var reports = (await reportingLine.AllReportsAsync(user, ct)).Where(r => r != user).ToArray();
        if (reports.Length == 0)
        {
            return [];
        }
        var rows = await session.QueryAsync(
            $"""
            select {Columns} from services.request r
            where coalesce(r.on_behalf_of_user_id, r.requester_user_id) = any(@reports) and (@status::text is null or r.status = @status)
            order by r.created_at desc, r.id limit @limit
            """, new { reports, status, limit = Math.Clamp(limit, 1, 200) }, Read, ct);
        var list = new List<RequestSummary>();
        foreach (var row in rows)
        {
            list.Add(await SummaryAsync(row, ct));
        }
        return list;
    }

    private OrgCoverage? appReach;

    /// <summary>
    /// For an application app (spec 035 US1 scenario 4): the units its own <c>service.request_read</c> covers. It reads the
    /// requests of people in those units, by the unit the request was filed under, and the requests it filed itself.
    /// </summary>
    private async Task<OrgCoverage> AppReachAsync(CancellationToken ct) => appReach ??= await authz.CoverageAsync(Permissions.ServiceRequestRead, ct);

    private static bool Reaches(OrgCoverage reach, Guid? unit) => reach.WholeTenant || unit is { } u && reach.Units.Contains(u);

    /// <summary>
    /// One page of requests for the public API (spec 035 <c>requests:read</c>), newest first, after the last one of the
    /// previous page. A person reads their own, as in My requests; an application app reads those its request role reaches.
    /// </summary>
    public async Task<IReadOnlyList<RequestSummary>> PageAsync(string? status, DateTimeOffset? beforeAt, Guid? beforeId, int limit, CancellationToken ct)
    {
        Guid? user = null;
        Guid? app = null;
        Guid[]? units = null;
        var everywhere = false;
        if (context.ActorType == ActorType.App)
        {
            app = context.ActorId;
            var reach = await AppReachAsync(ct);
            everywhere = reach.WholeTenant;
            units = reach.Units.ToArray();
        }
        else
        {
            user = context.RequireUser();
        }
        var rows = await session.QueryAsync(
            $"""
            select {Columns} from services.request r
            where (@status::text is null or r.status = @status)
              and (@beforeAt::timestamptz is null or (r.created_at, r.id) < (@beforeAt::timestamptz, @beforeId::uuid))
              and (@user::uuid is not null and (r.requester_user_id = @user or r.on_behalf_of_user_id = @user)
                or @app::uuid is not null and (@everywhere or r.requester_org_unit_id = any(@units) or r.requester_app_id = @app))
            order by r.created_at desc, r.id desc limit @limit
            """, new { status, beforeAt, beforeId = beforeId ?? Guid.Empty, user, app, everywhere, units = units ?? [], limit = Math.Clamp(limit, 1, 200) }, Read, ct);
        var list = new List<RequestSummary>();
        foreach (var row in rows)
        {
            list.Add(await SummaryAsync(row, ct));
        }
        return list;
    }

    /// <summary>My requests (FR-005): filed by me or for me, newest first.</summary>
    public async Task<IReadOnlyList<RequestSummary>> MineAsync(string? status, int limit, CancellationToken ct)
    {
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            $"""
            select {Columns} from services.request r
            where (r.requester_user_id = @user or r.on_behalf_of_user_id = @user) and (@status::text is null or r.status = @status)
            order by r.created_at desc, r.id limit @limit
            """, new { user, status, limit = Math.Clamp(limit, 1, 200) }, Read, ct);
        var list = new List<RequestSummary>();
        foreach (var row in rows)
        {
            list.Add(await SummaryAsync(row, ct));
        }
        return list;
    }

    public async Task<RequestDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var row = await VisibleRowAsync(id, ct);
        var version = await Versions.GetAsync(session, row.ServiceId, row.ServiceVersion, ct);
        var events = await session.QueryAsync(
            "select event, step, actor_id, on_behalf_of, detail->>'comment', at from services.request_event where request_id = @id order by id",
            new { id }, r => (Event: r.GetString(0), Step: r.Get<string?>("step"), Actor: r.Get<Guid?>("actor_id"), OnBehalfOf: r.Get<Guid?>("on_behalf_of"),
                Comment: r.IsDBNull(4) ? null : r.GetString(4), At: r.GetFieldValue<DateTimeOffset>(5)), ct);
        var tracking = new List<TrackingEntry>();
        foreach (var e in events)
        {
            tracking.Add(new TrackingEntry(e.Event, e.Step, e.Actor is { } a ? await PersonAsync(a, ct) : null, e.OnBehalfOf is { } b ? await PersonAsync(b, ct) : null,
                e.Comment, e.At));
        }
        return new RequestDetail(await SummaryAsync(row, ct), row.ServiceVersion, version?.Form ?? FormDefinition.Empty,
            Catalogue.Json<Dictionary<string, JsonElement>>(row.Data), row.Classification, await attachments.ForRequestAsync(id, ct), tracking,
            version?.Route.Actions is { Count: > 0 } actions && row.InstanceId is { } instance ? await AutomationAsync(row, instance, actions, ct) : null);
    }

    /// <summary>The connector steps of the request's current route, with the open incident of each; owners may act on them.</summary>
    private async Task<RequestAutomation> AutomationAsync(RequestRow row, Guid instance, IReadOnlyList<ActionStep> actions, CancellationToken ct)
    {
        // Steps are published as action_1, action_2, ... in route order; their names in each language come from the version.
        IReadOnlyDictionary<string, string>? NamesOf(string element) =>
            element.StartsWith("action_", StringComparison.Ordinal) && int.TryParse(element["action_".Length..], out var n) && n >= 1 && n <= actions.Count
                ? actions[n - 1].Names : null;
        var incidents = (await engine.IncidentsAsync(instance, ct)).Where(i => i.Status == "open").ToDictionary(i => i.ElementId, i => i.Id);
        var steps = (await engine.ServiceTasksAsync(instance, ct))
            .Select(s => new AutomationStepView(s.ElementId, s.StepName, s.Status, s.ExternalReference, s.ErrorClass, s.ErrorCode, s.StartedAt, s.EndedAt,
                incidents.TryGetValue(s.ElementId, out var incident) ? incident : null, NamesOf(s.ElementId)))
            .ToList();
        var owner = await session.ScalarAsync<Guid?>("select owner_org_unit_id from services.service where id = @id", new { id = row.ServiceId }, ct);
        var canOperate = (await authz.CheckAsync(Permissions.ServiceManage, owner is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct)).Allowed;
        return new RequestAutomation(steps, canOperate);
    }

    // ---- Triggers (FR-014) ----

    /// <summary>The deepest chain of requests started by other requests (edge cases: trigger loops).</summary>
    public const int MaxChainDepth = 5;

    /// <summary>
    /// Starts a request of a published form service from a trigger or a start service step, for <paramref name="subject"/>,
    /// who becomes its requester as if they had filed it, so it shows in their and their manager's lists. It carries no
    /// form values: its steps read the request and the requester. One event starts at most one request of a service
    /// (<paramref name="eventKey"/>); returns null when it already started one, or when the service, the plan or the
    /// person cannot take it. Runs as the system actor.
    /// </summary>
    internal async Task<Guid?> StartByTriggerAsync(Guid serviceId, Guid subject, string eventKey, int depth, string trigger, CancellationToken ct)
    {
        var service = await session.QuerySingleOrDefaultAsync(
            "select kind, status, current_version, classification, owner_org_unit_id from services.service where id = @serviceId",
            new { serviceId }, r => (Kind: r.GetString(0), Status: r.GetString(1), Version: r.Get<int?>("current_version"), Classification: r.GetString(3),
                Owner: r.Get<Guid?>("owner_org_unit_id")), ct);
        if (service.Kind != ServiceKinds.Form || service.Status != "published" || service.Version is not { } version || depth > MaxChainDepth
            || !await entitlements.IsEnabledAsync(Capabilities.WorkflowAutomation, ct)
            || await users.GetAsync(subject, ct) is not { Status: "active" })
        {
            return null;
        }
        var published = (await Versions.GetAsync(session, serviceId, version, ct))!;
        var id = Ids.New();
        var now = clock.UtcNow;
        var number = await NextNumberAsync(ct);
        var unit = await PrimaryUnitAsync(subject, ct);
        var inserted = await session.ScalarAsync<Guid?>(
            """
            insert into services.request (tenant_id, id, number, service_id, service_version, requester_user_id, requester_org_unit_id,
              status, data, classification, created_at, submitted_at, trigger_event, chain_depth)
            values (platform.current_tenant_id(), @id, @number, @serviceId, @version, @subject, @unit, @status, '{}'::jsonb, @classification, @now, @now, @eventKey, @depth)
            on conflict (tenant_id, service_id, trigger_event) where trigger_event is not null do nothing
            returning id
            """, new
            {
                id, number, serviceId, version, subject, unit, classification = service.Classification, now, eventKey, depth,
                status = published.Route.Approval is null && published.Route.Graph is null ? RequestStatuses.Submitted : RequestStatuses.InApproval,
            }, ct);
        if (inserted is null)
        {
            return null;
        }
        await EventAsync(id, "triggered", null, null, null, null, ct);
        if (published.Route.HasSteps)
        {
            await StartRouteAsync(id, serviceId, unit, subject, subject, 1, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.RequestStartedByTrigger, "request", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { service = serviceId, version, number, trigger, @event = eventKey, depth, forUser = subject }), ct);
        await outbox.EnqueueAsync(new RequestSubmitted(id, serviceId, number), ct);
        return id;
    }

    /// <summary>The person a request is for, and how deep it sits in a chain of started requests.</summary>
    internal Task<(Guid Subject, int Depth)> ChainOfAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync("select coalesce(on_behalf_of_user_id, requester_user_id), chain_depth from services.request where id = @id",
            new { id }, r => (r.GetGuid(0), r.GetInt32(1)), ct);

    /// <summary>
    /// The requester left (disabled by directory sync): their open requests are cancelled with reason "requester left",
    /// except those of services marked "continue after exit" on the version they run on, and those the leaving itself started.
    /// </summary>
    internal async Task<int> CancelForLeaverAsync(Guid user, CancellationToken ct)
    {
        var open = await session.QueryAsync(
            """
            select r.id from services.request r
            join services.service_version v on v.service_id = r.service_id and v.version = r.service_version
            where r.requester_user_id = @user and r.status = any(@statuses)
              and coalesce((v.route->>'continueAfterExit')::boolean, false) = false
              and r.trigger_event is distinct from @leaving
            order by r.created_at
            """, new { user, statuses = RequestStatuses.Cancellable.ToArray(), leaving = $"{TriggerEvents.UserDeprovisioned}:{user:N}" }, r => r.GetGuid(0), ct);
        foreach (var id in open)
        {
            var row = (await RowAsync(id, true, ct))!;
            if (row.InstanceId is { } instance && (await engine.GetInstanceAsync(instance, ct))?.Status == "active")
            {
                await engine.WithdrawAsync(instance, ct);
            }
            await session.ExecAsync("update services.request set status = 'cancelled', closed_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
            await EventAsync(id, "requester_left", null, null, null, null, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.RequestCancelled, "request", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
                Changes: new { row.Number, from = row.Status, reason = "requester_left" }), ct);
            await outbox.EnqueueAsync(new RequestCompleted(id, row.ServiceId, RequestStatuses.Cancelled), ct);
        }
        return open.Count;
    }

    // ---- Cancel ----

    /// <summary>The person who filed a request cancels it until it is approved (section 6); its open task is cancelled with it.</summary>
    public async Task<RequestDetail> CancelAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        if (row.RequesterId != user)
        {
            await VisibleRowAsync(id, ct);
            throw AppException.Denied();
        }
        if (!RequestStatuses.Cancellable.Contains(row.Status))
        {
            throw AppException.Conflict("conflict.services.request_closed");
        }
        if (row.InstanceId is { } instance && (await engine.GetInstanceAsync(instance, ct))?.Status == "active")
        {
            await engine.WithdrawAsync(instance, ct);
        }
        await session.ExecAsync("update services.request set status = 'cancelled', closed_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        await EventAsync(id, "cancelled", null, user, null, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RequestCancelled, "request", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { row.Number, from = row.Status }), ct);
        await outbox.EnqueueAsync(new RequestCompleted(id, row.ServiceId, RequestStatuses.Cancelled), ct);
        return await DetailAsync(id, ct);
    }
}
