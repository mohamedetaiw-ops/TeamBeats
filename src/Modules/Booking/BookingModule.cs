using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

/// <summary>Workplace booking of rooms, desks, parking and equipment (spec 031, module map: Booking).</summary>
public static class BookingModule
{
    public static readonly MigrationSource Migrations = new("booking", typeof(BookingModule).Assembly);

    public static IServiceCollection AddBookingModule(this IServiceCollection services)
    {
        services.AddScoped<Facilities>();
        services.AddScoped<BookingRules>();
        services.AddScoped<BookingCancellations>();
        services.AddScoped<Bookings>();
        services.AddScoped<BookingLeavers>();
        services.AddScoped<BookingApprovals>();
        services.AddScoped<Workflow.Contracts.ITaskSource>(sp => sp.GetRequiredService<BookingApprovals>());
        services.AddMessageHandler<Workflow.Contracts.UserTaskCreated, BookingApprovals>("booking.approval_waiting");
        services.AddScoped<BookingMaintenance>();
        services.AddScoped<BookingReports>();
        services.AddMessageHandler<TenantMembershipDisabled, BookingLeavers>("booking.leaver");
        return services;
    }

    private static async Task<IResult> CommitAsync<T>(IDbSession session, T value, CancellationToken ct)
    {
        await session.CommitAsync(ct);
        return Results.Ok(value);
    }

    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/booking").RequireTenantUser().WithTags("Booking");

        root.MapGet("/rights", async (Facilities facilities, CancellationToken ct) => Results.Ok(await facilities.RightsAsync(ct)));

        // ---- Finding and booking (US2) ----

        root.MapGet("/sites", async (Facilities facilities, CancellationToken ct) => Results.Ok(await facilities.SitesAsync(false, ct)));

        root.MapGet("/sites/{id:guid}/search", async (Guid id, string? kind, DateOnly? date, TimeOnly? start, TimeOnly? end, int? capacity, string? attributes, Guid? zone,
            Bookings bookings, CancellationToken ct) => Results.Ok(await bookings.SearchAsync(id, kind, date, start, end, capacity, attributes, zone, ct)));

        root.MapGet("/resources/{id:guid}/availability", async (Guid id, DateOnly date, Bookings bookings, CancellationToken ct) =>
            Results.Ok(await bookings.AvailabilityAsync(id, date, ct)));

        root.MapPost("/bookings", async (BookRequest request, Bookings bookings, IDbSession session, CancellationToken ct) =>
        {
            var result = await bookings.BookAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/booking/bookings/{result.Bookings[0].Id}", result);
        });

        root.MapPost("/bookings:team", async (TeamBookRequest request, Bookings bookings, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await bookings.BookTeamAsync(request, ct), ct));

        root.MapGet("/bookings/mine", async (Bookings bookings, CancellationToken ct) => Results.Ok(await bookings.MineAsync(ct)));

        root.MapGet("/bookings/{id:guid}", async (Guid id, Bookings bookings, CancellationToken ct) => Results.Ok(await bookings.GetAsync(id, ct)));

        root.MapPost("/bookings/{id:guid}:cancel", async (Guid id, bool? series, CancelRequest? request, Bookings bookings, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await bookings.CancelAsync(id, series ?? false, request, ct), ct));

        // ---- Check-in (US4) ----

        root.MapPost("/bookings/{id:guid}:check-in", async (Guid id, string? via, Bookings bookings, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await bookings.CheckInAsync(id, via, ct), ct));

        root.MapGet("/scan/{code}", async (string code, Bookings bookings, CancellationToken ct) => Results.Ok(await bookings.ScanAsync(code, ct)));

        root.MapPost("/scan/{code}:check-in", async (string code, Bookings bookings, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await bookings.ScanCheckInAsync(code, ct), ct));

        root.MapGet("/sites/{id:guid}/kiosk", async (Guid id, Bookings bookings, CancellationToken ct) => Results.Ok(await bookings.KioskAsync(id, ct)));

        root.MapGet("/bookings/{id:guid}/calendar.ics", async (Guid id, Bookings bookings, RequestLocale locale, CancellationToken ct) =>
        {
            var (name, content) = await bookings.CalendarFileAsync(id, locale.Locale, ct);
            return Results.File(content, "text/calendar; charset=utf-8", name);
        });

        // ---- Sites, resources and rules (US1) ----

        root.MapGet("/reports/sites", async (BookingReports reports, CancellationToken ct) => Results.Ok(await reports.SitesAsync(ct)));

        root.MapGet("/reports/utilization", async (Guid site, DateOnly? from, DateOnly? to, string? groupBy, BookingReports reports, CancellationToken ct) =>
            Results.Ok(await reports.UtilizationAsync(site, from, to, groupBy, ct)));

        var manage = root.MapGroup("/manage");

        manage.MapGet("/sites", async (Facilities facilities, CancellationToken ct) => Results.Ok(await facilities.SitesAsync(true, ct)));

        manage.MapPost("/sites", async (SiteRequest request, Facilities facilities, IDbSession session, CancellationToken ct) =>
        {
            var view = await facilities.CreateSiteAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/booking/manage/sites/{view.Id}", view);
        });

        manage.MapGet("/sites/{id:guid}", async (Guid id, Facilities facilities, CancellationToken ct) => Results.Ok(await facilities.DetailAsync(id, ct)));

        manage.MapPut("/sites/{id:guid}", async (Guid id, SiteRequest request, Facilities facilities, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await facilities.UpdateSiteAsync(id, request, ct), ct));

        manage.MapDelete("/sites/{id:guid}", async (Guid id, Facilities facilities, IDbSession session, CancellationToken ct) =>
        {
            await facilities.RemoveSiteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        manage.MapPost("/sites/{id:guid}/floors", async (Guid id, PlaceRequest request, Facilities facilities, IDbSession session, CancellationToken ct) =>
        {
            var view = await facilities.AddFloorAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/booking/manage/sites/{id}", view);
        });

        manage.MapPost("/sites/{id:guid}/floors/{floorId:guid}/zones", async (Guid id, Guid floorId, PlaceRequest request, Facilities facilities, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await facilities.AddZoneAsync(id, floorId, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/booking/manage/sites/{id}", view);
        });

        manage.MapGet("/sites/{id:guid}/labels", async (Guid id, Facilities facilities, CancellationToken ct) => Results.Ok(await facilities.LabelsAsync(id, ct)));

        manage.MapGet("/resources/{id:guid}/qr.svg", async (Guid id, Facilities facilities, CancellationToken ct) =>
            Results.Text(await facilities.QrSvgAsync(id, ct), "image/svg+xml; charset=utf-8"));

        manage.MapPost("/resources", async (ResourceRequest request, Facilities facilities, IDbSession session, CancellationToken ct) =>
        {
            var view = await facilities.CreateResourceAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/booking/manage/resources/{view.Id}", view);
        });

        manage.MapPut("/resources/{id:guid}", async (Guid id, ResourceRequest request, Facilities facilities, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await facilities.UpdateResourceAsync(id, request, ct), ct));

        manage.MapDelete("/resources/{id:guid}", async (Guid id, Facilities facilities, IDbSession session, CancellationToken ct) =>
        {
            await facilities.RemoveResourceAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        manage.MapGet("/resources/{id:guid}/rule", async (Guid id, BookingRules rules, CancellationToken ct) => Results.Ok(await rules.ResourceRuleAsync(id, ct)));

        manage.MapPut("/resources/{id:guid}/rule", async (Guid id, RuleRequest request, BookingRules rules, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await rules.SetResourceRuleAsync(id, request, ct), ct));

        manage.MapGet("/rules/{kind}", async (string kind, Guid? site, BookingRules rules, CancellationToken ct) => Results.Ok(await rules.KindRuleAsync(kind, site, ct)));

        manage.MapPut("/rules/{kind}", async (string kind, Guid? site, RuleRequest request, BookingRules rules, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await rules.SetKindRuleAsync(kind, site, request, ct), ct));

        manage.MapGet("/settings", async (BookingRules rules, CancellationToken ct) => Results.Ok(await rules.GetSettingsAsync(ct)));

        manage.MapPut("/settings", async (SettingsRequest request, BookingRules rules, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await rules.SetSettingsAsync(request, ct), ct));

        return app;
    }
}

/// <summary>Cancels the future bookings of a person whose membership ended (spec 031 FR-016).</summary>
public sealed class BookingLeavers(Bookings bookings) : IMessageHandler<TenantMembershipDisabled>
{
    public Task HandleAsync(TenantMembershipDisabled message, CancellationToken ct) => bookings.CancelForLeaverAsync(message.UserId, ct);
}
