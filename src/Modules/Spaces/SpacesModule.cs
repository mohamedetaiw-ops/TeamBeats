using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

/// <summary>Whether spaces are in the package, and what the viewer may do across them: create one, and manage the tenant's space rules and templates.</summary>
public sealed record SpacesRights(bool Enabled, bool CanCreate, bool CanManage, bool Guests);

/// <summary>Project and team spaces (spec 029, module map: Spaces).</summary>
public static class SpacesModule
{
    public static readonly MigrationSource Migrations = new("spaces", typeof(SpacesModule).Assembly);

    public static IServiceCollection AddSpacesModule(this IServiceCollection services)
    {
        services.AddScoped<SpacesService>();
        services.AddScoped<SpaceSettings>();
        services.AddScoped<SpaceTasks>();
        services.AddScoped<SpaceMilestones>();
        services.AddScoped<SpaceCalendar>();
        services.AddScoped<SpaceGuests>();
        services.AddScoped<IGuestInvitationSource>(sp => sp.GetRequiredService<SpaceGuests>());
        services.AddScoped<Contracts.ISpaceGuestReview>(sp => sp.GetRequiredService<SpaceGuests>());
        services.AddScoped<IResourceDescriptorProvider, SpaceDescriptorProvider>();
        services.AddScoped<Modules.Search.Contracts.ISearchSource, SpacesSearchSource>();
        services.AddScoped<Modules.Search.Contracts.ISearchSource, SpaceTaskSearchSource>();
        services.AddMessageHandler<Tenancy.Contracts.TenantMembershipDisabled, SpaceLeavers>("spaces.ownerless");
        return services;
    }

    public static IEndpointRouteBuilder MapSpacesEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/spaces").RequireTenantUser().WithTags("Spaces");

        root.MapGet("/rights", async (SpacesService spaces, IAuthorizationService authz, IEntitlementService entitlements, CancellationToken ct) =>
        {
            var enabled = await entitlements.IsEnabledAsync(Capabilities.SpacesProjects, ct);
            return Results.Ok(new SpacesRights(
                enabled,
                enabled && await spaces.MayCreateAsync(ct),
                enabled && (await authz.CheckAsync(Permissions.SpacesManage, AuthzTarget.Tenant, ct)).Allowed,
                enabled && await entitlements.IsEnabledAsync(Capabilities.SpacesGuests, ct)));
        });

        root.MapGet("/templates", async (SpaceSettings settings, CancellationToken ct) => Results.Ok(await settings.OptionsAsync(ct)));

        root.MapGet("", async (string? scope, string? q, SpacesService spaces, CancellationToken ct) =>
            Results.Ok(await spaces.ListAsync(scope == "mine", scope == "all", q, ct)));

        root.MapPost("", async (SpaceRequest request, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/spaces/{view.Id}", view);
        });

        // Opening a space may give it its library and discussion (FR-006), so the read commits.
        root.MapGet("/{id:guid}", async (Guid id, SpacesService spaces, IDbSession session, CancellationToken ct) => await CommitAsync(session, await spaces.GetAsync(id, ct), ct));

        root.MapPut("/{id:guid}", async (Guid id, SpaceRequest request, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}:archive", async (Guid id, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.ArchiveAsync(id, true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}:restore", async (Guid id, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.ArchiveAsync(id, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}:join", async (Guid id, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.JoinAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}:leave", async (Guid id, SpacesService spaces, ITenantContext context, IDbSession session, CancellationToken ct) =>
        {
            await spaces.RemoveMemberAsync(id, context.RequireUser(), ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/{id:guid}/members", async (Guid id, SpacesService spaces, CancellationToken ct) => Results.Ok(await spaces.MembersAsync(id, ct)));

        root.MapPost("/{id:guid}/members", async (Guid id, MemberRequest request, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var members = await spaces.SetMemberAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(members);
        });

        root.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            await spaces.RemoveMemberAsync(id, userId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // Guests (US4, FR-007, FR-008). Inviting and renewing a guest are sensitive actions that need a fresh sign-in (spec 026).
        root.MapGet("/as-guest", async (SpaceGuests guests, CancellationToken ct) => Results.Ok(await guests.MineAsync(ct)));
        root.MapGet("/{id:guid}/guests", async (Guid id, SpaceGuests guests, CancellationToken ct) => Results.Ok(await guests.ListAsync(id, ct)));
        root.MapPost("/{id:guid}/guests", async (Guid id, GuestInviteRequest request, SpaceGuests guests, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await guests.InviteAsync(id, request, ct), ct)).RequireStepUp();
        root.MapPut("/{id:guid}/guests/{userId:guid}", async (Guid id, Guid userId, GuestRenewRequest request, SpaceGuests guests, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await guests.RenewAsync(id, userId, request, ct), ct)).RequireStepUp();
        root.MapDelete("/{id:guid}/guests/invitations/{invitationId:guid}", async (Guid id, Guid invitationId, SpaceGuests guests, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await guests.CancelInvitationAsync(id, invitationId, ct), ct));
        root.MapPut("/{id:guid}/guests/files", async (Guid id, GuestFilesRequest request, SpaceGuests guests, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await guests.SetFilesAsync(id, request, ct), ct));

        MapTaskEndpoints(root);

        // ---- Tenant rules and templates (spaces administrators) ----

        root.MapGet("/manage/settings", async (SpaceSettings settings, CancellationToken ct) => Results.Ok(await settings.GetRulesAsync(ct)));

        root.MapPut("/manage/settings", async (SpaceRulesRequest request, SpaceSettings settings, SpaceGuests guests, IDbSession session, CancellationToken ct) =>
        {
            var rules = await settings.SetRulesAsync(request, ct);
            await guests.SyncFilesAsync(rules.GuestConfidential, ct);
            await session.CommitAsync(ct);
            return Results.Ok(rules);
        });

        root.MapGet("/manage/templates", async (SpaceSettings settings, CancellationToken ct) => Results.Ok(await settings.TemplatesAsync(ct)));

        root.MapPost("/manage/templates", async (TemplateRequest request, SpaceSettings settings, IDbSession session, CancellationToken ct) =>
        {
            var view = await settings.CreateTemplateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/spaces/manage/templates/{view.Id}", view);
        });

        root.MapPut("/manage/templates/{id:guid}", async (Guid id, TemplateRequest request, SpaceSettings settings, IDbSession session, CancellationToken ct) =>
        {
            var view = await settings.UpdateTemplateAsync(id, request, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/manage/templates/{id:guid}:retire", async (Guid id, SpaceSettings settings, IDbSession session, CancellationToken ct) =>
        {
            var view = await settings.UpdateTemplateAsync(id, new TemplateRequest(null, null, null, null), true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        return app;
    }

    private static async Task<IResult> CommitAsync<T>(IDbSession session, T value, CancellationToken ct)
    {
        await session.CommitAsync(ct);
        return Results.Ok(value);
    }

    /// <summary>Tasks, milestones, status and My tasks (spec 029 US2, US3, US7).</summary>
    private static void MapTaskEndpoints(RouteGroupBuilder root)
    {
        root.MapGet("/my-tasks", async (bool? done, SpaceTasks tasks, CancellationToken ct) => Results.Ok(await tasks.MineAsync(done ?? false, ct)));

        var space = root.MapGroup("/{id:guid}");

        space.MapGet("/overview", async (Guid id, SpaceMilestones milestones, CancellationToken ct) => Results.Ok(await milestones.OverviewAsync(id, ct)));

        // The team calendar (US6, FR-009): events, milestones, due dates and approved leave; free and busy through spec 018.
        space.MapGet("/calendar", async (Guid id, DateOnly? from, DateOnly? to, SpaceCalendar calendar, CancellationToken ct) =>
            Results.Ok(await calendar.GetAsync(id, from, to, ct)));
        space.MapGet("/calendar/availability", async (Guid id, DateOnly? date, SpaceCalendar calendar, CancellationToken ct) =>
            Results.Ok(await calendar.AvailabilityAsync(id, date, ct)));
        space.MapPost("/events", async (Guid id, SpaceEventRequest request, SpaceCalendar calendar, IDbSession session, CancellationToken ct) =>
        {
            var view = await calendar.CreateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/spaces/{id}/events/{view.Id}", view);
        });
        space.MapPut("/events/{eventId:guid}", async (Guid id, Guid eventId, SpaceEventRequest request, SpaceCalendar calendar, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await calendar.UpdateAsync(id, eventId, request, ct), ct));
        space.MapDelete("/events/{eventId:guid}", async (Guid id, Guid eventId, SpaceCalendar calendar, IDbSession session, CancellationToken ct) =>
        {
            await calendar.DeleteAsync(id, eventId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        space.MapPost("/status", async (Guid id, StatusRequest request, SpaceMilestones milestones, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await milestones.SetStatusAsync(id, request, ct), ct));

        space.MapGet("/milestones", async (Guid id, SpaceMilestones milestones, CancellationToken ct) => Results.Ok(await milestones.MilestonesAsync(id, ct)));

        space.MapPost("/milestones", async (Guid id, MilestoneRequest request, SpaceMilestones milestones, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await milestones.CreateAsync(id, request, ct), ct));

        space.MapPut("/milestones/{milestoneId:guid}", async (Guid id, Guid milestoneId, MilestoneRequest request, SpaceMilestones milestones, IDbSession session,
            CancellationToken ct) => await CommitAsync(session, await milestones.UpdateAsync(id, milestoneId, request, ct), ct));

        space.MapDelete("/milestones/{milestoneId:guid}", async (Guid id, Guid milestoneId, SpaceMilestones milestones, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await milestones.DeleteAsync(id, milestoneId, ct), ct));

        space.MapPost("/lists", async (Guid id, ListRequest request, SpaceTasks tasks, ILocalizer text, ITenantSettingsReader tenantSettings, IDbSession session,
            CancellationToken ct) =>
        {
            var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            (string, bool)[] columns =
            [
                (text.Get("spaces.template.column.todo", locale), false),
                (text.Get("spaces.template.column.doing", locale), false),
                (text.Get("spaces.template.column.done", locale), true),
            ];
            return await CommitAsync(session, await tasks.AddListAsync(id, request, columns, ct), ct);
        });

        space.MapPost("/lists/{listId:guid}/columns", async (Guid id, Guid listId, ColumnRequest request, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await tasks.AddColumnAsync(id, listId, request, ct), ct));

        space.MapGet("/tasks", async (Guid id, Guid? list, SpaceTasks tasks, CancellationToken ct) => Results.Ok(await tasks.BoardAsync(id, list, ct)));

        space.MapGet("/tasks.csv", async (Guid id, SpaceTasks tasks, CancellationToken ct) =>
        {
            var (name, content) = await tasks.ExportAsync(id, ct);
            return Results.File(content, "text/csv; charset=utf-8", name);
        });

        space.MapPost("/tasks", async (Guid id, TaskRequest request, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
        {
            var task = await tasks.CreateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/spaces/{id}/tasks/{task.Task.Id}", task);
        });

        space.MapGet("/tasks/{taskId:guid}", async (Guid id, Guid taskId, SpaceTasks tasks, CancellationToken ct) => Results.Ok(await tasks.GetAsync(id, taskId, ct)));

        space.MapPut("/tasks/{taskId:guid}", async (Guid id, Guid taskId, TaskRequest request, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await tasks.UpdateAsync(id, taskId, request, ct), ct));

        space.MapDelete("/tasks/{taskId:guid}", async (Guid id, Guid taskId, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
        {
            await tasks.DeleteAsync(id, taskId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        space.MapPost("/tasks/{taskId:guid}:move", async (Guid id, Guid taskId, MoveRequest request, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await tasks.MoveAsync(id, taskId, request, ct), ct));

        space.MapPost("/tasks/{taskId:guid}:complete", async (Guid id, Guid taskId, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await tasks.CompleteAsync(id, taskId, true, ct), ct));

        space.MapPost("/tasks/{taskId:guid}:reopen", async (Guid id, Guid taskId, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await tasks.CompleteAsync(id, taskId, false, ct), ct));

        space.MapPost("/tasks/{taskId:guid}/checklist", async (Guid id, Guid taskId, ChecklistRequest request, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await tasks.AddChecklistItemAsync(id, taskId, request, ct), ct));

        space.MapPut("/tasks/{taskId:guid}/checklist/{itemId:guid}", async (Guid id, Guid taskId, Guid itemId, ChecklistRequest request, SpaceTasks tasks, IDbSession session,
            CancellationToken ct) => await CommitAsync(session, await tasks.SetChecklistItemAsync(id, taskId, itemId, request, ct), ct));

        space.MapDelete("/tasks/{taskId:guid}/checklist/{itemId:guid}", async (Guid id, Guid taskId, Guid itemId, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await tasks.RemoveChecklistItemAsync(id, taskId, itemId, ct), ct));

        space.MapPost("/tasks/{taskId:guid}/comments", async (Guid id, Guid taskId, CommentRequest request, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await tasks.CommentAsync(id, taskId, request, ct), ct));

        space.MapPost("/tasks/{taskId:guid}/files", async (Guid id, Guid taskId, TaskFileRequest request, SpaceTasks tasks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await tasks.LinkFileAsync(id, taskId, request, ct), ct));

        space.MapDelete("/tasks/{taskId:guid}/files/{documentId:guid}", async (Guid id, Guid taskId, Guid documentId, SpaceTasks tasks, IDbSession session,
            CancellationToken ct) => await CommitAsync(session, await tasks.UnlinkFileAsync(id, taskId, documentId, ct), ct));
    }
}
