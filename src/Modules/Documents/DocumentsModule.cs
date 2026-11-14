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
        services.AddScoped<TeamBeats.Modules.PublicApi.Contracts.IPublicResourceVisibility, DocumentVisibility>();
        services.AddScoped<TeamBeats.Modules.Search.Contracts.ISearchSource, DocumentSearchSource>();
        services.AddScoped<Libraries>();
        services.AddScoped<Contracts.ILegalHolds, LegalHolds>();
        services.AddScoped<DocumentFiles>();
        services.AddScoped<TeamBeats.Modules.Agents.Contracts.IAgentTool, GetDocumentTool>();
        services.AddScoped<DocumentAccess>();
        services.AddScoped<DocumentVersions>();
        services.AddScoped<OfficeEditing>();
        services.AddScoped<Records>();
        services.AddScoped<GuestShares>();
        services.AddScoped<Assets>();
        services.AddScoped<DocumentImports>();
        services.AddMessageHandler<DocumentImportRequested, DocumentImportRunner>("documents.import");
        services.AddScoped<Contracts.IAssetImages>(sp => sp.GetRequiredService<Assets>());
        services.AddScoped<Workflow.Contracts.IPersonalTaskSource, AssetAccessTasks>();
        services.AddScoped<IResourceDescriptorProvider, AssetCollectionDescriptorProvider>();
        services.AddScoped<DocumentPreviews>();
        services.AddScoped<DocumentReleases>();
        services.AddScoped<DocumentTaskSource>();
        services.AddScoped<Workflow.Contracts.ITaskSource>(sp => sp.GetRequiredService<DocumentTaskSource>());
        services.AddMessageHandler<Workflow.Contracts.UserTaskCreated, DocumentTaskSource>("documents.notify_release");
        services.AddScoped<DocumentReadConfirmations>();
        services.AddMessageHandler<Contracts.DocumentVersionPublished, DocumentReadConfirmations>("documents.read_confirmation");
        services.AddSingleton<Experience.Contracts.IComponentRegistration, DocumentsComponents>();
        services.AddScoped<KnowledgeBase>();
        services.AddScoped<Contracts.IKnowledgeIndex>(sp => sp.GetRequiredService<KnowledgeBase>());
        services.AddScoped<Contracts.IDocumentReferences, DocumentReferences>();
        services.AddScoped<Contracts.ISpaceLibraries, SpaceLibraries>();
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

        // Bulk import (FR-022): the ZIP is the request body; the manifest inside it says what each file becomes.
        libraries.MapPost("/{id:guid}/imports", async (Guid id, HttpRequest http, string? name, Guid? folder, DocumentImports imports, IDbSession session,
            CancellationToken ct) =>
        {
            if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = DocumentImports.MaxZipBytes + 1;
            }
            var view = await imports.StartAsync(id, name, folder, http.Body, http.ContentLength, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/documents/imports/{view.Id}", view);
        }).DisableAntiforgery();

        libraries.MapGet("/{id:guid}/imports", async (Guid id, DocumentImports imports, CancellationToken ct) => Results.Ok(await imports.ListAsync(id, ct)));

        // The file is the request body; what it is comes in the query, metadata as a JSON object.
        // sensitiveReason confirms a sensitive data warning (spec 026 US6).
        libraries.MapPost("/{id:guid}/files", async (Guid id, HttpRequest http, string? name, Guid? folder, Guid? type, string? metadata, string? classification, bool? publish,
            string? sensitiveReason, DocumentFiles files, IDbSession session, CancellationToken ct) =>
        {
            LiftBodyLimit(http);
            var request = new UploadRequest(name, folder, type, ParseMetadata(metadata), classification, publish ?? false, sensitiveReason);
            var view = await files.UploadAsync(id, request, http.Body, http.ContentLength, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/documents/{view.Id}", view);
        }).DisableAntiforgery();

        root.MapGet("/imports/{id:guid}", async (Guid id, DocumentImports imports, CancellationToken ct) => Results.Ok(await imports.GetAsync(id, ct)));
        root.MapGet("/imports/{id:guid}/rows", async (Guid id, string? status, int? limit, int? offset, DocumentImports imports, CancellationToken ct) =>
            Results.Ok(await imports.RowsAsync(id, status, limit ?? 100, offset ?? 0, ct)));
        root.MapGet("/imports/{id:guid}/report", async (Guid id, DocumentImports imports, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.CacheControl = "private, no-store";
            return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(await imports.ReportCsvAsync(id, ct))).ToArray(),
                "text/csv; charset=utf-8", $"import-{id:N}.csv");
        });

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

        root.MapGet("/{id:guid}", async (Guid id, DocumentFiles files, IDbSession session, CancellationToken ct) =>
        {
            // A guest's view is audited (spec 011 US8), so the read commits.
            var view = await files.GetAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

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
        root.MapPut("/{id:guid}/working", async (Guid id, HttpRequest http, string? name, string? sensitiveReason, DocumentVersions versions, IDbSession session,
            CancellationToken ct) =>
        {
            LiftBodyLimit(http);
            var view = await versions.SaveWorkingCopyAsync(id, name, http.Body, http.ContentLength, ct, sensitiveReason);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).DisableAntiforgery();

        // With a name the body is the new file; without one the saved working copy is checked in.
        root.MapPost("/{id:guid}/checkin", async (Guid id, HttpRequest http, string? name, string? note, bool? publish, string? sensitiveReason, DocumentVersions versions,
            IDbSession session, CancellationToken ct) =>
        {
            LiftBodyLimit(http);
            var request = new CheckInRequest(note, publish ?? false, sensitiveReason);
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

        // Office editing in the browser (FR-010): whether it is offered, start, check in the edited copy, or discard it.
        root.MapGet("/{id:guid}/office", async (Guid id, OfficeEditing office, CancellationToken ct) => Results.Ok(await office.GetAsync(id, ct)));

        root.MapPost("/{id:guid}/office/start", async (Guid id, OfficeEditing office, IDbSession session, CancellationToken ct) =>
        {
            var view = await office.StartAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/office/checkin", async (Guid id, OfficeCheckInRequest request, OfficeEditing office, IDbSession session, CancellationToken ct) =>
        {
            var (view, removeCopy) = await office.CheckInAsync(id, new CheckInRequest(request.Note, request.Publish, request.SensitiveReason), ct);
            await session.CommitAsync(ct);
            await removeCopy(CancellationToken.None);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}/office/discard", async (Guid id, OfficeEditing office, IDbSession session, CancellationToken ct) =>
        {
            var (view, removeCopy) = await office.DiscardAsync(id, ct);
            await session.CommitAsync(ct);
            await removeCopy(CancellationToken.None);
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

        // Folders shared read-only with guest spaces (spec 011 US8, FR-017).
        libraries.MapGet("/{id:guid}/guest-shares", async (Guid id, GuestShares shares, CancellationToken ct) => Results.Ok(await shares.ListAsync(id, ct)));

        libraries.MapPost("/{id:guid}/guest-shares", async (Guid id, GuestShareRequest request, GuestShares shares, IDbSession session, CancellationToken ct) =>
        {
            var view = await shares.CreateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/documents/guest-shares/{view.Id}", view);
        });

        root.MapPost("/guest-shares/{id:guid}/revoke", async (Guid id, GuestShares shares, IDbSession session, CancellationToken ct) =>
        {
            var view = await shares.RevokeAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/shared-with-space/{spaceId:guid}", async (Guid spaceId, GuestShares shares, CancellationToken ct) =>
            Results.Ok(await shares.ForSpaceAsync(spaceId, ct)));

        // The brand asset portal (spec 011 US5, FR-012).
        var assets = app.MapGroup("/api/v1/assets").RequireTenantUser().WithTags("Assets");

        assets.MapGet("/portal", async (Assets service, CancellationToken ct) => Results.Ok(await service.PortalAsync(ct)));
        assets.MapGet("/collections", async (Assets service, CancellationToken ct) => Results.Ok(await service.CollectionsAsync(ct)));

        assets.MapPost("/collections", async (AssetCollectionRequest request, Assets service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.CreateCollectionAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/assets/collections/{view.Id}", view);
        });

        assets.MapPut("/collections/{id:guid}", async (Guid id, AssetCollectionRequest request, Assets service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.UpdateCollectionAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        assets.MapGet("/collections/{id:guid}/assets", async (Guid id, Assets service, CancellationToken ct) => Results.Ok(await service.AssetsAsync(id, ct)));

        assets.MapPost("/collections/{id:guid}/assets", async (Guid id, HttpRequest http, string? name, string? title, string? notes, DateTimeOffset? rightsExpireAt,
            Assets service, IDbSession session, CancellationToken ct) =>
        {
            // The file is the body; its name, title and usage notes (in the caller's language) come in the query.
            var language = http.Headers.AcceptLanguage.ToString().StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
            var request = new AssetRequest(string.IsNullOrWhiteSpace(title) ? null : new Dictionary<string, string> { [language] = title },
                string.IsNullOrWhiteSpace(notes) ? null : new Dictionary<string, string> { [language] = notes }, rightsExpireAt);
            var view = await service.UploadAsync(id, name, request, http.Body, http.ContentLength, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/assets/{view.Id}", view);
        });

        assets.MapGet("/{id:guid}", async (Guid id, Assets service, CancellationToken ct) => Results.Ok(await service.GetAsync(id, ct)));

        assets.MapPut("/{id:guid}", async (Guid id, AssetRequest request, Assets service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        assets.MapPost("/{id:guid}/renditions", async (Guid id, Assets service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.RenderAgainAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        assets.MapGet("/{id:guid}/thumbnail", async (Guid id, Assets service, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type) = await service.ThumbnailAsync(id, ct);
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            response.Headers.CacheControl = "private, max-age=300";
            return Results.File(content, type);
        });

        assets.MapGet("/{id:guid}/image", async (Guid id, Assets service, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type) = await service.ImageAsync(id, ct);
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            response.Headers.CacheControl = "private, max-age=300";
            return Results.File(content, type);
        });

        assets.MapGet("/{id:guid}/renditions/{key}", async (string key, Guid id, Assets service, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type, fileName) = await service.DownloadAsync(id, key, ct);
            await session.CommitAsync(ct);
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            response.Headers.CacheControl = "private, no-store";
            return Results.File(content, type, fileName);
        });

        assets.MapGet("/picker", async (string? q, Assets service, CancellationToken ct) => Results.Ok(await service.PickerAsync(q, ct)));

        assets.MapPost("/collections/{id:guid}/access-requests", async (Guid id, AccessRequestBody body, Assets service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.RequestAccessAsync(id, body, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        assets.MapGet("/access-requests", async (Assets service, CancellationToken ct) => Results.Ok(await service.RequestsAsync(ct)));

        assets.MapPost("/access-requests/{id:guid}", async (Guid id, AccessDecision decision, Assets service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.DecideAsync(id, decision, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        assets.MapGet("/downloads", async (DateTimeOffset? from, DateTimeOffset? to, Assets service, CancellationToken ct) =>
            Results.Ok(await service.DownloadsAsync(from, to, ct)));

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

        // Spec 011 R2 US6 and US7: records management, for records managers only.
        var records = app.MapGroup("/api/v1/records").RequireTenantUser().WithTags("Records");

        records.MapGet("/labels", async (Records service, CancellationToken ct) => Results.Ok(await service.LabelsAsync(ct)));

        records.MapPost("/labels", async (LabelRequest request, Records service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.CreateLabelAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/records/labels/{view.Id}", view);
        });

        records.MapPut("/labels/{id:guid}", async (Guid id, LabelRequest request, Records service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.UpdateLabelAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        records.MapGet("/libraries", async (Records service, CancellationToken ct) => Results.Ok(await service.LibrariesAsync(ct)));

        records.MapPut("/libraries/{id:guid}/label", async (Guid id, LabelChoice choice, Records service, IDbSession session, CancellationToken ct) =>
        {
            await service.SetLibraryLabelAsync(id, choice, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        records.MapPut("/types/{id:guid}/label", async (Guid id, LabelChoice choice, Records service, IDbSession session, CancellationToken ct) =>
        {
            await service.SetTypeLabelAsync(id, choice, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        records.MapGet("/documents/{id:guid}", async (Guid id, Records service, CancellationToken ct) => Results.Ok(await service.DocumentAsync(id, ct)));

        records.MapPut("/documents/{id:guid}/label", async (Guid id, LabelChoice choice, Records service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.SetDocumentLabelAsync(id, choice, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        records.MapGet("/disposition", async (Records service, CancellationToken ct) => Results.Ok(await service.QueueAsync(ct)));

        records.MapPost("/disposition/{id:guid}", async (Guid id, DispositionDecision decision, Records service, IDbSession session, CancellationToken ct) =>
        {
            await service.DecideAsync(id, decision, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        records.MapGet("/dispositions", async (Records service, CancellationToken ct) => Results.Ok(await service.DispositionRecordsAsync(ct)));

        records.MapGet("/holds", async (Records service, CancellationToken ct) => Results.Ok(await service.HoldsAsync(ct)));

        records.MapPost("/holds", async (HoldRequest request, Records service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.CreateHoldAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/records/holds/{view.Hold.Id}", view);
        });

        records.MapGet("/holds/{id:guid}", async (Guid id, Records service, CancellationToken ct) => Results.Ok(await service.HoldAsync(id, ct)));

        records.MapPost("/holds/{id:guid}/release", async (Guid id, Records service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.ReleaseHoldAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        app.MapDocumentPublicEndpoints();
        return app;
    }

    /// <summary>The service enforces the library's limit while streaming; the server-wide cap is lifted to the largest allowed.</summary>
    internal static void LiftBodyLimit(HttpRequest http)
    {
        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = FileTypes.DefaultMaxFileMb * 1024L * 1024L + 1;
        }
    }

    internal static IReadOnlyDictionary<string, JsonElement>? ParseMetadata(string? json)
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
