using System.Globalization;
using System.Text;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

/// <summary>One row of a bulk import manifest: the file in the ZIP and what to set on the document.</summary>
public sealed record ManifestRow(int Number, string Path, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// The manifest of a bulk import (spec 011 FR-022): <c>manifest.csv</c> at the root of the ZIP, UTF-8, comma separated,
/// with a header row. <c>path</c> names the file in the ZIP and is required; <c>folder</c> (folders under the import's
/// folder, separated by /), <c>name</c>, <c>type</c> (a document type's name or id), <c>classification</c>,
/// <c>created</c>, <c>modified</c> (ISO 8601) and <c>author</c> (an email) are optional; every
/// <c>field:{key}</c> column fills that metadata field.
/// </summary>
public static class ImportManifest
{
    public const string FileName = "manifest.csv";
    public const int MaxRows = 10_000;
    public const long MaxBytes = 20L * 1024 * 1024;
    public const string FieldPrefix = "field:";

    public static readonly IReadOnlySet<string> Columns = new HashSet<string>(StringComparer.Ordinal)
    {
        "path", "folder", "name", "type", "classification", "created", "modified", "author",
    };

    /// <summary>Reads the manifest, refusing it whole when its header or shape is wrong; each row is judged later on its own.</summary>
    public static IReadOnlyList<ManifestRow> Parse(string text)
    {
        var records = Records(text.TrimStart('﻿')).Where(r => r.Count > 1 || r[0].Length > 0).ToList();
        if (records.Count == 0)
        {
            throw AppException.Validation("manifest", "validation.documents.import_manifest");
        }
        var header = records[0].Select(h => h.Trim()).ToList();
        if (header.Any(h => !Columns.Contains(h) && !(h.StartsWith(FieldPrefix, StringComparison.Ordinal) && h.Length > FieldPrefix.Length))
            || header.Distinct(StringComparer.Ordinal).Count() != header.Count || !header.Contains("path"))
        {
            throw AppException.Validation("manifest", "validation.documents.import_columns");
        }
        if (records.Count - 1 > MaxRows)
        {
            throw AppException.Validation("manifest", "validation.documents.import_rows");
        }
        var rows = new List<ManifestRow>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Count != header.Count)
            {
                throw AppException.Validation("manifest", "validation.documents.import_manifest");
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < header.Count; c++)
            {
                if (record[c].Trim() is { Length: > 0 } value)
                {
                    values[header[c]] = value;
                }
            }
            var path = NormalizePath(values.GetValueOrDefault("path"));
            if (path is null || !paths.Add(path))
            {
                throw AppException.Validation("manifest", "validation.documents.import_paths");
            }
            values.Remove("path");
            rows.Add(new ManifestRow(i, path, values));
        }
        if (rows.Count == 0)
        {
            throw AppException.Validation("manifest", "validation.documents.import_manifest");
        }
        return rows;
    }

    /// <summary>A path inside the ZIP with forward slashes and no leading slash; null when empty or climbing out.</summary>
    public static string? NormalizePath(string? path)
    {
        var clean = (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        if (clean.Length == 0 || clean.Length > 1000 || clean.Split('/').Any(p => p is "" or "." or ".."))
        {
            return null;
        }
        return clean;
    }

    /// <summary>The folder names of a folder column, or null when one is not a valid folder name.</summary>
    public static IReadOnlyList<string>? FolderPath(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return [];
        }
        var parts = folder.Replace('\\', '/').Split('/').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count > 20 || parts.Any(p => p.Length is 0 or > 200 || p.Any(char.IsControl) || p is "." or "..") ? null : parts;
    }

    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss",
    ];

    /// <summary>A date or date and time; a date alone is midnight UTC; a time without an offset is UTC. Never in the future.</summary>
    public static DateTimeOffset? Date(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (DateTimeOffset.TryParseExact(value.Trim(), IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            && at.Year >= 1900 && at <= now)
        {
            return at;
        }
        throw AppException.Validation("dates", "validation.documents.import_date");
    }

    /// <summary>RFC 4180 records: commas, quoted fields with doubled quotes, CRLF or LF line ends.</summary>
    internal static IEnumerable<List<string>> Records(string text)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    break;
                case '\n' or '\r':
                    record.Add(field.ToString());
                    field.Clear();
                    yield return record;
                    record = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        if (quoted)
        {
            throw AppException.Validation("manifest", "validation.documents.import_manifest");
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }
}
