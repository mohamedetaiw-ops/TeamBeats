using System.Security.Cryptography;
using TeamBeats.Modules.Documents;
using TeamBeats.Modules.Security.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 011 R3: the chunked AES-GCM format of files in libraries on the customer's key.</summary>
public sealed class FileCipherTests
{
    private static readonly TenantDataKey Key = new(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));

    private static async Task<byte[]> SealAsync(byte[] plain, TenantDataKey key)
    {
        using var output = new MemoryStream();
        await FileCipher.EncryptAsync(new MemoryStream(plain), output, key, CancellationToken.None);
        return output.ToArray();
    }

    private static async Task<byte[]> OpenAsync(byte[] sealedBytes, TenantDataKey key)
    {
        var header = sealedBytes[..FileCipher.HeaderSize];
        using var output = new MemoryStream();
        await FileCipher.DecryptAsync(header, new MemoryStream(sealedBytes[FileCipher.HeaderSize..]), output, key, CancellationToken.None);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(FileCipher.ChunkSize)]
    [InlineData(FileCipher.ChunkSize + 1)]
    [InlineData(3 * FileCipher.ChunkSize + 17)]
    public async Task Files_of_any_size_open_as_they_were_sealed(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        var sealedBytes = await SealAsync(plain, Key);
        Assert.Equal(Key.Id, FileCipher.KeyIdOf(sealedBytes));
        Assert.Equal(plain, await OpenAsync(sealedBytes, Key));
    }

    [Fact]
    public async Task A_changed_cut_or_wrongly_keyed_file_does_not_open()
    {
        var plain = RandomNumberGenerator.GetBytes(2 * FileCipher.ChunkSize + 100);
        var sealedBytes = await SealAsync(plain, Key);

        var changed = (byte[])sealedBytes.Clone();
        changed[FileCipher.HeaderSize + 10] ^= 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(changed, Key));

        // Dropping the last chunk leaves a chunk that was not sealed as the last one.
        var cut = sealedBytes[..(FileCipher.HeaderSize + 2 * (FileCipher.ChunkSize + 16))];
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(cut, Key));

        var otherHeader = (byte[])sealedBytes.Clone();
        otherHeader[FileCipher.HeaderSize - 1] ^= 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(otherHeader, Key));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(sealedBytes, new TenantDataKey(Key.Id, RandomNumberGenerator.GetBytes(32))));
    }

    [Fact]
    public void Only_document_file_keys_name_a_library_and_plain_bytes_are_not_taken_for_a_sealed_file()
    {
        var library = Guid.NewGuid();
        Assert.Equal(library, DocumentStorage.LibraryOf($"documents/{library:N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}.preview.pdf"));
        Assert.Null(DocumentStorage.LibraryOf($"assets/{library:N}/x/original"));
        Assert.Null(DocumentStorage.LibraryOf($"documents/{library:D}/x"));
        Assert.Null(DocumentStorage.LibraryOf("documents/short"));
        Assert.Null(FileCipher.KeyIdOf("plain text that is long enough to be a header"u8));
    }
}
