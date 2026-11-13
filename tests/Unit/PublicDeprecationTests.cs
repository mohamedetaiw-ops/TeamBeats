using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using TeamBeats.Modules.PublicApi.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 035 FR-014, US7 scenario 2 (slice 5c): a deprecated public operation answers with <c>Deprecation</c>,
/// <c>Sunset</c> and a link to its replacement, errors included, and cannot be withdrawn less than 12 months after
/// it was deprecated.
/// </summary>
public sealed class PublicDeprecationTests
{
    private static readonly DateOnly Announced = new(2027, 1, 15);
    private static readonly DateOnly Sunset = new(2028, 1, 15);

    private static async Task<WebApplication> AppAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        // As in the API host, an exception becomes an error answer after the response was cleared.
        app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = context => { context.Response.StatusCode = 500; return Task.CompletedTask; } });
        app.MapGet("/api/public/v1/old", () => Results.Ok(new { ok = true })).Deprecated(Announced, Sunset, "/api/public/v1/new");
        app.MapGet("/api/public/v1/old-failing", IResult () => throw new InvalidOperationException("boom")).Deprecated(Announced, Sunset, null);
        app.MapGet("/api/public/v1/new", () => Results.Ok(new { ok = true }));
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task A_deprecated_operation_answers_with_its_deprecation_sunset_and_replacement()
    {
        await using var app = await AppAsync();
        var client = app.GetTestClient();

        var old = await client.GetAsync("/api/public/v1/old");
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Equal("@" + new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), old.Headers.GetValues("Deprecation").Single());
        Assert.Equal("Sat, 15 Jan 2028 00:00:00 GMT", old.Headers.GetValues("Sunset").Single());
        Assert.Equal("</api/public/v1/new>; rel=\"successor-version\"", old.Headers.GetValues("Link").Single());

        // An error of a deprecated operation still tells the caller.
        var failing = await client.GetAsync("/api/public/v1/old-failing");
        Assert.Equal(HttpStatusCode.InternalServerError, failing.StatusCode);
        Assert.True(failing.Headers.Contains("Sunset"));

        var current = await client.GetAsync("/api/public/v1/new");
        Assert.False(current.Headers.Contains("Deprecation"));
        Assert.False(current.Headers.Contains("Sunset"));
    }

    [Fact]
    public void Notice_is_at_least_12_months_and_the_replacement_is_a_public_path()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        Assert.Throws<ArgumentException>(() => app.MapGet("/api/public/v1/a", () => "a").Deprecated(Announced, Announced.AddMonths(11), null));
        Assert.Throws<ArgumentException>(() => app.MapGet("/api/public/v1/b", () => "b").Deprecated(Announced, Sunset, "/api/v1/internal"));
        app.MapGet("/api/public/v1/c", () => "c").Deprecated(Announced, Announced.AddMonths(12), null);
    }

    [Fact]
    public void The_headers_name_the_announcement_and_the_withdrawal()
    {
        var headers = PublicDeprecation.Headers(new PublicDeprecationMetadata(Announced, Sunset, null));
        Assert.Equal(["Deprecation", "Sunset"], headers.Select(h => h.Name));
    }
}
