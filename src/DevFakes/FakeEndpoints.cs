using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace TeamBeats.DevFakes;

/// <summary>The fakes one development or staging host uses, shared by the HTTP handlers and the browser pages.</summary>
public sealed class FakeSystems(FakeScenario scenario, FakeIdentityProvider entra, FakeMicrosoftGraph graph, FakeOpenRouter openRouter)
{
    public FakeScenario Scenario { get; } = scenario;
    public FakeIdentityProvider Entra { get; } = entra;
    public FakeMicrosoftGraph Graph { get; } = graph;
    public FakeOpenRouter OpenRouter { get; } = openRouter;

    /// <summary>Group memberships per user, for the groups claim of the fake ID token.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GroupsByUser(string directoryId) =>
        Scenario.FindDirectory(directoryId)?.Users.ToDictionary(u => u.Id, u => (IReadOnlyList<string>)u.Groups, StringComparer.Ordinal)
        ?? new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>
/// Browser pages of the fake Microsoft sign-in and admin consent, the redirect rewrite that sends the browser to them,
/// and a small control API for testers. Mapped under <c>/dev/fake</c> by the development host only.
/// </summary>
public static class FakeEndpoints
{
    public const string Prefix = "/dev/fake";
    public const string EntraPrefix = Prefix + "/entra/";
    public const string MicrosoftLogin = "https://login.microsoftonline.com/";

    /// <summary>
    /// Rewrites redirects to Microsoft's sign-in and consent pages to the fake pages on the same host, so the real
    /// adapters run unchanged and the browser never leaves the test environment.
    /// </summary>
    public static IApplicationBuilder UseFakeMicrosoftRedirects(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var location = context.Response.Headers.Location.ToString();
                if (location.StartsWith(MicrosoftLogin, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.Location = EntraPrefix + location[MicrosoftLogin.Length..];
                }
                return Task.CompletedTask;
            });
            await next();
        });

    /// <param name="baseDomain">The installation's base domain: the fake pages may send the browser back to any host under it.</param>
    public static IEndpointRouteBuilder MapFakeEndpoints(this IEndpointRouteBuilder app, string baseDomain)
    {
        var fake = app.MapGroup(Prefix).WithTags("Development only: fake external systems").ExcludeFromDescription();

        fake.MapGet("/", (FakeSystems f) => Results.Ok(new
        {
            scenario = f.Scenario.Name,
            systems = new[]
            {
                new { name = "entra", down = f.Entra.Unavailable },
                new { name = "graph", down = f.Graph.Unavailable },
                new { name = "openrouter", down = f.OpenRouter.Unavailable },
            },
            directories = f.Scenario.Directories.Select(d => new
            {
                d.Id, d.DisplayName, consented = f.Graph.HasConsent(Guid.Parse(d.Id)),
                users = f.Graph.Data(Guid.Parse(d.Id)).CurrentUsers().Count, groups = d.Groups.Count, adminUnits = d.AdminUnits.Count, rooms = d.Rooms.Count,
            }),
        }));

        // Microsoft's authorize endpoint: a user picker for single sign-on and for delegated mail and calendar.
        fake.MapGet("/entra/{directory}/oauth2/v2.0/authorize", (string directory, HttpRequest request, FakeSystems f) =>
        {
            if (!Guid.TryParse(directory, out var directoryId))
            {
                return Page(request, "Unknown directory", "دليل غير معروف", Say(request, "The directory ID is not a GUID.", "معرف الدليل غير صالح."), HttpStatusCode.BadRequest);
            }
            var q = request.Query;
            if (!SafeRedirect(q["redirect_uri"], request, baseDomain))
            {
                return Page(request, "Redirect refused", "تم رفض إعادة التوجيه", Say(request, "The redirect URI is not on this test environment.", "عنوان إعادة التوجيه ليس ضمن بيئة الاختبار هذه."), HttpStatusCode.BadRequest);
            }
            var users = FakeScenario.SignInUsers(f.Graph, directoryId, f.GroupsByUser(directory));
            var ar = Arabic(request);
            var hidden = new StringBuilder();
            foreach (var key in new[] { "client_id", "redirect_uri", "state", "nonce", "code_challenge", "scope" })
            {
                hidden.Append($"<input type=\"hidden\" name=\"{key}\" value=\"{Enc(q[key])}\">");
            }
            var list = new StringBuilder("<ul>");
            foreach (var u in users)
            {
                list.Append("<li><form method=\"post\">").Append(hidden).Append($"<input type=\"hidden\" name=\"oid\" value=\"{Enc(u.ObjectId)}\">")
                    .Append($"<button type=\"submit\"><bdi>{Enc(u.Name)}</bdi> <small dir=\"ltr\">{Enc(u.Email)}</small></button></form></li>");
            }
            list.Append("</ul>");
            var cancel = Append(q["redirect_uri"]!, $"error=access_denied&error_description=user_cancelled&state={Uri.EscapeDataString(q["state"].ToString())}");
            var body = $"<p>{(ar ? "اختر حسابا تجريبيا لتسجيل الدخول. هذه صفحة وهمية لبيئة الاختبار ولا تتصل بمايكروسوفت." : "Choose a test account to sign in. This is a fake page for the test environment and does not contact Microsoft.")}</p>"
                + $"<p><strong>{Enc(f.Scenario.FindDirectory(directory)?.DisplayName ?? directory)}</strong></p>"
                + (users.Count == 0 ? $"<p>{(ar ? "لا يوجد مستخدمون في هذا الدليل." : "This directory has no users.")}</p>" : list.ToString())
                + $"<p><a href=\"{Enc(cancel)}\">{(ar ? "إلغاء" : "Cancel")}</a></p>";
            return Page(request, "Sign in to a fake Microsoft account", "تسجيل الدخول إلى حساب مايكروسوفت وهمي", body);
        });

        fake.MapPost("/entra/{directory}/oauth2/v2.0/authorize", async (string directory, HttpRequest request, FakeSystems f) =>
        {
            var form = await request.ReadFormAsync();
            var redirect = form["redirect_uri"].ToString();
            if (!Guid.TryParse(directory, out var directoryId) || !SafeRedirect(redirect, request, baseDomain))
            {
                return Results.BadRequest();
            }
            var user = FakeScenario.SignInUsers(f.Graph, directoryId, f.GroupsByUser(directory)).FirstOrDefault(u => u.ObjectId == form["oid"]);
            if (user is null)
            {
                return Results.Redirect(Append(redirect, $"error=access_denied&error_description=account_not_found&state={Uri.EscapeDataString(form["state"].ToString())}"));
            }
            var clientId = form["client_id"].ToString();
            string code;
            if (clientId == FakeMicrosoftGraph.ClientId)
            {
                code = f.Graph.IssueDelegatedCode(directoryId, user.ObjectId, clientId, redirect, form["code_challenge"].ToString(), form["scope"].ToString());
            }
            else
            {
                var app = f.Scenario.FindDirectory(directory)?.SsoApp;
                if (app is null || !string.Equals(app.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Redirect(Append(redirect, $"error=unauthorized_client&error_description=unknown_application&state={Uri.EscapeDataString(form["state"].ToString())}"));
                }
                var authorizeUrl = $"{MicrosoftLogin}{directoryId:D}/oauth2/v2.0/authorize?"
                    + string.Join("&", new[] { "client_id", "redirect_uri", "code_challenge", "nonce" }.Select(k => $"{k}={Uri.EscapeDataString(form[k].ToString())}"));
                code = f.Entra.Authenticate(authorizeUrl, user, app.ClientSecret);
            }
            return Results.Redirect(Append(redirect, $"code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(form["state"].ToString())}"));
        }).DisableAntiforgery();

        // Microsoft's admin consent endpoint for the TeamBeats application.
        fake.MapGet("/entra/{directory}/v2.0/adminconsent", (string directory, HttpRequest request, FakeSystems f) =>
        {
            var q = request.Query;
            if (!Guid.TryParse(directory, out _) || !SafeRedirect(q["redirect_uri"], request, baseDomain))
            {
                return Page(request, "Consent refused", "تم رفض الموافقة", Say(request, "The directory or redirect URI is not valid here.", "الدليل أو عنوان إعادة التوجيه غير صالح هنا."), HttpStatusCode.BadRequest);
            }
            var ar = Arabic(request);
            var hidden = string.Concat(new[] { "client_id", "redirect_uri", "state", "scope" }.Select(k => $"<input type=\"hidden\" name=\"{k}\" value=\"{Enc(q[k])}\">"));
            var scopes = string.Concat(q["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => $"<li dir=\"ltr\">{Enc(s)}</li>"));
            var body = $"<p>{(ar ? "يطلب تطبيق TeamBeats هذه الأذونات على الدليل الوهمي:" : "The TeamBeats application asks for these permissions on the fake directory:")}</p>"
                + $"<p><strong>{Enc(f.Scenario.FindDirectory(directory)?.DisplayName ?? directory)}</strong></p><ul>{scopes}</ul>"
                + $"<form method=\"post\">{hidden}<button type=\"submit\" name=\"decision\" value=\"accept\">{(ar ? "موافقة" : "Accept")}</button> "
                + $"<button type=\"submit\" name=\"decision\" value=\"cancel\">{(ar ? "إلغاء" : "Cancel")}</button></form>";
            return Page(request, "Fake admin consent", "موافقة المسؤول الوهمية", body);
        });

        fake.MapPost("/entra/{directory}/v2.0/adminconsent", async (string directory, HttpRequest request, FakeSystems f) =>
        {
            var form = await request.ReadFormAsync();
            var redirect = form["redirect_uri"].ToString();
            if (!Guid.TryParse(directory, out var directoryId) || !SafeRedirect(redirect, request, baseDomain))
            {
                return Results.BadRequest();
            }
            var state = Uri.EscapeDataString(form["state"].ToString());
            if (form["decision"] != "accept")
            {
                return Results.Redirect(Append(redirect, $"error=access_denied&error_description=admin_declined&state={state}"));
            }
            var d = f.Scenario.FindDirectory(directory);
            f.Graph.Consent(directoryId, d?.GrantedRoles ?? ["User.Read.All", "Group.Read.All", "AdministrativeUnit.Read.All"],
                f.Graph.Data(directoryId).CurrentUsers().Count, d?.Groups.Count ?? 0);
            return Results.Redirect(Append(redirect, $"admin_consent=True&tenant={directoryId:D}&state={state}"));
        }).DisableAntiforgery();

        // Control API for testers: change the fake directory, revoke consent, simulate outages.
        fake.MapPost("/graph/{directory:guid}/users", (Guid directory, ScenarioUser user, FakeSystems f) =>
        {
            if (string.IsNullOrWhiteSpace(user.Id) || string.IsNullOrWhiteSpace(user.Mail) || !user.Mail.EndsWith(".example", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "id and a mail on a .example domain are required" });
            }
            var data = f.Graph.Data(directory);
            data.AddUser(user.Id, user.DisplayName, user.Mail, user.Enabled, user.JobTitle);
            data.SetOrg(user.Id, user.CompanyName, user.Department, user.Division);
            foreach (var g in user.Groups)
            {
                data.AddMember(g, user.Id);
            }
            return Results.Ok(new { user.Id });
        });
        fake.MapPost("/graph/{directory:guid}/users/{id}:disable", (Guid directory, string id, FakeSystems f) => Change(() => f.Graph.Data(directory).UpdateUser(id, enabled: false)));
        fake.MapPost("/graph/{directory:guid}/users/{id}:enable", (Guid directory, string id, FakeSystems f) => Change(() => f.Graph.Data(directory).UpdateUser(id, enabled: true)));
        fake.MapPost("/graph/{directory:guid}/users/{id}:delete", (Guid directory, string id, FakeSystems f) => Change(() => f.Graph.Data(directory).DeleteUser(id)));
        fake.MapPost("/graph/{directory:guid}/consent:revoke", (Guid directory, FakeSystems f) => Change(() => f.Graph.Revoke(directory)));
        fake.MapPost("/outage/{system}", (string system, bool? down, FakeSystems f) =>
        {
            var value = down ?? true;
            switch (system)
            {
                case "entra": f.Entra.Unavailable = value; break;
                case "graph": f.Graph.Unavailable = value; break;
                case "openrouter": f.OpenRouter.Unavailable = value; break;
                default: return Results.NotFound();
            }
            return Results.Ok(new { system, down = value });
        });
        return app;
    }

    private static IResult Change(Action change)
    {
        try
        {
            change();
            return Results.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    /// <summary>
    /// The fake pages only send the browser back to this host or another tenant host of this installation, so they
    /// cannot be used as an open redirect.
    /// </summary>
    public static bool SafeRedirect(string? redirectUri, HttpRequest request, string baseDomain)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return false;
        }
        var host = uri.Host;
        return string.Equals(host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            || (baseDomain.Length > 0 && host.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase));
    }

    private static string Append(string url, string query) => url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query;

    private static bool Arabic(HttpRequest request) =>
        request.Headers.AcceptLanguage.ToString().TrimStart().StartsWith("ar", StringComparison.OrdinalIgnoreCase);

    private static string Say(HttpRequest request, string en, string ar) => $"<p>{Enc(Arabic(request) ? ar : en)}</p>";

    private static string Enc(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? string.Empty);

    /// <summary>A plain page in the visitor's language and direction. Marked everywhere as a test-only fake.</summary>
    private static IResult Page(HttpRequest request, string titleEn, string titleAr, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var ar = Arabic(request);
        var title = ar ? titleAr : titleEn;
        var html = $$"""
            <!doctype html>
            <html lang="{{(ar ? "ar" : "en")}}" dir="{{(ar ? "rtl" : "ltr")}}">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{Enc(title)}}</title>
            <style>
              body { font-family: system-ui, sans-serif; max-width: 36rem; margin: 2rem auto; padding: 0 1rem; color: #1b1b1f; background: #fff; }
              .banner { background: #fff3cd; border: 1px solid #c9a227; padding: .5rem .75rem; border-radius: .25rem; }
              ul { list-style: none; padding: 0; } li { margin: .4rem 0; }
              button { inline-size: 100%; text-align: start; padding: .6rem .75rem; font: inherit; cursor: pointer; }
              small { color: #555; margin-inline-start: .5rem; }
            </style></head>
            <body><main>
            <p class="banner" role="note">{{(ar ? "بيئة اختبار: بيانات وهمية فقط" : "Test environment: fictional data only")}}</p>
            <h1>{{Enc(title)}}</h1>
            {{body}}
            </main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, (int)status);
    }
}
