using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

/// <summary>Communities and engagement (spec 023, module map: Engagement).</summary>
public static class EngagementModule
{
    public static readonly MigrationSource Migrations = new("engagement", typeof(EngagementModule).Assembly);

    public static IServiceCollection AddEngagementModule(this IServiceCollection services)
    {
        services.AddSingleton<Experience.Contracts.IComponentRegistration, EngagementComponents>();
        services.AddScoped<MediaChecks>();
        services.AddScoped<EngagementActivity>();
        services.AddScoped<PostAttachments>();
        services.AddScoped<Communities>();
        services.AddScoped<Posts>();
        services.AddScoped<KeywordFilters>();
        services.AddScoped<Moderation>();
        services.AddScoped<SpeakUp>();
        services.AddScoped<Polls>();
        services.AddScoped<Surveys>();
        services.AddScoped<Recognition>();
        services.AddScoped<Awards>();
        services.AddScoped<Celebrations>();
        services.AddScoped<Galleries>();
        services.AddMessageHandler<Contracts.PostPublished, Points>("engagement.points_post");
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

        // Images and files for posts (FR-003): uploaded first, joined to the post when it is saved.
        root.MapPost("/communities/{id:guid}/attachments", async (Guid id, string? name, HttpRequest http, PostAttachments attachments, IDbSession session, CancellationToken ct) =>
        {
            var limit = Math.Max(PhotoFiles.MaxBytes, PostAttachments.MaxFileBytes);
            var buffer = new byte[limit + 1];
            int read, total = 0;
            while ((read = await http.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    throw Primitives.AppException.Validation("file", "validation.engagement.attachment_size");
                }
            }
            var view = await attachments.UploadAsync(id, name, http.ContentType, buffer[..total], ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/attachments/{view.Id}/file", view);
        }).DisableAntiforgery();

        root.MapGet("/attachments/{id:guid}/file", async (Guid id, Posts posts, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type, name) = await posts.OpenAttachmentAsync(id, ct);
            response.Headers.CacheControl = "private, no-cache";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            // Images show in the post; documents always download, so none of them runs in the intranet's pages.
            return type.StartsWith("image/", StringComparison.Ordinal) ? Results.File(content, type) : Results.File(content, type, name);
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

        // ---- Moderation (US8) ----

        root.MapPost("/posts/{id:guid}:report", async (Guid id, ReportRequest request, Moderation moderation, IDbSession session, CancellationToken ct) =>
        {
            await moderation.ReportAsync("post", id, request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapPost("/comments/{id:guid}:report", async (Guid id, ReportRequest request, Moderation moderation, IDbSession session, CancellationToken ct) =>
        {
            await moderation.ReportAsync("comment", id, request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/moderation", async (Guid? communityId, Moderation moderation, CancellationToken ct) => Results.Ok(await moderation.QueueAsync(communityId, ct)));

        root.MapPost("/moderation/{type}/{id:guid}:approve", async (string type, Guid id, Moderation moderation, IDbSession session, CancellationToken ct) =>
        {
            await moderation.ApproveAsync(type, id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapPost("/moderation/{type}/{id:guid}:remove", async (string type, Guid id, RemoveRequest request, Moderation moderation, IDbSession session, CancellationToken ct) =>
        {
            await moderation.RemoveAsync(type, id, request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // ---- Polls (US3) ----

        root.MapGet("/communities/{id:guid}/polls", async (Guid id, Polls polls, IDbSession session, CancellationToken ct) =>
        {
            var list = await polls.ListAsync(id, ct);
            // Reading can close polls that passed their end time.
            await session.CommitAsync(ct);
            return Results.Ok(list);
        });

        root.MapPost("/communities/{id:guid}/polls", async (Guid id, PollRequest request, Polls polls, IDbSession session, CancellationToken ct) =>
        {
            var view = await polls.CreateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/polls/{view.Id}", view);
        });

        root.MapGet("/polls/{id:guid}", async (Guid id, Polls polls, IDbSession session, CancellationToken ct) =>
        {
            var view = await polls.GetAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPut("/polls/{id:guid}/vote", async (Guid id, VoteRequest request, Polls polls, IDbSession session, CancellationToken ct) =>
        {
            var view = await polls.VoteAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/polls/{id:guid}/options", async (Guid id, PollOptionsRequest request, Polls polls, IDbSession session, CancellationToken ct) =>
        {
            var view = await polls.AddOptionsAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/polls/{id:guid}:close", async (Guid id, Polls polls, IDbSession session, CancellationToken ct) =>
        {
            var view = await polls.CloseAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // ---- Surveys (US3) ----

        // Reading can close surveys that passed their end date, so the reads commit too.
        root.MapGet("/surveys", async (Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var list = await surveys.ListAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(list);
        });

        root.MapPost("/surveys", async (SurveyRequest request, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var view = await surveys.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/surveys/{view.Id}", view);
        });

        root.MapGet("/surveys/{id:guid}", async (Guid id, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var view = await surveys.GetAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPut("/surveys/{id:guid}", async (Guid id, SurveyRequest request, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var view = await surveys.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/surveys/{id:guid}:publish", async (Guid id, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var view = await surveys.PublishAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/surveys/{id:guid}:close", async (Guid id, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var view = await surveys.CloseAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/surveys/{id:guid}/responses", async (Guid id, SurveyResponseRequest request, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var view = await surveys.RespondAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/surveys/{id:guid}/responses", async (Guid id, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var list = await surveys.ResponsesAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(list);
        });

        root.MapGet("/surveys/{id:guid}/results", async (Guid id, Guid? group, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var results = await surveys.ResultsAsync(id, group, ct);
            await session.CommitAsync(ct);
            return Results.Ok(results);
        });

        root.MapGet("/surveys/{id:guid}/results.csv", async (Guid id, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var (name, content) = await surveys.ExportAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.File(content, "text/csv; charset=utf-8", name);
        });

        root.MapGet("/settings/surveys", async (Surveys surveys, HttpResponse response, CancellationToken ct) =>
        {
            var view = await surveys.GetSettingsAsync(ct);
            response.Headers.ETag = Surveys.SettingsETag(view.Version);
            return Results.Ok(view);
        });

        root.MapPut("/settings/surveys", async (SurveySettingsRequest request, HttpRequest http, Surveys surveys, IDbSession session, CancellationToken ct) =>
        {
            var view = await surveys.SetSettingsAsync(request, IfMatch(http), ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = Surveys.SettingsETag(view.Version);
            return Results.Ok(view);
        });

        // ---- Recognition and points (US4) ----

        root.MapGet("/recognition/options", async (Recognition recognition, CancellationToken ct) => Results.Ok(await recognition.OptionsAsync(ct)));

        root.MapGet("/recognitions", async (string? cursor, Recognition recognition, CancellationToken ct) => Results.Ok(await recognition.WallAsync(cursor, ct)));

        root.MapPost("/recognitions", async (RecognitionRequest request, Recognition recognition, IDbSession session, CancellationToken ct) =>
        {
            var view = await recognition.GiveAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/recognitions/{view.Id}", view);
        });

        root.MapDelete("/recognitions/{id:guid}", async (Guid id, Recognition recognition, IDbSession session, CancellationToken ct) =>
        {
            await recognition.RemoveAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/people/{userId:guid}/recognitions", async (Guid userId, Recognition recognition, CancellationToken ct) => Results.Ok(await recognition.ReceivedAsync(userId, ct)));

        root.MapGet("/recognition/preferences", async (Recognition recognition, CancellationToken ct) => Results.Ok(await recognition.PreferenceAsync(ct)));

        root.MapPut("/recognition/preferences", async (RecognitionPreferenceRequest request, Recognition recognition, IDbSession session, CancellationToken ct) =>
        {
            var view = await recognition.SetPreferenceAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/settings/recognition", async (Recognition recognition, CancellationToken ct) => Results.Ok(await recognition.CatalogueAsync(ct)));

        root.MapPut("/settings/recognition", async (RecognitionCatalogueRequest request, Recognition recognition, IDbSession session, CancellationToken ct) =>
        {
            var view = await recognition.SetCatalogueAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // ---- Team and star of the period (US5) ----

        root.MapGet("/awards", async (Awards awards, CancellationToken ct) => Results.Ok(await awards.ListAsync(ct)));

        root.MapPost("/awards", async (AwardPeriodRequest request, Awards awards, IDbSession session, CancellationToken ct) =>
        {
            var view = await awards.OpenAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/awards/{view.Id}", view);
        });

        root.MapGet("/awards/{id:guid}", async (Guid id, Awards awards, CancellationToken ct) => Results.Ok(await awards.GetAsync(id, ct)));

        root.MapPut("/awards/{id:guid}/nomination", async (Guid id, AwardNominationRequest request, Awards awards, IDbSession session, CancellationToken ct) =>
        {
            var view = await awards.NominateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/awards/{id:guid}/nominations", async (Guid id, Awards awards, CancellationToken ct) => Results.Ok(await awards.NominationsAsync(id, ct)));

        root.MapPost("/awards/{id:guid}/winners", async (Guid id, AwardWinnersRequest request, Awards awards, IDbSession session, CancellationToken ct) =>
        {
            var view = await awards.DecideAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // ---- Celebrations (US6) ----

        root.MapGet("/celebrations", async (string? scope, Celebrations celebrations, CancellationToken ct) => Results.Ok(await celebrations.ListAsync(scope, ct)));

        root.MapPost("/celebrations/{userId:guid}/greetings", async (Guid userId, GreetingRequest request, Celebrations celebrations, IDbSession session, CancellationToken ct) =>
        {
            await celebrations.GreetAsync(userId, request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/celebrations/preferences", async (Celebrations celebrations, CancellationToken ct) => Results.Ok(await celebrations.PreferenceAsync(ct)));

        root.MapPut("/celebrations/preferences", async (CelebrationPreferenceRequest request, Celebrations celebrations, IDbSession session, CancellationToken ct) =>
        {
            var view = await celebrations.SetPreferenceAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/settings/celebrations", async (Celebrations celebrations, HttpResponse response, CancellationToken ct) =>
        {
            var view = await celebrations.GetSettingsAsync(ct);
            response.Headers.ETag = Celebrations.SettingsETag(view.Version);
            return Results.Ok(view);
        });

        root.MapPut("/settings/celebrations", async (CelebrationSettingsRequest request, HttpRequest http, Celebrations celebrations, IDbSession session, CancellationToken ct) =>
        {
            var view = await celebrations.SetSettingsAsync(request, IfMatch(http), ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = Celebrations.SettingsETag(view.Version);
            return Results.Ok(view);
        });

        // ---- Galleries (US7) ----

        root.MapGet("/communities/{id:guid}/albums", async (Guid id, Galleries galleries, CancellationToken ct) => Results.Ok(await galleries.AlbumsAsync(id, ct)));

        root.MapPost("/communities/{id:guid}/albums", async (Guid id, AlbumRequest request, Galleries galleries, IDbSession session, CancellationToken ct) =>
        {
            var view = await galleries.CreateAlbumAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/albums/{view.Id}", view);
        });

        root.MapGet("/albums/{id:guid}", async (Guid id, Galleries galleries, CancellationToken ct) => Results.Ok(await galleries.AlbumPageAsync(id, ct)));

        root.MapPost("/albums/{id:guid}/photos", async (Guid id, string? caption, HttpRequest http, Galleries galleries, IDbSession session, CancellationToken ct) =>
        {
            var buffer = new byte[PhotoFiles.MaxBytes + 1];
            int read, total = 0;
            while ((read = await http.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total > PhotoFiles.MaxBytes)
                {
                    throw Primitives.AppException.Validation("file", "validation.engagement.photo_size");
                }
            }
            var view = await galleries.UploadAsync(id, caption, http.ContentType, buffer[..total], ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/engagement/photos/{view.Id}", view);
        }).DisableAntiforgery();

        root.MapGet("/photos/{id:guid}/file", async (Guid id, Galleries galleries, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type) = await galleries.OpenAsync(id, ct);
            response.Headers.CacheControl = "private, no-cache";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(content, type);
        });

        root.MapPost("/photos/{id:guid}:approve", async (Guid id, Galleries galleries, IDbSession session, CancellationToken ct) =>
        {
            var view = await galleries.ApproveAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapDelete("/photos/{id:guid}", async (Guid id, Galleries galleries, IDbSession session, CancellationToken ct) =>
        {
            await galleries.RemoveAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapPut("/photos/{id:guid}/tags", async (Guid id, PhotoTagsRequest request, Galleries galleries, IDbSession session, CancellationToken ct) =>
        {
            var view = await galleries.SetTagsAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapDelete("/photos/{id:guid}/tags/me", async (Guid id, Galleries galleries, IDbSession session, CancellationToken ct) =>
        {
            await galleries.UntagMeAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/points/me", async (Points points, CancellationToken ct) => Results.Ok(await points.MineAsync(ct)));

        root.MapGet("/leaderboard", async (string? period, Guid? unit, Points points, CancellationToken ct) => Results.Ok(await points.LeaderboardAsync(period, unit, ct)));

        root.MapGet("/settings/points", async (Points points, HttpResponse response, CancellationToken ct) =>
        {
            var view = await points.GetSettingsAsync(ct);
            response.Headers.ETag = Points.ETag(view.Version);
            return Results.Ok(view);
        });

        root.MapPut("/settings/points", async (PointsSettingsRequest request, HttpRequest http, Points points, IDbSession session, CancellationToken ct) =>
        {
            var view = await points.SetSettingsAsync(request, IfMatch(http), ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = Points.ETag(view.Version);
            return Results.Ok(view);
        });

        // ---- Speak Up (US2) ----

        root.MapPost("/communities/{id:guid}/anonymous-posts", async (Guid id, AnonymousPostRequest request, SpeakUp speakUp, IDbSession session, CancellationToken ct) =>
        {
            var receipt = await speakUp.SubmitAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(receipt);
        });

        // The token travels in the body, not the address, so it does not land in request logs.
        root.MapPost("/speak-up/reply", async (AnonymousReplyRequest request, SpeakUp speakUp, IDbSession session, CancellationToken ct) =>
        {
            var reply = await speakUp.ReplyAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(reply);
        });

        root.MapPost("/moderation/anonymous/{id:guid}:publish", async (Guid id, AnonymousPublishRequest request, SpeakUp speakUp, IDbSession session, CancellationToken ct) =>
        {
            var postId = await speakUp.PublishAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(new { postId });
        });

        root.MapPost("/moderation/anonymous/{id:guid}:answer", async (Guid id, AnonymousAnswerRequest request, SpeakUp speakUp, IDbSession session, CancellationToken ct) =>
        {
            await speakUp.AnswerAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapPost("/moderation/anonymous/{id:guid}:decline", async (Guid id, AnonymousDeclineRequest request, SpeakUp speakUp, IDbSession session, CancellationToken ct) =>
        {
            await speakUp.DeclineAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/communities/{id:guid}/mutes", async (Guid id, Moderation moderation, CancellationToken ct) => Results.Ok(await moderation.MutesAsync(id, ct)));

        root.MapPut("/communities/{id:guid}/mutes", async (Guid id, MuteRequest request, Moderation moderation, IDbSession session, CancellationToken ct) =>
        {
            var mutes = await moderation.MuteAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(mutes);
        });

        root.MapDelete("/communities/{id:guid}/mutes/{userId:guid}", async (Guid id, Guid userId, Moderation moderation, IDbSession session, CancellationToken ct) =>
        {
            var mutes = await moderation.UnmuteAsync(id, userId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(mutes);
        });

        root.MapGet("/settings/keywords", async (KeywordFilters filters, HttpResponse response, CancellationToken ct) =>
        {
            var view = await filters.GetAsync(ct);
            response.Headers.ETag = KeywordFilters.ETag(view.Version);
            return Results.Ok(view);
        });

        root.MapPut("/settings/keywords", async (KeywordFilterRequest request, HttpRequest http, KeywordFilters filters, IDbSession session, CancellationToken ct) =>
        {
            var view = await filters.SetAsync(request, IfMatch(http), ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = KeywordFilters.ETag(view.Version);
            return Results.Ok(view);
        });

        return app;
    }
}
