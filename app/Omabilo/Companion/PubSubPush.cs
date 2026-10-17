// Gmail push notifications arrive as Google Cloud Pub/Sub "push" requests. A notice says only which
// mailbox changed and its new history id; it never carries mail. It is still checked before anything
// happens, because the endpoint is public: either Pub/Sub's signed OIDC token (Google's signature, our
// audience, the configured service account) or a long random token in the push URL must be present.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>What one Gmail notice says.</summary>
public sealed record GmailPushNotice(string EmailAddress, ulong HistoryId, string? MessageId);

/// <summary>How the push endpoint is configured. Null fields switch that check off; at least one way to authenticate is required.</summary>
public sealed record PushConfig(string Topic, string Audience, string? ServiceAccountEmail, string? UrlToken)
{
    public bool CanAuthenticate => ServiceAccountEmail != null || UrlToken != null;
}

public static class PushNotices
{
    /// <summary>Reads Pub/Sub's push envelope. Null when it isn't a Gmail notice.</summary>
    public static GmailPushNotice? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("data", out var data) || data.GetString() is not { Length: > 0 } b64)
            {
                return null;
            }

            var messageId = message.TryGetProperty("messageId", out var m) ? m.GetString() : message.TryGetProperty("message_id", out var m2) ? m2.GetString() : null;
            using var inner = JsonDocument.Parse(Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/').PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=')));
            var root = inner.RootElement;

            if (!root.TryGetProperty("emailAddress", out var email) || email.GetString() is not { Length: > 0 } address || !root.TryGetProperty("historyId", out var h))
            {
                return null;
            }

            var historyText = h.ValueKind == JsonValueKind.String ? h.GetString() : h.GetRawText();
            return ulong.TryParse(historyText, out var history) ? new GmailPushNotice(address, history, messageId) : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Google's public signing keys, fetched from Google and cached; tests supply their own.</summary>
public interface IGoogleSigningKeys
{
    Task<RSA?> FindAsync(string keyId, CancellationToken ct);
}

public sealed class GoogleSigningKeys(HttpClient http) : IGoogleSigningKeys
{
    public const string CertsUrl = "https://www.googleapis.com/oauth2/v3/certs";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, RSAParameters> _keys = new();
    private DateTime _fetchedUtc = DateTime.MinValue;

    public async Task<RSA?> FindAsync(string keyId, CancellationToken ct)
    {
        // Keys rotate: refresh hourly, and at once for an unknown key id (at most once a minute)
        var stale = DateTime.UtcNow - _fetchedUtc > TimeSpan.FromHours(1);
        var unknown = !_keys.ContainsKey(keyId) && DateTime.UtcNow - _fetchedUtc > TimeSpan.FromMinutes(1);

        if (stale || unknown)
        {
            await _gate.WaitAsync(ct);

            try
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync(CertsUrl, ct));
                var keys = new Dictionary<string, RSAParameters>();

                foreach (var k in doc.RootElement.GetProperty("keys").EnumerateArray())
                {
                    if (k.GetProperty("kty").GetString() == "RSA")
                    {
                        keys[k.GetProperty("kid").GetString()!] = new RSAParameters
                        {
                            Modulus = Pkce.FromBase64Url(k.GetProperty("n").GetString()!),
                            Exponent = Pkce.FromBase64Url(k.GetProperty("e").GetString()!),
                        };
                    }
                }

                _keys = keys;
                _fetchedUtc = DateTime.UtcNow;
            }
            finally
            {
                _gate.Release();
            }
        }

        if (!_keys.TryGetValue(keyId, out var p))
        {
            return null;
        }

        var rsa = RSA.Create();
        rsa.ImportParameters(p);
        return rsa;
    }
}

public sealed record PushAuthResult(bool Ok, string Method, string? Problem);

public sealed class PushAuthenticator(IGoogleSigningKeys keys, Func<DateTime> clock)
{
    private static readonly string[] Issuers = ["https://accounts.google.com", "accounts.google.com"];

    /// <summary>Checks the request; Problem is a short category for the log, never a token.</summary>
    public async Task<PushAuthResult> CheckAsync(PushConfig config, string? authorizationHeader, string? urlToken, CancellationToken ct)
    {
        if (config.ServiceAccountEmail != null && authorizationHeader is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var problem = await CheckOidcAsync(config, auth["Bearer ".Length..].Trim(), ct);
            return problem == null ? new(true, "oidc", null) : new(false, "oidc", problem);
        }

        if (config.UrlToken != null && urlToken != null)
        {
            var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(urlToken), Encoding.UTF8.GetBytes(config.UrlToken));
            return ok ? new(true, "url_token", null) : new(false, "url_token", "token_mismatch");
        }

        return new(false, "none", authorizationHeader == null ? "no_credentials" : "unexpected_credentials");
    }

    private async Task<string?> CheckOidcAsync(PushConfig config, string jwt, CancellationToken ct)
    {
        var parts = jwt.Split('.');

        if (parts.Length != 3)
        {
            return "malformed_token";
        }

        JsonElement header, claims;

        try
        {
            header = JsonDocument.Parse(Pkce.FromBase64Url(parts[0])).RootElement.Clone();
            claims = JsonDocument.Parse(Pkce.FromBase64Url(parts[1])).RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return "malformed_token";
        }

        if (!header.TryGetProperty("alg", out var alg) || alg.GetString() != "RS256" || !header.TryGetProperty("kid", out var kid) || kid.GetString() is not { } keyId)
        {
            return "unsupported_algorithm";
        }

        using var rsa = await keys.FindAsync(keyId, ct);

        if (rsa == null)
        {
            return "unknown_signing_key";
        }

        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);

        if (!rsa.VerifyData(signed, Pkce.FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return "bad_signature";
        }

        if (!claims.TryGetProperty("iss", out var iss) || !Issuers.Contains(iss.GetString()))
        {
            return "wrong_issuer";
        }

        if (!claims.TryGetProperty("aud", out var aud) || aud.GetString() != config.Audience)
        {
            return "wrong_audience";
        }

        var now = new DateTimeOffset(DateTime.SpecifyKind(clock(), DateTimeKind.Utc)).ToUnixTimeSeconds();

        if (!claims.TryGetProperty("exp", out var exp) || exp.GetInt64() < now - 300)
        {
            return "expired";
        }

        if (claims.TryGetProperty("iat", out var iat) && iat.GetInt64() > now + 300)
        {
            return "issued_in_future";
        }

        if (!claims.TryGetProperty("email", out var email) || !string.Equals(email.GetString(), config.ServiceAccountEmail, StringComparison.OrdinalIgnoreCase))
        {
            return "wrong_service_account";
        }

        if (!claims.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True)
        {
            return "email_not_verified";
        }

        return null;
    }
}
