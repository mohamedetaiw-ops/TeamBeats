using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi.Contracts;

/// <summary>The kinds of caller a public token stands for.</summary>
public static class PublicCallerKinds
{
    /// <summary>An app acting for a signed-in employee, with the person's own access.</summary>
    public const string Delegated = "delegated";

    /// <summary>An app acting as itself, with only the roles given to the app (FR-011).</summary>
    public const string Application = "application";
}

/// <summary>The app and principal behind a public API call, with the scopes its token holds.</summary>
public sealed record PublicCaller(Guid AppId, string Kind, Guid? UserId, IReadOnlySet<string> Scopes, Guid FamilyId)
{
    public bool IsDelegated => Kind == PublicCallerKinds.Delegated;

    /// <summary>
    /// The effective decision of a public operation (FR-009): the token must hold the operation's scope and the central
    /// authorization service must allow it for the principal. A scope never allows what the decision does not.
    /// </summary>
    public bool May(string scope, TeamBeats.Authorization.Contracts.Decision decision) => Scopes.Contains(scope) && decision.Allowed;

    /// <summary>Scopes only narrow (FR-009): the operation still asks the authorization service afterwards.</summary>
    public void Demand(string scope)
    {
        if (!Scopes.Contains(scope))
        {
            throw new AppException(ProblemCodes.ApiInsufficientScope, 403, null, null, new Dictionary<string, object?> { ["scope"] = scope });
        }
    }
}

/// <summary>The scope a public operation declares (FR-008); every <c>/api/public</c> endpoint has exactly one.</summary>
public sealed record PublicScopeMetadata(string Scope);

/// <summary>Marks a create or action operation that honours <c>Idempotency-Key</c> for 24 hours (FR-015).</summary>
public sealed record PublicIdempotentMetadata;

/// <summary>
/// The <c>PublicEndpoint</c> convention (spec 035 plan, T010). Owning modules map their public operations in their own
/// projects under <see cref="MapPublicApi"/> and declare the scope with <see cref="RequireScope{TBuilder}"/>; the scope
/// check and the scope's capability keys run before the handler, and the handler still asks the central authorization
/// service, so business rules stay in the module.
/// </summary>
public static class PublicEndpoints
{
    public const string Prefix = "/api/public";
    public const string Version1 = Prefix + "/v1";

    /// <summary>The OpenAPI document of the public API, separate from the internal web API (FR-014).</summary>
    public const string DocumentName = "public-v1";

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>A group of public v1 operations of one module, in the public OpenAPI document only.</summary>
    public static RouteGroupBuilder MapPublicApi(this IEndpointRouteBuilder app, string tag) =>
        app.MapGroup(Version1).WithTags(tag).WithGroupName(DocumentName);

    /// <summary>
    /// Declares the operation's scope: the token must hold it, the caller's kind must be allowed to hold it, and the
    /// package must have the scope's capability keys (section 9). A scope never allows anything on its own.
    /// </summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder
    {
        var definition = ScopeCatalogue.Find(scope) ?? throw new ArgumentException($"Scope {scope} is not in the catalogue.", nameof(scope));
        builder.WithMetadata(new PublicScopeMetadata(scope));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var caller = http.Caller();
            caller.Demand(scope);
            if (caller.IsDelegated ? !definition.AllowsDelegated : !definition.AllowsApplication)
            {
                throw AppException.Denied(caller.IsDelegated ? "authz.denied.api_application_only" : "authz.denied.api_delegated_only");
            }
            var entitlements = http.RequestServices.GetRequiredService<IEntitlementService>();
            foreach (var capability in definition.Capabilities)
            {
                await entitlements.DemandAsync(capability, http.RequestAborted);
            }
            return await next(context);
        });
        return builder;
    }

    /// <summary>Honours <c>Idempotency-Key</c> on this create or action operation (FR-015): the first answer is replayed for 24 hours.</summary>
    public static TBuilder Idempotent<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new PublicIdempotentMetadata());

    /// <summary>The caller of a public operation, set by the token check before any endpoint runs.</summary>
    public static PublicCaller Caller(this HttpContext http) => http.Features.Get<PublicCaller>() ?? throw AppException.Unauthenticated();

    /// <summary>The page size asked for: 50 by default, at most 200 (FR-015).</summary>
    public static int PageSize(int? limit)
    {
        if (limit is { } value && (value < 1 || value > MaxPageSize))
        {
            throw AppException.Validation("limit", "validation.api.page_size");
        }
        return limit ?? DefaultPageSize;
    }

    /// <summary>An opaque cursor: the position after the last item of a page, as base64url JSON.</summary>
    public static string EncodeCursor<T>(T position) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(position)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads a cursor this API wrote; anything else is a validation error, never a silent first page.</summary>
    public static T? DecodeCursor<T>(string? cursor) where T : class
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }
        try
        {
            var text = cursor.Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(text)) ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException)
        {
            throw AppException.Validation("cursor", "validation.api.cursor");
        }
    }

    /// <summary>
    /// A readable resource with a weak ETag over its JSON (FR-015): a matching <c>If-None-Match</c> answers 304 with no
    /// body. The tag is a hash of what the caller may see, so two callers who see different fields get different tags.
    /// </summary>
    public static IResult Resource<T>(HttpContext http, T value)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var json = JsonSerializer.SerializeToUtf8Bytes(value, options);
        var tag = "W/\"" + Convert.ToBase64String(SHA256.HashData(json), 0, 18).Replace('+', '-').Replace('/', '_') + "\"";
        http.Response.Headers.ETag = tag;
        http.Response.Headers.CacheControl = "private, no-cache";
        var match = http.Request.Headers.IfNoneMatch.ToString();
        if (match.Length > 0 && match.Split(',').Any(m => m.Trim() == tag || m.Trim() == "*"))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Bytes(json, "application/json");
    }
}

/// <summary>One page of a public list (FR-015): <c>nextCursor</c> is null on the last page.</summary>
public sealed record PublicPage<T>(IReadOnlyList<T> Items, string? NextCursor);
