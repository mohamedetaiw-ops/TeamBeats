using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Attendance;

/// <summary>Clock data imported through spec 017 becomes punches (US3 scenario 2).</summary>
public sealed class ClockDataHandler(AttendanceService attendance) : IMessageHandler<ClockDataImported>
{
    public async Task HandleAsync(ClockDataImported message, CancellationToken ct) => await attendance.ImportClockDataAsync(ct);
}

/// <summary>Attendance endpoints (spec 022 slice 4). Every endpoint needs <c>hr.attendance</c> in the plan.</summary>
public static class AttendanceEndpoints
{
    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapAttendanceEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me/attendance").RequireTenantUser().RequiresCapability(Capabilities.HrAttendance).WithTags("Attendance");
        me.MapGet("", async (string? month, AttendanceService attendance, CancellationToken ct) => Results.Ok(await attendance.MineAsync(month, ct)));
        me.MapPost("/punches", async (PunchRequest request, AttendanceService attendance, IDbSession session, CancellationToken ct) =>
        {
            var view = await attendance.PunchAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/me/attendance/punches/{view.Id}", view);
        });

        me.MapGet("/requests", async (AttendanceRequests requests, CancellationToken ct) => Results.Ok(await requests.MineAsync(ct)));
        me.MapPost("/requests", async (AttendanceRequestInput input, AttendanceRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var view = await requests.CreateAsync(input, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/hr/attendance/requests/{view.Id}", view);
        });

        var hr = app.MapGroup("/api/v1/hr").RequireTenantUser().RequiresCapability(Capabilities.HrAttendance).WithTags("Attendance");
        hr.MapGet("/attendance/requests/{id:guid}", async (Guid id, AttendanceRequests requests, CancellationToken ct) => Results.Ok(await requests.GetAsync(id, ct)));
        hr.MapPost("/attendance/requests/{id:guid}:withdraw", async (Guid id, AttendanceRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var view = await requests.WithdrawAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        hr.MapGet("/attendance/team", async (string? month, AttendanceService attendance, CancellationToken ct) => Results.Ok(await attendance.TeamAsync(month, ct)));
        hr.MapGet("/attendance/people/{userId:guid}", async (Guid userId, string? month, AttendanceService attendance, CancellationToken ct) =>
            Results.Ok(await attendance.PersonAsync(userId, month, ct)));
        hr.MapGet("/attendance/export", async (string? month, AttendanceService attendance, IDbSession session, CancellationToken ct) =>
        {
            var (name, content) = await attendance.ExportAsync(month, ct);
            await session.CommitAsync(ct);
            return Results.File(content, "text/csv; charset=utf-8", name);
        }).InAdminArea();

        hr.MapGet("/attendance-settings", async (AttendanceService attendance, HttpResponse response, CancellationToken ct) =>
        {
            var view = await attendance.SettingsAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();
        hr.MapPut("/attendance-settings", async (AttendanceSettingsRequest request, HttpRequest http, AttendanceService attendance, IDbSession session, CancellationToken ct) =>
        {
            var view = await attendance.UpdateSettingsAsync(IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        hr.MapGet("/work-rules", async (AttendanceService attendance, CancellationToken ct) => Results.Ok(await attendance.ListRulesAsync(ct))).InAdminArea();
        hr.MapPut("/work-rules", async (WorkRuleRequest request, HttpRequest http, AttendanceService attendance, IDbSession session, CancellationToken ct) =>
        {
            var view = await attendance.SaveRuleAsync(IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();
        return app;
    }
}

/// <summary>The Attendance service in the one catalogue (FR-026), shown only where the plan has <c>hr.attendance</c>.</summary>
public sealed class AttendanceModuleServices : TeamBeats.Modules.Services.Contracts.IModuleServiceSource
{
    public const string Key = "hr.attendance";

    public IReadOnlyList<TeamBeats.Modules.Services.Contracts.ModuleServiceRegistration> Services { get; } =
    [
        new(Key, new Dictionary<string, string> { ["en"] = "Attendance", ["ar"] = "الحضور" },
            new Dictionary<string, string> { ["en"] = "Sign in and out and see your month", ["ar"] = "سجّل حضورك وانصرافك واطلع على شهرك" },
            "/attendance", null, Capabilities.HrAttendance, Icon: "clock"),
    ];
}
