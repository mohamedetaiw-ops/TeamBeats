using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Services;

/// <summary>
/// One starter template of the pack (spec 016 section 12): the settings, form and step graph a draft service starts
/// from. <see cref="Without"/> lists capabilities that replace the template, such as the expense module for the simple
/// expense claim. The pack is versioned product configuration; a switched-on template becomes an ordinary draft the
/// tenant edits, checked by the same rules as any other draft.
/// </summary>
public sealed record ServiceTemplate(
    string Key,
    int Version,
    string? Icon,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Descriptions,
    string Classification,
    FormDefinition Form,
    ServiceRoute Route,
    bool AllowOnBehalf = false,
    int MaxAttachments = 0,
    IReadOnlyList<string>? Without = null);

/// <summary>A template as the catalogue owner sees it: whether it fits the plan and the draft or service made from it.</summary>
public sealed record ServiceTemplateView(string Key, int Version, string? Icon, IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string> Descriptions,
    bool Available, Guid? ServiceId, string? ServiceStatus);

/// <summary>
/// The starter template pack (spec 016 section 12, T025). Tenant-wide catalogue owners (<c>service.manage</c> for the
/// whole tenant) list the templates and switch one on, which creates a tenant-owned draft service from it; they then
/// edit and publish it like any other. Switching on a template already switched on returns its service.
/// </summary>
public sealed class ServiceTemplates(IDbSession session, IAuthorizationService authz, IEntitlementService entitlements, ServiceAdmin admin)
{
    private static readonly Lazy<IReadOnlyList<ServiceTemplate>> Pack = new(Load);

    public static IReadOnlyList<ServiceTemplate> All => Pack.Value;

    private static IReadOnlyList<ServiceTemplate> Load()
    {
        var assembly = typeof(ServiceTemplates).Assembly;
        var list = new List<ServiceTemplate>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Templates.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            list.AddRange(JsonSerializer.Deserialize<List<ServiceTemplate>>(stream, JsonSerializerOptions.Web) ?? []);
        }
        if (list.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            throw new InvalidOperationException("Service template keys must be unique.");
        }
        return list;
    }

    private async Task DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        await authz.DemandAsync(Permissions.ServiceManage, AuthzTarget.Tenant, ct);
    }

    /// <summary>A template fits the plan when no replacing capability is on and every step of its route is in the plan.</summary>
    private async Task<bool> AvailableAsync(ServiceTemplate template, CancellationToken ct)
    {
        foreach (var capability in template.Without ?? [])
        {
            if (await entitlements.IsEnabledAsync(capability, ct))
            {
                return false;
            }
        }
        foreach (var capability in template.Route.Graph?.Capabilities() ?? new HashSet<string>())
        {
            if (!await entitlements.IsEnabledAsync(capability, ct))
            {
                return false;
            }
        }
        return true;
    }

    private async Task<Dictionary<string, (Guid Id, string Status)>> SwitchedOnAsync(CancellationToken ct) =>
        (await session.QueryAsync("select template_key, id, status from services.service where template_key is not null", null,
            r => (Key: r.GetString(0), Id: r.GetGuid(1), Status: r.GetString(2)), ct)).ToDictionary(r => r.Key, r => (r.Id, r.Status), StringComparer.Ordinal);

    public async Task<IReadOnlyList<ServiceTemplateView>> ListAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        var on = await SwitchedOnAsync(ct);
        var views = new List<ServiceTemplateView>();
        foreach (var t in All)
        {
            var made = on.TryGetValue(t.Key, out var s) ? s : ((Guid Id, string Status)?)null;
            views.Add(new ServiceTemplateView(t.Key, t.Version, t.Icon, t.Names, t.Descriptions, await AvailableAsync(t, ct), made?.Id, made?.Status));
        }
        return views;
    }

    /// <summary>Creates the tenant-owned draft service of a template, or returns the one made earlier.</summary>
    public async Task<(ServiceAdminView View, bool Created)> SwitchOnAsync(string key, CancellationToken ct)
    {
        await DemandAsync(ct);
        var template = All.FirstOrDefault(t => t.Key == key) ?? throw AppException.NotFound();
        if (!await AvailableAsync(template, ct))
        {
            throw AppException.Validation("key", "validation.services.template_unavailable");
        }
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':services.template:' || @key, 0))", new { key }, ct);
        if ((await SwitchedOnAsync(ct)).TryGetValue(key, out var existing))
        {
            return (await admin.GetAsync(existing.Id, ct), false);
        }
        var created = await admin.CreateAsync(new ServiceSettingsRequest(ServiceKinds.Form, template.Names, template.Descriptions, null, null, null, null,
            template.Classification, null, Icon: template.Icon), ct);
        var id = created.Service.Id;
        await session.ExecAsync("update services.service set template_key = @key where id = @id", new { key, id }, ct);
        var view = await admin.SaveDraftAsync(id, new ServiceDraftRequest(template.Form, template.Route, null, template.AllowOnBehalf, template.MaxAttachments), ct);
        return (view, true);
    }
}
