using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

public static class PackageKinds
{
    public const string Scorm12 = "scorm12";
    public const string Xapi = "xapi";
}

/// <summary>An imported package as learning administrators see it.</summary>
public sealed record PackageView(Guid Id, Guid CourseId, string Kind, string Title, string LaunchPath, int Files, long Bytes, DateTimeOffset CreatedAt);

/// <summary>What a package's manifest says: its kind, title, the file that starts it and, for xAPI, its activity ID.</summary>
public sealed record PackageManifest(string Kind, string Title, string LaunchPath, string? ActivityId);

/// <summary>
/// SCORM 1.2 and xAPI package import (spec 028 US7, FR-016). Learning administrators who manage the course upload a zip
/// of up to 500 MB. It is scanned (026) before anything is read from it, its entries are checked (no paths out of the
/// package, no zip bombs), its manifest is read (<c>imsmanifest.xml</c> for SCORM 1.2, <c>tincan.xml</c> for xAPI)
/// and its files are unpacked to tenant storage. A package lesson then points at it. Needs <c>learning.scorm</c>.
/// </summary>
public sealed partial class Packages(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    Courses courses,
    TenantObjectStorage storage,
    IMalwareScanner scanner,
    IClock clock)
{
    public const long MaxBytes = 500L * 1024 * 1024;
    public const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxFiles = 10_000;
    public const int MaxManifestBytes = 1024 * 1024;
    public const string Prefix = "learning/packages";

    private const string Columns = "id, course_id, kind, title, launch_path, files, bytes, created_at";

    private static PackageView Read(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt64(6), r.GetFieldValue<DateTimeOffset>(7));

    public static string FileKey(Guid package, string path) => $"{Prefix}/{package:N}/{path}";

    [GeneratedRegex(@"^[A-Za-z0-9 ._()\-+@~&,=!$'%\[\]]+(/[A-Za-z0-9 ._()\-+@~&,=!$'%\[\]]+)*$")]
    private static partial Regex SafePath();

    /// <summary>A path inside the package that stays inside it, or null.</summary>
    public static string? CleanPath(string? raw)
    {
        var path = raw?.Replace('\\', '/').TrimStart('/');
        // Storage refuses any "..", so a name that holds one (or an escaped slash around one) is refused here.
        if (string.IsNullOrEmpty(path) || path.Length > 300 || !SafePath().IsMatch(path) || path.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }
        return path.Split('/').Any(p => p is "." or ".." || p.Trim().Length == 0) ? null : path;
    }

    public async Task<IReadOnlyList<PackageView>> ListAsync(Guid courseId, CancellationToken ct)
    {
        await courses.ManagedAsync(courseId, false, ct);
        return await session.QueryAsync($"select {Columns} from learning.package where course_id = @courseId order by created_at desc, id", new { courseId }, Read, ct);
    }

    public async Task<PackageView> ImportAsync(Guid courseId, Stream body, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningScorm, ct);
        var row = await courses.ManagedAsync(courseId, false, ct);
        if (row.Status == CourseStatuses.Retired)
        {
            throw AppException.Conflict("conflict.learning.course_retired");
        }
        if (row.ExternalId is not null)
        {
            throw AppException.Conflict("conflict.learning.external_course");
        }
        var temp = Path.Combine(Path.GetTempPath(), $"tb-package-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        throw AppException.Validation("package", "validation.learning.package_size");
                    }
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total == 0)
                {
                    throw AppException.Validation("package", "validation.learning.package_zip");
                }
            }
            // Nothing is read from the package before the scanner calls it clean (026).
            await using (var scan = File.OpenRead(temp))
            {
                switch (await scanner.ScanAsync(scan, ct))
                {
                    case ScanVerdict.Infected:
                        throw AppException.Validation("package", "validation.learning.package_infected");
                    case ScanVerdict.Failed:
                        throw AppException.Conflict("conflict.learning.package_scan_failed");
                }
            }
            return await UnpackAsync(row, temp, ct);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private async Task<PackageView> UnpackAsync(Courses.CourseRow row, string temp, CancellationToken ct)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(temp);
        }
        catch (InvalidDataException)
        {
            throw AppException.Validation("package", "validation.learning.package_zip");
        }
        using (zip)
        {
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long unpacked = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') && entry.Length == 0)
                {
                    continue;
                }
                var path = CleanPath(entry.FullName);
                unpacked += entry.Length;
                // A file that expands more than 100 times over 10 MB is a zip bomb, not a course.
                var bomb = entry.Length > 10 * 1024 * 1024 && entry.Length > entry.CompressedLength * 100;
                if (path is null || bomb || files.Count >= MaxFiles || unpacked > MaxUnpackedBytes || !files.TryAdd(path, entry))
                {
                    throw AppException.Validation("package", "validation.learning.package_zip");
                }
            }
            var manifest = Manifest(files);
            var launchFile = manifest.LaunchPath.Split('?', 2)[0];
            if (!files.ContainsKey(launchFile))
            {
                throw AppException.Validation("package", "validation.learning.package_manifest");
            }
            var id = Ids.New();
            foreach (var (path, entry) in files)
            {
                await using var content = entry.Open();
                await storage.PutAsync(FileKey(id, path), content, ContentTypes.For(path), ct);
            }
            var now = clock.UtcNow;
            await session.ExecAsync(
                """
                insert into learning.package (tenant_id, id, course_id, kind, title, launch_path, activity_id, files, bytes, created_by, created_at)
                values (@tenant, @id, @course, @kind, @title, @launch, @activity, @files, @bytes, @user, @now)
                """, new
                {
                    tenant = context.RequireTenant(), id, course = row.Id, kind = manifest.Kind, title = manifest.Title, launch = manifest.LaunchPath,
                    activity = manifest.ActivityId, files = files.Count, bytes = unpacked, user = context.RequireUser(), now,
                }, ct);
            return new PackageView(id, row.Id, manifest.Kind, manifest.Title, manifest.LaunchPath, files.Count, unpacked, now);
        }
    }

    private static PackageManifest Manifest(IReadOnlyDictionary<string, ZipArchiveEntry> files)
    {
        if (files.TryGetValue("imsmanifest.xml", out var scorm))
        {
            return Scorm(Load(scorm));
        }
        if (files.TryGetValue("tincan.xml", out var tincan))
        {
            return Tincan(Load(tincan));
        }
        throw AppException.Validation("package", "validation.learning.package_manifest");
    }

    /// <summary>The manifest as XML, with no DTDs or external entities.</summary>
    private static XDocument Load(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxManifestBytes)
        {
            throw AppException.Validation("package", "validation.learning.package_manifest");
        }
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxManifestBytes * 2 });
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            throw AppException.Validation("package", "validation.learning.package_manifest");
        }
    }

    private static IEnumerable<XElement> Named(XContainer root, string local) => root.Descendants().Where(e => e.Name.LocalName == local);

    private static string Title(string? value) => value?.Trim() is { Length: > 0 } t ? (t.Length > 200 ? t[..200] : t) : "Package";

    /// <summary>
    /// SCORM 1.2: the first item of the default organization that points at a SCO, else the first SCO resource. SCORM
    /// 2004 manifests are refused (spec 028 open question: 1.2 and xAPI only in R2).
    /// </summary>
    public static PackageManifest Scorm(XDocument doc)
    {
        var root = doc.Root ?? throw AppException.Validation("package", "validation.learning.package_manifest");
        var version = Named(root, "schemaversion").FirstOrDefault()?.Value.Trim();
        if (version is not null && !version.StartsWith("1.2", StringComparison.Ordinal))
        {
            throw AppException.Validation("package", "validation.learning.package_version");
        }
        var resources = Named(root, "resource").ToList();
        static string? Type(XElement r) => r.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("scormtype", StringComparison.OrdinalIgnoreCase))?.Value;
        var organizations = Named(root, "organizations").FirstOrDefault();
        var defaultId = organizations?.Attribute("default")?.Value;
        var organization = organizations?.Elements().Where(e => e.Name.LocalName == "organization")
            .OrderByDescending(o => o.Attribute("identifier")?.Value == defaultId).FirstOrDefault();
        XElement? resource = null;
        foreach (var item in organization is null ? [] : Named(organization, "item"))
        {
            var reference = item.Attribute("identifierref")?.Value;
            resource = resources.FirstOrDefault(r => r.Attribute("identifier")?.Value == reference && r.Attribute("href") is not null);
            if (resource is not null)
            {
                break;
            }
        }
        resource ??= resources.FirstOrDefault(r => string.Equals(Type(r), "sco", StringComparison.OrdinalIgnoreCase) && r.Attribute("href") is not null);
        var href = resource?.Attribute("href")?.Value;
        var xmlBase = resource?.Attribute(XNamespace.Xml + "base")?.Value ?? "";
        var launch = href is null ? null : CleanLaunch(xmlBase + href);
        if (launch is null)
        {
            throw AppException.Validation("package", "validation.learning.package_manifest");
        }
        var title = organization?.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
        return new PackageManifest(PackageKinds.Scorm12, Title(title), launch, null);
    }

    /// <summary>xAPI (the tincan.xml launch convention): the first activity with a launch file.</summary>
    public static PackageManifest Tincan(XDocument doc)
    {
        var root = doc.Root ?? throw AppException.Validation("package", "validation.learning.package_manifest");
        var activity = Named(root, "activity").FirstOrDefault(a => a.Elements().Any(e => e.Name.LocalName == "launch"));
        var launch = CleanLaunch(activity?.Elements().First(e => e.Name.LocalName == "launch").Value.Trim());
        var id = activity?.Attribute("id")?.Value.Trim();
        if (launch is null || string.IsNullOrEmpty(id) || id.Length > 500 || !Uri.TryCreate(id, UriKind.Absolute, out _))
        {
            throw AppException.Validation("package", "validation.learning.package_manifest");
        }
        var title = activity!.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
        return new PackageManifest(PackageKinds.Xapi, Title(title), launch, id);
    }

    /// <summary>A launch file inside the package, with an optional query the package wants, or null.</summary>
    private static string? CleanLaunch(string? raw)
    {
        if (raw is null || raw.Length > 500 || raw.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        var parts = raw.Split('?', 2);
        var path = CleanPath(Uri.UnescapeDataString(parts[0]));
        if (path is null)
        {
            return null;
        }
        return parts.Length == 2 && parts[1].All(c => char.IsAsciiLetterOrDigit(c) || "=&-_.%+".Contains(c)) ? $"{path}?{parts[1]}" : path;
    }
}

/// <summary>Content types for package files. Anything else is served as bytes, never run.</summary>
public static class ContentTypes
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html", [".htm"] = "text/html", [".js"] = "text/javascript", [".mjs"] = "text/javascript", [".css"] = "text/css",
        [".json"] = "application/json", [".xml"] = "application/xml", [".txt"] = "text/plain", [".png"] = "image/png", [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".svg"] = "image/svg+xml", [".webp"] = "image/webp", [".ico"] = "image/x-icon",
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg", [".m4a"] = "audio/mp4",
        [".vtt"] = "text/vtt", [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf", [".pdf"] = "application/pdf",
    };

    public static string For(string path) => Types.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";
}
