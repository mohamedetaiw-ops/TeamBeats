using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr;

/// <summary>Leave, attendance and HR workflows (spec 022, module map: Hr). R1 has working calendars only.</summary>
public static class HrModule
{
    public static readonly MigrationSource Migrations = new("hr", typeof(HrModule).Assembly);

    public static IServiceCollection AddHrModule(this IServiceCollection services)
    {
        services.AddScoped<CalendarService>();
        services.AddScoped<WorkingCalendar>();
        services.AddScoped<IWorkingCalendar>(sp => sp.GetRequiredService<WorkingCalendar>());
        return services;
    }

    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapHrEndpoints(this IEndpointRouteBuilder app)
    {
        var hr = app.MapGroup("/api/v1/hr").RequireTenantUser().WithTags("HR: working calendars");

        hr.MapGet("/calendars", async (CalendarService calendars, IDbSession session, CancellationToken ct) =>
        {
            var list = await calendars.ListAsync(ct);
            await session.CommitAsync(ct); // The tenant default is created on first use.
            return Results.Ok(list);
        });

        hr.MapPost("/calendars", async (CalendarRequest request, CalendarService calendars, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var view = await calendars.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Created($"/api/v1/hr/calendars/{view.Id}", view);
        });

        hr.MapGet("/calendars/{id:guid}", async (Guid id, CalendarService calendars, HttpResponse response, CancellationToken ct) =>
        {
            var view = await calendars.GetAsync(id, ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        hr.MapPut("/calendars/{id:guid}", async (Guid id, CalendarRequest request, HttpRequest http, CalendarService calendars, IDbSession session, CancellationToken ct) =>
        {
            var view = await calendars.UpdateAsync(id, IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        hr.MapDelete("/calendars/{id:guid}", async (Guid id, CalendarService calendars, IDbSession session, CancellationToken ct) =>
        {
            await calendars.DeleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        hr.MapPut("/calendars/{id:guid}/holidays", async (Guid id, HolidaysRequest request, HttpRequest http, CalendarService calendars, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await calendars.SetHolidaysAsync(id, IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        hr.MapGet("/assignments", async (CalendarService calendars, CancellationToken ct) => Results.Ok(await calendars.ListAssignmentsAsync(ct)));

        hr.MapPut("/assignments/{orgUnitId:guid}", async (Guid orgUnitId, AssignmentRequest request, CalendarService calendars, IDbSession session, CancellationToken ct) =>
        {
            var view = await calendars.AssignAsync(orgUnitId, request, ct);
            await session.CommitAsync(ct);
            return view is null ? Results.NoContent() : Results.Ok(view);
        });

        hr.MapGet("/working-days", async (Guid? orgUnitId, Guid? calendarId, DateOnly from, DateOnly to, CalendarService calendars, WorkingCalendar resolver,
            CancellationToken ct) => Results.Ok(await calendars.PreviewAsync(orgUnitId, calendarId, from, to, resolver, ct)));

        return app;
    }
}
