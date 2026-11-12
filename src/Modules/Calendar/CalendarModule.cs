using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Calendar;

/// <summary>The unified employee and team calendar (spec 037, module map: Calendar).</summary>
public static class CalendarModule
{
    public static readonly MigrationSource Migrations = new("calendar", typeof(CalendarModule).Assembly);

    public static IServiceCollection AddCalendarModule(this IServiceCollection services)
    {
        services.AddScoped<CalendarSettings>();
        services.AddScoped<CompanyEvents>();
        services.AddScoped<UnifiedCalendar>();
        services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IComponentRegistration, CalendarComponents>();
        services.AddScoped<TeamBeats.Modules.Agents.Contracts.IAgentTool, MyCalendarTool>();
        services.AddScoped<TeamBeats.Modules.Agents.Contracts.IAgentTool, TeamAvailabilityTool>();
        return services;
    }

    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/calendar").RequireTenantUser().WithTags("Calendar");

        root.MapGet("/events", async (DateOnly from, DateOnly to, string? layers, string? cursor, bool? refresh, UnifiedCalendar calendar, CancellationToken ct) =>
            Results.Ok(await calendar.EventsAsync(from, to, layers, cursor, refresh == true, ct)));

        root.MapGet("/summary", async (DateOnly from, DateOnly to, UnifiedCalendar calendar, CancellationToken ct) =>
            Results.Ok(await calendar.SummaryAsync(from, to, ct)));

        root.MapGet("/team-availability", async (DateOnly from, DateOnly to, UnifiedCalendar calendar, IDbSession session, CancellationToken ct) =>
        {
            var view = await calendar.TeamAvailabilityAsync(from, to, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/holidays", async (DateOnly from, DateOnly to, UnifiedCalendar calendar, CancellationToken ct) =>
            Results.Ok(await calendar.HolidaysAsync(from, to, ct)));

        root.MapGet("/preferences", async (UnifiedCalendar calendar, CancellationToken ct) => Results.Ok(await calendar.PreferencesAsync(ct)));

        root.MapPut("/preferences", async (PreferencesRequest request, UnifiedCalendar calendar, IDbSession session, CancellationToken ct) =>
        {
            var view = await calendar.SavePreferencesAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/sources/status", async (UnifiedCalendar calendar, CancellationToken ct) => Results.Ok(await calendar.SourcesAsync(ct)));

        root.MapPost("/events/{id:guid}/response", async (Guid id, ResponseRequest request, CompanyEvents events, IDbSession session, CancellationToken ct) =>
        {
            var view = await events.RespondAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/admin-events", async (CompanyEvents events, CancellationToken ct) => Results.Ok(await events.ListAdminAsync(ct)));

        root.MapPost("/admin-events", async (EventRequest request, CompanyEvents events, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var view = await events.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Created($"/api/v1/calendar/admin-events/{view.Id}", view);
        });

        root.MapGet("/admin-events/{id:guid}", async (Guid id, CompanyEvents events, HttpResponse response, CancellationToken ct) =>
        {
            var view = await events.GetAdminAsync(id, ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        root.MapPatch("/admin-events/{id:guid}", async (Guid id, EventRequest request, HttpRequest http, CompanyEvents events, IDbSession session, HttpResponse response,
            CancellationToken ct) =>
        {
            var view = await events.UpdateAsync(id, http.Headers.IfMatch.FirstOrDefault(), request, ct);
            await session.CommitAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        root.MapPost("/admin-events/{id:guid}/cancel", async (Guid id, CompanyEvents events, IDbSession session, CancellationToken ct) =>
        {
            var view = await events.CancelAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapDelete("/admin-events/{id:guid}", async (Guid id, CompanyEvents events, IDbSession session, CancellationToken ct) =>
        {
            await events.DeleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/admin-events/{id:guid}/responses", async (Guid id, CompanyEvents events, CancellationToken ct) => Results.Ok(await events.ResponsesAsync(id, ct)));

        root.MapGet("/settings", async (CalendarSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));

        root.MapPut("/settings", async (SettingsRequest request, CalendarSettings settings, IDbSession session, CancellationToken ct) =>
        {
            var view = await settings.PutAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        return app;
    }
}
