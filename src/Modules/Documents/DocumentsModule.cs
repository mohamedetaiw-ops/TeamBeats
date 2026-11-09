using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>Document management and the knowledge base (spec 011, module map: Documents).</summary>
public static class DocumentsModule
{
    public static readonly MigrationSource Migrations = new("documents", typeof(DocumentsModule).Assembly);

    public static IServiceCollection AddDocumentsModule(this IServiceCollection services)
    {
        services.AddScoped<Libraries>();
        services.AddScoped<DocumentFiles>();
        services.AddScoped<DocumentAccess>();
        services.AddScoped<DocumentVersions>();
        services.AddScoped<DocumentPreviews>();
        services.AddScoped<DocumentReleases>();
        services.AddScoped<DocumentTaskSource>();
        services.AddScoped<Workflow.Contracts.ITaskSource>(sp => sp.GetRequiredService<DocumentTaskSource>());
        services.AddMessageHandler<Workflow.Contracts.UserTaskCreated, DocumentTaskSource>("documents.notify_release");
        services.AddScoped<DocumentReadConfirmations>();
        services.AddMessageHandler<Contracts.DocumentVersionPublished, DocumentReadConfirmations>("documents.read_confirmation");
        services.AddScoped<KnowledgeBase>();
        services.AddScoped<Contracts.IKnowledgeIndex>(sp => sp.GetRequiredService<KnowledgeBase>());
        services.AddScoped<IResourceDescriptorProvider, LibraryDescriptorProvider>();
        services.AddScoped<IResourceDescriptorProvider, FolderDescriptorProvider>();
        services.AddScoped<IResourceDescriptorProvider, DocumentDescriptorProviderForFiles>();
        return services;
    }

    public static IEndpointRouteBuilder MapDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/documents").RequireTenantUser().WithTags("Documents");

        root.MapGet("/settings", async (Libraries service, CancellationToken ct) => Results.Ok(await service.SettingsAsync(ct)));

        root.MapPut("/settings", async (DocumentSettingsRequest request, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.UpdateSettingsAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        var libraries = root.MapGroup("/libraries").WithTags("Documents: libraries");

        libraries.MapGet("", async (Libraries service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

        libraries.MapPost("", async (LibraryRequest request, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/documents/libraries/{view.Id}", view);
        });

        libraries.MapGet("/{id:guid}", async (Guid id, Libraries service, CancellationToken ct) => Results.Ok(await service.GetAsync(id, ct)));

        libraries.MapPut("/{id:guid}", async (Guid id, LibraryRequest request, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        libraries.MapPost("/{id:guid}/types", async (Guid id, DocumentTypeRequest request, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.CreateTypeAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/documents/types/{view.Id}", view);
        });

        libraries.MapGet("/{id:guid}/folders", async (Guid id, Libraries service, CancellationToken ct) => Results.Ok(await service.FoldersAsync(id, ct)));

        libraries.MapPost("/{id:guid}/folders", async (Guid id, FolderRequest request, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.CreateFolderAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/documents/folders/{view.Id}", view);
        });

        libraries.MapGet("/{id:guid}/views", async (Guid id, Libraries service, CancellationToken ct) => Results.Ok(await service.ViewsAsync(id, ct)));

        libraries.MapPost("/{id:guid}/views", async (Guid id, SavedViewRequest request, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.CreateViewAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/documents/views/{view.Id}", view);
        });

        // Filters come as f.{field}=value, with f.{field}.from and f.{field}.to for ranges of numbers and dates.
        libraries.MapGet("/{id:guid}/items", async (Guid id, HttpRequest http, Guid? folder, bool? recursive, Guid? type, string? q, string? sort, bool? desc, Guid? view,
            int? limit, int? offset, DocumentFiles files, CancellationToken ct) =>
        {
            var filters = http.Query.Where(p => p.Key.StartsWith("f.", StringComparison.Ordinal) && p.Key.Length > 2)
                .ToDictionary(p => p.Key[2..], p => p.Value.ToString(), StringComparer.Ordinal);
            var query = new ItemQuery(folder, recursive ?? false, type, q, filters.Count > 0 ? filters : null, sort, desc ?? false);
            return Results.Ok(await files.ItemsAsync(id, query, view, limit ?? 50, offset ?? 0, ct));
        });

        // The file is the request body; what it is comes in the query, metadata as a JSON object.
        libraries.MapPost("/{id:guid}/files", async (Guid id, HttpRequest http, string? name, Guid? folder, Guid? type, string? metadata, string? classification, bool? publish,
            DocumentFiles files, IDbSession session, CancellationToken ct) =>
        {
            LiftBodyLimit(http);
            var request = new UploadRequest(name, folder, type, ParseMetadata(metadata), classification, publish ?? false);
            var view = await files.UploadAsync(id, request, http.Body, http.ContentLength, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/documents/{view.Id}", view);
        }).DisableAntiforgery();

        root.MapPut("/types/{id:guid}", async (Guid id, DocumentTypeRequest request, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.UpdateTypeAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapDelete("/views/{id:guid}", async (Guid id, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            await service.DeleteViewAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapPost("/folders/{id:guid}/permissions/break", async (Guid id, BreakRequest? request, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.BreakFolderAsync(id, request ?? new BreakRequest(), ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/folders/{id:guid}/permissions/inherit", async (Guid id, BreakRequest? request, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.InheritFolderAsync(id, request ?? new BreakRequest(), ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/{id:guid}", async (Guid id, DocumentFiles files, CancellationToken ct) => Results.Ok(await files.GetAsync(id, ct)));

        root.MapPut("/{id:guid}/metadata", async (Guid id, MetadataRequest request, DocumentFiles files, IDbSession session, CancellationToken ct) =>
        {
            var view = await files.UpdateMetadataAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/permissions/break", async (Guid id, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var rights = await service.BreakDocumentAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(rights);
        });

        root.MapPost("/{id:guid}/permissions/inherit", async (Guid id, Libraries service, IDbSession session, CancellationToken ct) =>
        {
            var rights = await service.InheritDocumentAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(rights);
        });

        root.MapPost("/{id:guid}/checkout", async (Guid id, DocumentVersions versions, IDbSession session, CancellationToken ct) =>
        {
            var view = await versions.CheckOutAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // The working copy is the request body, named in the query.
        root.MapPut("/{id:guid}/working", async (Guid id, HttpRequest http, string? name, DocumentVersions versions, IDbSession session, CancellationToken ct) =>
        {
            LiftBodyLimit(http);
            var view = await versions.SaveWorkingCopyAsync(id, name, http.Body, http.ContentLength, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).DisableAntiforgery();

        // With a name the body is the new file; without one the saved working copy is checked in.
        root.MapPost("/{id:guid}/checkin", async (Guid id, HttpRequest http, string? name, string? note, bool? publish, DocumentVersions versions, IDbSession session,
            CancellationToken ct) =>
        {
            LiftBodyLimit(http);
            var request = new CheckInRequest(note, publish ?? false);
            var view = string.IsNullOrEmpty(name)
                ? await versions.CheckInAsync(id, request, null, null, null, ct)
                : await versions.CheckInAsync(id, request, name, http.Body, http.ContentLength, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).DisableAntiforgery();

        root.MapPost("/{id:guid}/discard", async (Guid id, DocumentVersions versions, IDbSession session, CancellationToken ct) =>
        {
            var view = await versions.DiscardAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/force-checkin", async (Guid id, ForceCheckInRequest request, DocumentVersions versions, IDbSession session, CancellationToken ct) =>
        {
            var view = await versions.ForceCheckInAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/{id:guid}/versions", async (Guid id, DocumentVersions versions, CancellationToken ct) => Results.Ok(await versions.VersionsAsync(id, ct)));

        root.MapGet("/{id:guid}/compare", async (Guid id, Guid from, Guid to, DocumentVersions versions, CancellationToken ct) =>
            Results.Ok(await versions.CompareAsync(id, from, to, ct)));

        root.MapPost("/{id:guid}/restore", async (Guid id, RestoreRequest request, DocumentVersions versions, IDbSession session, CancellationToken ct) =>
        {
            var view = await versions.RestoreAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapDelete("/{id:guid}", async (Guid id, DocumentVersions versions, IDbSession session, CancellationToken ct) =>
        {
            await versions.DeleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapPost("/{id:guid}/undelete", async (Guid id, DocumentVersions versions, IDbSession session, CancellationToken ct) =>
        {
            var view = await versions.UndeleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        libraries.MapGet("/{id:guid}/recycle-bin", async (Guid id, DocumentVersions versions, CancellationToken ct) => Results.Ok(await versions.RecycleBinAsync(id, ct)));

        root.MapGet("/{id:guid}/download-token", async (Guid id, Guid? version, DocumentFiles files, IDbSession session, CancellationToken ct) =>
        {
            var token = await files.DownloadTokenAsync(id, version, ct);
            await session.CommitAsync(ct);
            return Results.Ok(token);
        });

        root.MapGet("/{id:guid}/preview", async (Guid id, Guid? version, DocumentPreviews previews, IDbSession session, CancellationToken ct) =>
        {
            var view = await previews.PreviewAsync(id, version, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/previews/{token}", async (string token, DocumentPreviews previews, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type) = await previews.OpenAsync(token, ct);
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; media-src 'self'; sandbox";
            response.Headers.CacheControl = "private, no-store";
            response.Headers.ContentDisposition = "inline";
            return Results.File(content, type, enableRangeProcessing: true);
        });

        root.MapPost("/{id:guid}/release", async (Guid id, ReleaseRequest request, DocumentReleases releases, IDbSession session, CancellationToken ct) =>
        {
            var view = await releases.SubmitAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/release/withdraw", async (Guid id, DocumentReleases releases, IDbSession session, CancellationToken ct) =>
        {
            var view = await releases.WithdrawReleaseAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/review", async (Guid id, DocumentReviewRequest request, DocumentReleases releases, IDbSession session, CancellationToken ct) =>
        {
            var view = await releases.ReviewAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, DocumentReleases releases, CancellationToken ct) =>
            Results.Ok(await releases.VersionAsync(id, versionId, ct)));

        root.MapPost("/{id:guid}/read-confirmation", async (Guid id, DocumentReadConfirmations confirmations, IDbSession session, CancellationToken ct) =>
        {
            var view = await confirmations.ConfirmAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/{id:guid}/read-confirmations", async (Guid id, DocumentReadConfirmations confirmations, CancellationToken ct) =>
            Results.Ok(await confirmations.ReportAsync(id, ct)));

        root.MapGet("/{id:guid}/controlled-copy", async (Guid id, Guid? version, DocumentPreviews previews, IDbSession session, CancellationToken ct) =>
        {
            var token = await previews.ControlledCopyAsync(id, version, ct);
            await session.CommitAsync(ct);
            return Results.Ok(token);
        });

        root.MapGet("/controlled/{token}", async (string token, DocumentPreviews previews, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var (content, fileName) = await previews.OpenControlledAsync(token, ct);
            await session.CommitAsync(ct);
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            response.Headers.CacheControl = "private, no-store";
            return Results.File(content, "application/pdf", fileName);
        });

        libraries.MapGet("/{id:guid}/reviews", async (Guid id, DocumentReleases releases, CancellationToken ct) => Results.Ok(await releases.ReviewsAsync(id, ct)));

        root.MapGet("/files/{token}", async (string token, DocumentFiles files, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type, fileName) = await files.OpenAsync(token, ct);
            await session.CommitAsync(ct);
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            response.Headers.CacheControl = "private, no-store";
            return Results.File(content, type, fileName);
        });

        var kb = app.MapGroup("/api/v1/kb").RequireTenantUser().WithTags("Knowledge base");

        kb.MapGet("/templates", () => Results.Ok(KnowledgeBase.Templates()));

        kb.MapGet("/articles", async (int? limit, KnowledgeBase service, CancellationToken ct) => Results.Ok(await service.ListAsync(limit ?? 50, ct)));

        kb.MapPost("/articles", async (ArticleRequest request, KnowledgeBase service, IDbSession session, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/kb/articles/{created.Id}", created);
        });

        kb.MapGet("/articles/{id:guid}", async (Guid id, KnowledgeBase service, CancellationToken ct) => Results.Ok(await service.GetAsync(id, ct)));

        kb.MapPut("/articles/{id:guid}/related", async (Guid id, RelatedRequest request, KnowledgeBase service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.SetRelatedAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        kb.MapPost("/articles/{id:guid}/feedback", async (Guid id, FeedbackRequest request, KnowledgeBase service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.FeedbackAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        kb.MapGet("/articles/{id:guid}/feedback", async (Guid id, KnowledgeBase service, CancellationToken ct) => Results.Ok(await service.ReportAsync(id, ct)));

        kb.MapGet("/articles/{id:guid}/units", async (Guid id, KnowledgeBase service, CancellationToken ct) => Results.Ok(await service.ReaderUnitsAsync(id, ct)));

        return app;
    }

    /// <summary>The service enforces the library's limit while streaming; the server-wide cap is lifted to the largest allowed.</summary>
    private static void LiftBodyLimit(HttpRequest http)
    {
        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = FileTypes.DefaultMaxFileMb * 1024L * 1024L + 1;
        }
    }

    private static IReadOnlyDictionary<string, JsonElement>? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            throw AppException.Validation("metadata", "validation.documents.metadata_json");
        }
    }
}
