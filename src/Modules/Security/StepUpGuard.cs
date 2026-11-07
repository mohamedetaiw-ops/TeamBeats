using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

/// <summary>
/// Step-up for sensitive actions (spec 026 US2 scenario 2, FR-005): an endpoint marked <c>RequireStepUp()</c> runs only
/// when the caller authenticated within the tenant's step-up window. Otherwise the answer is 401
/// <c>security.step_up_required</c> with the sign-in address that asks the identity provider for a fresh sign-in.
/// Sessions from the development sign-in (dev host only, ADR-0004) count as fresh.
/// </summary>
public static class StepUpGuard
{
    public const string StepUpPath = "/api/v1/auth/signin/sso?stepUp=true";

    public static IApplicationBuilder UseStepUp(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var context = http.RequestServices.GetRequiredService<ITenantContext>();
            if (context.ActorType != ActorType.User || http.GetEndpoint()?.Metadata.GetMetadata<StepUpMetadata>() is null
                || http.Features.Get<SessionAssurance>() is not { } assurance || assurance.Method == "dev")
            {
                await next(http);
                return;
            }
            var policy = await http.RequestServices.GetRequiredService<ISecurityPolicy>().GetAsync(http.RequestAborted);
            var now = http.RequestServices.GetRequiredService<IClock>().UtcNow;
            if (Fresh(assurance.AuthenticatedAt, policy.StepUpMinutes, now))
            {
                await next(http);
                return;
            }
            throw new AppException(ProblemCodes.StepUpRequired, 401, null, null,
                new Dictionary<string, object?> { ["stepUpUrl"] = StepUpPath, ["stepUpMinutes"] = policy.StepUpMinutes });
        });

    public static bool Fresh(DateTimeOffset authenticatedAt, int stepUpMinutes, DateTimeOffset now) =>
        now - authenticatedAt <= TimeSpan.FromMinutes(stepUpMinutes);
}
