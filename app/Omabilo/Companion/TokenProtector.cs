// Encrypts Google tokens before they are stored: AES-256-GCM with a 32-byte key kept in the IKON
// secret OMABILO_TOKEN_KEY, and the owner's user id as associated data, so a ciphertext copied onto
// another person's row fails to decrypt instead of granting that person's mailbox.

using System.Security.Cryptography;
using System.Text;

public sealed class TokenProtector
{
    private const string Prefix = "v1:";
    private readonly byte[] _key;

    public TokenProtector(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The token key must be 32 bytes (base64 of 32 random bytes).");
        }

        _key = key;
    }

    /// <summary>Null when the secret is missing or not a 32-byte base64 key, so the app can say what to configure.</summary>
    public static TokenProtector? FromBase64(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        try
        {
            // A value piped in from Windows PowerShell can carry an invisible byte-order mark
            var key = Convert.FromBase64String(secret.Trim().Trim((char)0xFEFF).Trim());
            return key.Length == 32 ? new TokenProtector(key) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string Protect(string plaintext, string userId)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(userId));

        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    /// <summary>The plaintext, or null when the value was not encrypted for this user with this key.</summary>
    public string? Unprotect(string? protectedValue, string userId)
    {
        if (protectedValue is null || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var all = Convert.FromBase64String(protectedValue[Prefix.Length..]);

            if (all.Length < 28)
            {
                return null;
            }

            var nonce = all[..12];
            var tag = all[^16..];
            var cipher = all[12..^16];
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(userId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
