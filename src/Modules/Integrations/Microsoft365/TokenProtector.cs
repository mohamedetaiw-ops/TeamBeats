using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// Encrypts delegated tokens at rest with AES-GCM (spec 003 FR-016, spec 018 section 14). The 256-bit key is platform
/// configuration (<c>Integrations:Microsoft365:TokenKey</c>, base64) filled from the secret store. The associated data
/// binds each ciphertext to its tenant, user and session, so a row copied anywhere else does not decrypt.
/// </summary>
public sealed class TokenProtector(IConfiguration configuration)
{
    public const string KeySetting = "Integrations:Microsoft365:TokenKey";

    public bool Configured => Key() is not null;

    public (byte[] Nonce, byte[] Ciphertext) Protect(string token, Guid tenantId, Guid userId, Guid sessionId)
    {
        var key = Key() ?? throw new GraphUnavailableException("Delegated token encryption is not configured on this installation.");
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = Encoding.UTF8.GetBytes(token);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Associated(tenantId, userId, sessionId));
        }
        CryptographicOperations.ZeroMemory(plain);
        return (nonce, [.. cipher, .. tag]);
    }

    /// <summary>Returns the token, or null when the key, binding or ciphertext does not match.</summary>
    public string? Unprotect(byte[] nonce, byte[] ciphertext, Guid tenantId, Guid userId, Guid sessionId)
    {
        var key = Key();
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        if (key is null || ciphertext.Length < tagSize)
        {
            return null;
        }
        var plain = new byte[ciphertext.Length - tagSize];
        try
        {
            using var aes = new AesGcm(key, tagSize);
            aes.Decrypt(nonce, ciphertext.AsSpan(0, plain.Length), ciphertext.AsSpan(plain.Length), plain, Associated(tenantId, userId, sessionId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private byte[]? Key()
    {
        var value = configuration[KeySetting];
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        try
        {
            var key = Convert.FromBase64String(value);
            return key.Length == 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] Associated(Guid tenantId, Guid userId, Guid sessionId) =>
        Encoding.ASCII.GetBytes($"tb-delegated-token/v1/{tenantId:N}/{userId:N}/{sessionId:N}");
}
