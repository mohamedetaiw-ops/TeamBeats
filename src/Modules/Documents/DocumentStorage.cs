using System.Buffers.Binary;
using System.Security.Cryptography;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Envelope encryption of stored files with the tenant data key (spec 011 R3, spec 026 FR-007). The format is a header
/// (magic, data key id, nonce prefix) followed by chunks of 64 KiB, each sealed with AES-256-GCM. A chunk's nonce is the
/// prefix and its index, with the top bit set on the last chunk, and the header is the associated data of every chunk,
/// so chunks cannot be reordered, dropped, cut short or moved to another file without failing to open.
/// </summary>
internal static class FileCipher
{
    public const int ChunkSize = 64 * 1024;
    private const int TagSize = 16;
    private const uint LastFlag = 0x8000_0000u;
    public static readonly byte[] Magic = [0x00, (byte)'T', (byte)'B', (byte)'E', (byte)'N', (byte)'C', (byte)'1', 0x00];
    public const int HeaderSize = 8 + 16 + 8;

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                break;
            }
            read += n;
        }
        return read;
    }

    private static void NonceFor(Span<byte> nonce, ReadOnlySpan<byte> header, uint index, bool last)
    {
        if (index >= LastFlag)
        {
            throw new InvalidOperationException("The file is too large to encrypt.");
        }
        header.Slice(24, 8).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[8..], index | (last ? LastFlag : 0));
    }

    public static async Task EncryptAsync(Stream plain, Stream output, TenantDataKey key, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        key.Id.TryWriteBytes(header.AsSpan(8, 16));
        RandomNumberGenerator.Fill(header.AsSpan(24, 8));
        await output.WriteAsync(header, ct);
        using var aes = new AesGcm(key.Key, TagSize);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var sealedChunk = new byte[ChunkSize + TagSize];
        var nonce = new byte[12];
        var length = await ReadFullAsync(plain, current, ct);
        for (uint index = 0; ; index++)
        {
            var nextLength = length == ChunkSize ? await ReadFullAsync(plain, next, ct) : 0;
            var last = nextLength == 0;
            NonceFor(nonce, header, index, last);
            aes.Encrypt(nonce, current.AsSpan(0, length), sealedChunk.AsSpan(0, length), sealedChunk.AsSpan(length, TagSize), header);
            await output.WriteAsync(sealedChunk.AsMemory(0, length + TagSize), ct);
            if (last)
            {
                return;
            }
            (current, next) = (next, current);
            length = nextLength;
        }
    }

    /// <summary>The data key id of an encrypted file's header, or null when the bytes are not one.</summary>
    public static Guid? KeyIdOf(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderSize && header[..8].SequenceEqual(Magic) ? new Guid(header.Slice(8, 16)) : null;

    /// <summary>Opens an encrypted file whose header has been read already. Throws when any chunk does not open.</summary>
    public static async Task DecryptAsync(byte[] header, Stream sealedStream, Stream output, TenantDataKey key, CancellationToken ct)
    {
        using var aes = new AesGcm(key.Key, TagSize);
        var current = new byte[ChunkSize + TagSize];
        var next = new byte[ChunkSize + TagSize];
        var plain = new byte[ChunkSize];
        var nonce = new byte[12];
        var length = await ReadFullAsync(sealedStream, current, ct);
        for (uint index = 0; ; index++)
        {
            if (length < TagSize)
            {
                throw new CryptographicException("The encrypted file is cut short.");
            }
            var nextLength = length == ChunkSize + TagSize ? await ReadFullAsync(sealedStream, next, ct) : 0;
            var last = nextLength == 0;
            NonceFor(nonce, header, index, last);
            var size = length - TagSize;
            aes.Decrypt(nonce, current.AsSpan(0, size), current.AsSpan(size, TagSize), plain.AsSpan(0, size), header);
            await output.WriteAsync(plain.AsMemory(0, size), ct);
            if (last)
            {
                return;
            }
            (current, next) = (next, current);
            length = nextLength;
        }
    }

    /// <summary>A seekable scratch file that is deleted when it is closed.</summary>
    public static FileStream Scratch() =>
        new(Path.Combine(Path.GetTempPath(), "tb-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
}

/// <summary>
/// The object storage of document files (spec 011 R3). Files of a library that uses the customer's key are encrypted
/// with the tenant data key on the way in and opened on the way out; every other file passes through unchanged. Only
/// files under <c>documents/{library}/</c> of such a library are ever checked for the encrypted format, so a plain file
/// that happens to start with the same bytes elsewhere is never misread.
/// </summary>
public sealed class DocumentStorage(TenantObjectStorage inner, IDbSession session, ITenantDataKeys keys)
{
    private readonly Dictionary<Guid, bool> encrypted = [];

    /// <summary>The library of a document file key (<c>documents/{library:N}/...</c>), or null for any other key.</summary>
    internal static Guid? LibraryOf(string key)
    {
        const string Prefix = "documents/";
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length < Prefix.Length + 33 || key[Prefix.Length + 32] != '/')
        {
            return null;
        }
        return Guid.TryParseExact(key.AsSpan(Prefix.Length, 32), "N", out var id) ? id : null;
    }

    internal async Task<bool> EncryptedAsync(Guid library, CancellationToken ct)
    {
        if (!encrypted.TryGetValue(library, out var value))
        {
            value = await session.ScalarAsync<bool>("select coalesce((select customer_key from documents.library where id = @library), false)", new { library }, ct);
            encrypted[library] = value;
        }
        return value;
    }

    private static AppException Unavailable() => new(ProblemCodes.TenantKeyUnavailable, 503);

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        if (LibraryOf(key) is not { } library || !await EncryptedAsync(library, ct))
        {
            await inner.PutAsync(key, content, contentType, ct);
            return;
        }
        var dataKey = await keys.CurrentAsync(ct) ?? throw Unavailable();
        await using var scratch = FileCipher.Scratch();
        await FileCipher.EncryptAsync(content, scratch, dataKey, ct);
        scratch.Position = 0;
        await inner.PutAsync(key, scratch, contentType, ct);
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var stored = await inner.OpenReadAsync(key, ct);
        if (stored is null || LibraryOf(key) is not { } library || !await EncryptedAsync(library, ct))
        {
            return stored;
        }
        await using (stored)
        {
            var header = new byte[FileCipher.HeaderSize];
            var read = 0;
            while (read < header.Length && await stored.ReadAsync(header.AsMemory(read), ct) is var n and > 0)
            {
                read += n;
            }
            var scratch = FileCipher.Scratch();
            try
            {
                if (FileCipher.KeyIdOf(header.AsSpan(0, read)) is { } id)
                {
                    var dataKey = await keys.FindAsync(id, ct) ?? throw Unavailable();
                    await FileCipher.DecryptAsync(header, stored, scratch, dataKey, ct);
                }
                else
                {
                    // Not moved onto the customer's key yet (the background pass does it): the file as stored.
                    await scratch.WriteAsync(header.AsMemory(0, read), ct);
                    await stored.CopyToAsync(scratch, ct);
                }
                scratch.Position = 0;
                return scratch;
            }
            catch
            {
                await scratch.DisposeAsync();
                throw;
            }
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);

    /// <summary>
    /// Rewrites one stored file under the tenant data key, unless it already is. Used by the background pass when a
    /// library starts using the customer's key. Returns false when there is no such file.
    /// </summary>
    internal async Task<bool> EncryptInPlaceAsync(string key, CancellationToken ct)
    {
        await using var stored = await inner.OpenReadAsync(key, ct);
        if (stored is null)
        {
            return false;
        }
        var header = new byte[FileCipher.HeaderSize];
        var read = 0;
        while (read < header.Length && await stored.ReadAsync(header.AsMemory(read), ct) is var n and > 0)
        {
            read += n;
        }
        if (FileCipher.KeyIdOf(header.AsSpan(0, read)) is not null)
        {
            return true;
        }
        var dataKey = await keys.CurrentAsync(ct) ?? throw Unavailable();
        await using var plain = FileCipher.Scratch();
        await plain.WriteAsync(header.AsMemory(0, read), ct);
        await stored.CopyToAsync(plain, ct);
        plain.Position = 0;
        await using var sealedFile = FileCipher.Scratch();
        await FileCipher.EncryptAsync(plain, sealedFile, dataKey, ct);
        sealedFile.Position = 0;
        await stored.DisposeAsync();
        await inner.PutAsync(key, sealedFile, "application/octet-stream", ct);
        return true;
    }
}
