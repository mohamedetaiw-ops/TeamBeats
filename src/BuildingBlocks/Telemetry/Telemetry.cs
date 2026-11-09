using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TeamBeats.Primitives;

namespace TeamBeats.Telemetry;

public static class TelemetryExtensions
{
    public const string ActivitySourceName = "TeamBeats";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>OpenTelemetry traces and metrics (constitution XIX). OTLP export when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.</summary>
    public static IServiceCollection AddTeamBeatsTelemetry(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var otlp = !string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                // Module sources are named TeamBeats.{Module}, such as TeamBeats.Evaluation (spec 034 FR-029).
                t.AddSource(ActivitySourceName, $"{ActivitySourceName}.*").AddAspNetCoreInstrumentation();
                if (otlp)
                {
                    t.AddOtlpExporter();
                }
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation().AddMeter("TeamBeats.*");
                if (otlp)
                {
                    m.AddOtlpExporter();
                }
            });
        return services;
    }

    /// <summary>
    /// Accepts a well-formed inbound correlation ID or creates one, echoes it on the response, tags the trace,
    /// and makes it ambient so audit records and outbox messages carry it.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var inbound = context.Request.Headers[Correlation.HeaderName].ToString();
            var id = IsValid(inbound) ? inbound : Correlation.NewId();
            Correlation.Id = id;
            Activity.Current?.SetTag("correlation_id", id);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[Correlation.HeaderName] = id;
                return Task.CompletedTask;
            });
            await next(context);
        });

    private static bool IsValid(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
