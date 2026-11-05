using TeamBeats.TenantContext;

namespace TeamBeats.Persistence;

/// <summary>S3-compatible object storage abstraction. R0 ships a file-system implementation; an S3 adapter follows.</summary>
public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed class FileSystemObjectStorage(string root) : IObjectStorage
{
    private string PathFor(string key)
    {
        ObjectKeys.Validate(key);
        var full = Path.GetFullPath(Path.Combine(root, key));
        var rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Object key escapes the storage root.");
        }
        return full;
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}

public static class ObjectKeys
{
    public static void Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith('/') || key.Contains("..", StringComparison.Ordinal) || key.Contains('\\'))
        {
            throw new ArgumentException("Invalid object key.", nameof(key));
        }
    }
}

/// <summary>
/// Object storage scoped to the current tenant: every key is placed under <c>tenants/{tenant_id}/</c>
/// (constitution I: tenant context travels with object storage paths). Modules never build the prefix themselves.
/// </summary>
public sealed class TenantObjectStorage(IObjectStorage inner, ITenantContext context)
{
    public string FullKey(string relativeKey)
    {
        ObjectKeys.Validate(relativeKey);
        return $"tenants/{context.RequireTenant():D}/{relativeKey}";
    }

    public Task PutAsync(string relativeKey, Stream content, string contentType, CancellationToken ct = default) =>
        inner.PutAsync(FullKey(relativeKey), content, contentType, ct);

    public Task<Stream?> OpenReadAsync(string relativeKey, CancellationToken ct = default) =>
        inner.OpenReadAsync(FullKey(relativeKey), ct);

    public Task DeleteAsync(string relativeKey, CancellationToken ct = default) =>
        inner.DeleteAsync(FullKey(relativeKey), ct);
}
