using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

/// <summary>Communities and engagement (spec 023, module map: Engagement).</summary>
public static class EngagementModule
{
    public static readonly MigrationSource Migrations = new("engagement", typeof(EngagementModule).Assembly);

    public static IServiceCollection AddEngagementModule(this IServiceCollection services)
    {
        services.AddScoped<Communities>();
        services.AddScoped<Posts>();
        services.AddScoped<Search.Contracts.ISearchSource, EngagementSearchSource>();
        return services;
    }

    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapEngagementEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/engagement").RequireTenantUser().WithTags("Engagement");

        // ---- Communities ----

        root.MapGet("/communities", async (string? q, Communities communities, CancellationToken ct) => Results.Ok(await communities.ListAsync(q, ct)));

        root.MapGet("/rights", async (Communities communities, CancellationToken ct) => Results.Ok(await communities.RightsAsync(ct)));

        root.MapPost("/communities", async (CommunityRequest request, Communities communities, IDbSession session, CancellationToken ct) =>
        {
            var view = await communities.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/communities/{view.Id}", view);
        });

        root.MapGet("/communities/{id:guid}", async (Guid id, Communities communities, HttpResponse response, CancellationToken ct) =>
        {
            var view = await communities.GetAsync(id, ct);
            response.Headers.ETag = Communities.ETag(view.Version);
            return Results.Ok(view);
        });

        root.MapPut("/communities/{id:guid}", async (Guid id, CommunityRequest request, HttpRequest http, Communities communities, IDbSession session, CancellationToken ct) =>
        {
            var view = await communities.UpdateAsync(id, request, IfMatch(http), ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = Communities.ETag(view.Version);
            return Results.Ok(view);
        });

        root.MapPost("/communities/{id:guid}:archive", async (Guid id, Communities communities, IDbSession session, CancellationToken ct) =>
        {
            var view = await communities.ArchiveAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/communities/{id:guid}:join", async (Guid id, Communities communities, IDbSession session, CancellationToken ct) =>
        {
            var view = await communities.JoinAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/communities/{id:guid}:leave", async (Guid id, Communities communities, IDbSession session, CancellationToken ct) =>
        {
            var view = await communities.LeaveAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/communities/{id:guid}/members", async (Guid id, Communities communities, CancellationToken ct) => Results.Ok(await communities.MembersAsync(id, ct)));

        root.MapPut("/communities/{id:guid}/members", async (Guid id, MemberRequest request, Communities communities, IDbSession session, CancellationToken ct) =>
        {
            var members = await communities.SetMemberAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(members);
        });

        root.MapDelete("/communities/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, Communities communities, IDbSession session, CancellationToken ct) =>
        {
            var members = await communities.RemoveMemberAsync(id, userId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(members);
        });

        root.MapPost("/communities/{id:guid}/channels", async (Guid id, ChannelRequest request, Communities communities, IDbSession session, CancellationToken ct) =>
        {
            var view = await communities.AddChannelAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/communities/{id}", view);
        });

        // ---- Posts ----

        root.MapGet("/communities/{id:guid}/posts", async (Guid id, Guid? channelId, string? cursor, int? limit, Posts posts, CancellationToken ct) =>
            Results.Ok(await posts.FeedAsync(id, channelId, cursor, limit, ct)));

        root.MapPost("/communities/{id:guid}/posts", async (Guid id, PostRequest request, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            var saved = await posts.CreateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/posts/{saved.Post.Id}", saved);
        });

        root.MapGet("/posts/{id:guid}", async (Guid id, Posts posts, CancellationToken ct) => Results.Ok(await posts.GetAsync(id, ct)));

        root.MapPut("/posts/{id:guid}", async (Guid id, PostRequest request, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            var saved = await posts.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(saved);
        });

        root.MapDelete("/posts/{id:guid}", async (Guid id, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            await posts.DeleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapPost("/posts/{id:guid}:pin", async (Guid id, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            var view = await posts.PinAsync(id, true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/posts/{id:guid}:unpin", async (Guid id, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            var view = await posts.PinAsync(id, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/posts/{id:guid}:follow", async (Guid id, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            var view = await posts.SetFollowingAsync(id, true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/posts/{id:guid}:unfollow", async (Guid id, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            var view = await posts.SetFollowingAsync(id, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPut("/posts/{id:guid}/reaction", async (Guid id, ReactionRequest request, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            var counts = await posts.ReactAsync("post", id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(counts);
        });

        root.MapGet("/posts/{id:guid}/comments", async (Guid id, Posts posts, CancellationToken ct) => Results.Ok(await posts.CommentsAsync(id, ct)));

        root.MapPost("/posts/{id:guid}/comments", async (Guid id, CommentRequest request, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            var saved = await posts.CommentAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/posts/{id}/comments", saved);
        });

        root.MapDelete("/comments/{id:guid}", async (Guid id, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            await posts.DeleteCommentAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapPut("/comments/{id:guid}/reaction", async (Guid id, ReactionRequest request, Posts posts, IDbSession session, CancellationToken ct) =>
        {
            var counts = await posts.ReactAsync("comment", id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(counts);
        });

        root.MapGet("/hashtags/{tag}", async (string tag, Posts posts, CancellationToken ct) => Results.Ok(await posts.HashtagAsync(tag, ct)));

        return app;
    }
}
