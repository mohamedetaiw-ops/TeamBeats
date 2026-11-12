using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Learning;

/// <summary>One launch, as the runtime finds it from its token.</summary>
internal sealed record PackageLaunch(Guid Id, Guid AssignmentId, string LessonKey, Guid PackageId, int CourseVersion, string Kind, string? ActivityId, string State,
    string? Status, DateTimeOffset? ReportedAt, Guid LearnerId);

/// <summary>
/// The package runtime (spec 028 US7, FR-016). A launch token in the content address is the only key: package files
/// are read through it from tenant storage, the tenant comes from the host, and no session is used, so the sandboxed
/// frame (an opaque origin, without <c>allow-same-origin</c>) never sees the TeamBeats session. SCORM 1.2 packages get
/// a small in-house bridge script that provides the SCORM API and posts values back through the same token; xAPI
/// packages get a small statement endpoint there. A completed or passed result marks the lesson done, with its score.
/// </summary>
public sealed class PackageRuntime(IDbSession session, TenantObjectStorage storage, Player player, IClock clock)
{
    public static readonly TimeSpan LaunchLifetime = TimeSpan.FromHours(8);
    public const string ContentBase = "/api/v1/learning/content";
    public const int MaxHtmlBytes = 5 * 1024 * 1024;
    public const int MaxReportBytes = 64 * 1024;
    public const int MaxStateBytes = 64 * 1024;
    public const int MaxStatements = 50;
    public const int MaxSuspendData = 4096;

    /// <summary>The content security policy of every package file: its own opaque origin, scripts allowed, framed only by TeamBeats.</summary>
    public const string ContentPolicy =
        "sandbox allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox; default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; connect-src 'self'; frame-ancestors 'self'";

    private static readonly HashSet<string> ScormKeys = new(StringComparer.Ordinal)
    {
        "cmi.core.lesson_status", "cmi.core.score.raw", "cmi.core.score.min", "cmi.core.score.max", "cmi.core.lesson_location", "cmi.suspend_data",
        "cmi.core.exit", "cmi.core.session_time",
    };

    private static readonly string[] ScormStatuses = ["passed", "completed", "failed", "incomplete", "browsed", "not attempted"];

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Root(string token) => $"{ContentBase}/{token}";

    /// <summary>
    /// Where the frame opens. xAPI packages get the tincan launch parameters: the endpoint (this launch's own statement
    /// endpoint), the actor as a TeamBeats account (no email leaves the product), the registration and the activity.
    /// </summary>
    public static string LaunchUrl(string token, string kind, string launchPath, string? activityId, Guid launchId, Guid learner)
    {
        var parts = launchPath.Split('?', 2);
        var path = string.Join('/', parts[0].Split('/').Select(Uri.EscapeDataString));
        var query = parts.Length == 2 ? parts[1] : "";
        if (kind == PackageKinds.Xapi)
        {
            var actor = JsonSerializer.Serialize(new { objectType = "Agent", account = new { homePage = "https://teambeats.app", name = learner.ToString() } });
            var xapi = string.Join('&',
                $"endpoint={Uri.EscapeDataString($"{Root(token)}/__tb/xapi/")}",
                $"auth={Uri.EscapeDataString("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("launch:" + token)))}",
                $"actor={Uri.EscapeDataString(actor)}",
                $"registration={launchId}",
                $"activity_id={Uri.EscapeDataString(activityId ?? "")}");
            query = query.Length == 0 ? xapi : $"{query}&{xapi}";
        }
        return $"{Root(token)}/{path}{(query.Length == 0 ? "" : "?" + query)}";
    }

    internal async Task<PackageLaunch?> FindAsync(string token, CancellationToken ct)
    {
        if (token.Length is < 20 or > 100)
        {
            return null;
        }
        return await session.QuerySingleOrDefaultAsync(
            """
            select l.id, l.assignment_id, l.lesson_key, l.package_id, l.course_version, p.kind, p.activity_id, l.state::text, l.status, l.reported_at, a.learner_id
            from learning.package_launch l join learning.package p on p.id = l.package_id join learning.assignment a on a.id = l.assignment_id
            where l.token_hash = @hash and l.expires_at > @now
            """, new { hash = Hash(token), now = clock.UtcNow },
            r => new PackageLaunch(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetInt32(4), r.GetString(5), r.Get<string>("activity_id"), r.GetString(7),
                r.Get<string>("status"), r.Get<DateTimeOffset?>("reported_at"), r.GetGuid(10)), ct);
    }

    /// <summary>A file of the launched package. HTML of a SCORM package gets the bridge script before anything else runs.</summary>
    public async Task<(Stream Content, string Type)?> OpenAsync(string token, string rawPath, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { } launch || Packages.CleanPath(rawPath) is not { } path)
        {
            return null;
        }
        if (await storage.OpenReadAsync(Packages.FileKey(launch.PackageId, path), ct) is not { } content)
        {
            return null;
        }
        var type = ContentTypes.For(path);
        if (type != "text/html" || launch.Kind != PackageKinds.Scorm12)
        {
            return (content, type);
        }
        await using (content)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            if (buffer.Length > MaxHtmlBytes)
            {
                return null;
            }
            var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return (new MemoryStream(Encoding.UTF8.GetBytes(Inject(html, $"<script src=\"{Root(token)}/__tb/scorm.js\"></script>"))), type);
        }
    }

    /// <summary>Puts the tag first in the head, so the SCORM API exists before the package looks for it.</summary>
    public static string Inject(string html, string tag)
    {
        var head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        var end = head >= 0 ? html.IndexOf('>', head) : -1;
        return end >= 0 ? html.Insert(end + 1, tag) : tag + html;
    }

    // ---- SCORM 1.2 ----

    /// <summary>The bridge for one launch: the SCORM 1.2 API, with the values this learner saved before.</summary>
    public async Task<string?> BridgeAsync(string token, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { Kind: PackageKinds.Scorm12 } launch)
        {
            return null;
        }
        var saved = JsonNode.Parse(launch.State)?["scorm"] as JsonObject ?? new JsonObject();
        saved["cmi.core.student_id"] = launch.LearnerId.ToString();
        return BridgeScript.Replace("__RUNTIME__", JsonSerializer.Serialize($"{Root(token)}/__tb/runtime"), StringComparison.Ordinal)
            .Replace("__STATE__", saved.ToJsonString(), StringComparison.Ordinal);
    }

    /// <summary>Values the package set: the ones the bridge may write are kept for the next launch; a final status records the result once.</summary>
    public async Task<bool> ReportScormAsync(string token, string body, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { Kind: PackageKinds.Scorm12 } launch || body.Length > MaxReportBytes)
        {
            return false;
        }
        JsonObject? values;
        try
        {
            values = JsonNode.Parse(body)?["values"] as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
        var state = JsonNode.Parse(launch.State) as JsonObject ?? new JsonObject();
        var scorm = state["scorm"] as JsonObject ?? new JsonObject();
        foreach (var (key, node) in values ?? new JsonObject())
        {
            var value = node?.GetValueKind() is JsonValueKind.String or JsonValueKind.Number ? node.ToString() : null;
            if (!ScormKeys.Contains(key) || value is null || value.Length > (key == "cmi.suspend_data" ? MaxSuspendData : 255))
            {
                continue;
            }
            if (key == "cmi.core.lesson_status" && !ScormStatuses.Contains(value))
            {
                continue;
            }
            scorm[key] = value;
        }
        state["scorm"] = scorm;
        var status = scorm["cmi.core.lesson_status"]?.ToString();
        int? score = Percent(scorm["cmi.core.score.raw"]?.ToString(), scorm["cmi.core.score.min"]?.ToString(), scorm["cmi.core.score.max"]?.ToString());
        await SaveAsync(launch, state, status is "passed" or "completed" or "failed" or "incomplete" ? status : null, score, ct);
        if (status is "passed" or "completed" || (status == "failed" && launch.Status != "failed"))
        {
            await ResultAsync(launch, status != "failed", score, ct);
        }
        return true;
    }

    private static int? Percent(string? raw, string? min, string? max)
    {
        static decimal? Number(string? s) => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        if (Number(raw) is not { } r)
        {
            return null;
        }
        var lo = Number(min) ?? 0;
        var hi = Number(max) ?? 100;
        var percent = hi > lo ? (r - lo) * 100 / (hi - lo) : r;
        return (int)Math.Round(Math.Clamp(percent, 0, 100));
    }

    private Task SaveAsync(PackageLaunch launch, JsonObject state, string? status, int? score, CancellationToken ct)
    {
        var json = state.ToJsonString();
        return json.Length > MaxStateBytes
            ? Task.CompletedTask
            : session.ExecAsync("update learning.package_launch set state = @state::jsonb, status = coalesce(@status, status), score = coalesce(@score, score) where id = @id",
                new { id = launch.Id, state = json, status, score }, ct);
    }

    /// <summary>
    /// Records a result: a pass or completion once per launch, with its score as an attempt and the lesson done; a failure
    /// as a failed attempt each time the package moves to failed, so the learner can try again in the same launch.
    /// </summary>
    private async Task ResultAsync(PackageLaunch launch, bool done, int? score, CancellationToken ct)
    {
        if (done && await session.ScalarAsync<bool?>("update learning.package_launch set reported_at = @now where id = @id and reported_at is null returning true",
                new { id = launch.Id, now = clock.UtcNow }, ct) != true)
        {
            return;
        }
        await player.RecordPackageAsync(launch.AssignmentId, launch.LessonKey, launch.CourseVersion, done, score, done, ct);
    }

    // ---- xAPI ----

    /// <summary>
    /// Statements from an xAPI package. "completed" or "passed" on the package's own activity completes the lesson, and
    /// "failed" records a failed attempt; the score is <c>result.score.scaled</c> or raw over max. Other statements are
    /// accepted and not kept.
    /// </summary>
    public async Task<IReadOnlyList<string>?> StatementsAsync(string token, string body, string? id, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { Kind: PackageKinds.Xapi } launch || body.Length > MaxReportBytes)
        {
            return null;
        }
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
        var statements = parsed is JsonArray list ? list.OfType<JsonObject>().ToList() : parsed is JsonObject one ? [one] : [];
        if (statements.Count is 0 or > MaxStatements)
        {
            return null;
        }
        var ids = new List<string>();
        foreach (var statement in statements)
        {
            ids.Add(statement["id"]?.ToString() is { Length: > 0 and <= 64 } given ? given : id ?? Guid.NewGuid().ToString());
            var verb = statement["verb"]?["id"]?.ToString() ?? "";
            var target = statement["object"]?["id"]?.ToString();
            if (target is null || !string.Equals(target, launch.ActivityId, StringComparison.Ordinal))
            {
                continue;
            }
            var outcome = verb switch
            {
                "http://adlnet.gov/expapi/verbs/completed" => "completed",
                "http://adlnet.gov/expapi/verbs/passed" => "passed",
                "http://adlnet.gov/expapi/verbs/failed" => "failed",
                _ => null,
            };
            if (outcome is null || (outcome == "failed" && launch.Status == "failed") || (outcome != "failed" && launch.ReportedAt is not null))
            {
                continue;
            }
            var result = statement["result"]?["score"];
            int? score = result?["scaled"]?.GetValueKind() == JsonValueKind.Number
                ? (int)Math.Round(Math.Clamp(result["scaled"]!.GetValue<decimal>(), 0, 1) * 100)
                : Percent(result?["raw"]?.ToString(), result?["min"]?.ToString(), result?["max"]?.ToString());
            await session.ExecAsync("update learning.package_launch set status = @outcome, score = coalesce(@score, score) where id = @id", new { id = launch.Id, outcome, score }, ct);
            await ResultAsync(launch, outcome != "failed", score, ct);
            launch = outcome == "failed" ? launch with { Status = outcome } : launch with { Status = outcome, ReportedAt = clock.UtcNow };
        }
        return ids;
    }

    /// <summary>An xAPI state document the package saved, by its state ID.</summary>
    public async Task<(bool Found, string? Document)> StateAsync(string token, string? stateId, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { Kind: PackageKinds.Xapi } launch || stateId is not { Length: > 0 and <= 200 })
        {
            return (false, null);
        }
        return (true, (JsonNode.Parse(launch.State)?["xapi"] as JsonObject)?[stateId]?.ToString());
    }

    public async Task<bool> SaveStateAsync(string token, string? stateId, string? document, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { Kind: PackageKinds.Xapi } launch || stateId is not { Length: > 0 and <= 200 })
        {
            return false;
        }
        var state = JsonNode.Parse(launch.State) as JsonObject ?? new JsonObject();
        var xapi = state["xapi"] as JsonObject ?? new JsonObject();
        if (document is null)
        {
            xapi.Remove(stateId);
        }
        else
        {
            xapi[stateId] = document;
        }
        state["xapi"] = xapi;
        if (state.ToJsonString().Length > MaxStateBytes)
        {
            return false;
        }
        await SaveAsync(launch, state, null, null, ct);
        return true;
    }

    /// <summary>
    /// The SCORM 1.2 API (the run-time calls a SCO makes) as a small in-house script, as the plan asks: values live in
    /// the page and are posted back as text, so no request needs a session or a preflight.
    /// </summary>
    private const string BridgeScript = """
        (function () {
          "use strict";
          var runtime = __RUNTIME__;
          var values = __STATE__;
          var dirty = {};
          var error = "0";
          var started = false;
          var finished = false;
          var readOnly = { "cmi.core.student_id": 1, "cmi.core.student_name": 1, "cmi.core.credit": 1, "cmi.core.entry": 1, "cmi.core.total_time": 1, "cmi.launch_data": 1, "cmi.core.lesson_mode": 1 };
          var defaults = {
            "cmi.core.lesson_status": "not attempted", "cmi.core.credit": "credit", "cmi.core.lesson_mode": "normal", "cmi.core.student_name": "",
            "cmi.core._children": "student_id,student_name,lesson_location,credit,lesson_status,entry,score,total_time,lesson_mode,exit,session_time",
            "cmi.core.score._children": "raw,min,max", "cmi._version": "3.4"
          };
          var messages = { "0": "No error", "101": "General exception", "301": "Not initialized", "401": "Not implemented error", "403": "Element is read only" };
          function send(finish) {
            var body = JSON.stringify({ values: dirty, finish: !!finish });
            dirty = {};
            try {
              if (finish && navigator.sendBeacon) { navigator.sendBeacon(runtime, new Blob([body], { type: "text/plain" })); }
              else { fetch(runtime, { method: "POST", body: body, headers: { "content-type": "text/plain" }, keepalive: true, credentials: "omit" }); }
            } catch (e) { }
          }
          window.API = {
            LMSInitialize: function () { if (started) { error = "101"; return "false"; } started = true; error = "0"; return "true"; },
            LMSFinish: function () { if (!started || finished) { error = "301"; return "false"; } finished = true; send(true); error = "0"; return "true"; },
            LMSGetValue: function (key) {
              error = "0";
              if (key === "cmi.core.entry") { return values["cmi.suspend_data"] ? "resume" : "ab-initio"; }
              if (Object.prototype.hasOwnProperty.call(values, key)) { return String(values[key]); }
              return Object.prototype.hasOwnProperty.call(defaults, key) ? defaults[key] : "";
            },
            LMSSetValue: function (key, value) {
              if (!started || finished) { error = "301"; return "false"; }
              if (readOnly[key]) { error = "403"; return "false"; }
              values[key] = String(value); dirty[key] = String(value); error = "0"; return "true";
            },
            LMSCommit: function () { if (!started) { error = "301"; return "false"; } send(false); error = "0"; return "true"; },
            LMSGetLastError: function () { return error; },
            LMSGetErrorString: function (code) { return messages[code] || ""; },
            LMSGetDiagnostic: function () { return ""; }
          };
          window.addEventListener("pagehide", function () { if (started && !finished && Object.keys(dirty).length > 0) { send(false); } });
        })();
        """;
}
