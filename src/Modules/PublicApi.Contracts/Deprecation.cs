using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TeamBeats.Modules.PublicApi.Contracts;

/// <summary>
/// A public operation on its way out (spec 035 FR-014, US7 scenario 2): when it was announced, the day it stops working,
/// at least 12 months later, and the operation that replaces it.
/// </summary>
public sealed record PublicDeprecationMetadata(DateOnly Announced, DateOnly Sunset, string? Replacement);

/// <summary>
/// Marks public operations deprecated. Every answer of such an operation, errors included, carries <c>Deprecation</c>
/// (RFC 9745, the announcement as <c>@</c> and Unix seconds), <c>Sunset</c> (RFC 8594, an HTTP date) and, when there is
/// one, a <c>Link</c> to the replacement with <c>rel="successor-version"</c>. The OpenAPI document marks it deprecated
/// and the developer page lists it with its replacement and date.
/// </summary>
public static class PublicDeprecation
{
    /// <summary>The shortest notice the public API gives before an operation stops working.</summary>
    public const int NoticeMonths = 12;

    public static TBuilder Deprecated<TBuilder>(this TBuilder builder, DateOnly announced, DateOnly sunset, string? replacement)
        where TBuilder : IEndpointConventionBuilder
    {
        if (sunset < announced.AddMonths(NoticeMonths))
        {
            throw new ArgumentException($"A public operation is withdrawn at least {NoticeMonths} months after it is deprecated.", nameof(sunset));
        }
        if (replacement is not null && !replacement.StartsWith(PublicEndpoints.Prefix + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The replacement is a public API path.", nameof(replacement));
        }
        var metadata = new PublicDeprecationMetadata(announced, sunset, replacement);
        builder.WithMetadata(metadata, new ObsoleteAttribute($"Deprecated on {announced:yyyy-MM-dd}; withdrawn on {sunset:yyyy-MM-dd}."));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var response = context.HttpContext.Response;
            response.OnStarting(() =>
            {
                foreach (var (name, value) in Headers(metadata))
                {
                    response.Headers[name] = value;
                }
                return Task.CompletedTask;
            });
            return await next(context);
        });
        return builder;
    }

    /// <summary>The headers a deprecated operation answers with.</summary>
    public static IReadOnlyList<(string Name, string Value)> Headers(PublicDeprecationMetadata metadata)
    {
        var announced = new DateTimeOffset(metadata.Announced.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var sunset = new DateTimeOffset(metadata.Sunset.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var list = new List<(string, string)>
        {
            ("Deprecation", "@" + announced.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ("Sunset", sunset.ToString("r", CultureInfo.InvariantCulture)),
        };
        if (metadata.Replacement is { } replacement)
        {
            list.Add(("Link", $"<{replacement}>; rel=\"successor-version\""));
        }
        return list;
    }
}
