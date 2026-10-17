// Google OAuth 2.0 (authorization code + PKCE) and the Gmail REST API, over a plain HttpClient so
// the tests can stand in for Google with a fake handler. Read-only: the only scope requested is
// gmail.readonly, and nothing here can send, change or delete mail.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record GoogleOAuthConfig(string ClientId, string ClientSecret, string RedirectUri);

public sealed record GoogleTokens(string AccessToken, DateTime ExpiresUtc, string? RefreshToken, string Scope);

public sealed record GmailProfile(string EmailAddress, string HistoryId);

public sealed record GmailMessageMeta(string Id, string ThreadId, string From, string Subject, DateTime ReceivedUtc, string Snippet, string? HistoryId);

public sealed record GmailAttachmentRef(string FileName, string MimeType, long Size, string AttachmentId);

public sealed record GmailFullMessage(GmailMessageMeta Meta, string BodyText, IReadOnlyList<GmailAttachmentRef> Attachments);

public sealed record GmailHistoryPage(IReadOnlyList<string> AddedMessageIds, string? NextPageToken, string HistoryId);

/// <summary>Google refused the grant (revoked, expired or never given): the person has to reconnect.</summary>
public sealed class GoogleAuthException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A Gmail API error. Retryable ones (rate limits, server errors) are retried with backoff.</summary>
public sealed class GmailApiException(int status, string reason, bool retryable) : Exception($"Gmail API {status} {reason}")
{
    public int Status { get; } = status;
    public string Reason { get; } = reason;
    public bool Retryable { get; } = retryable;
}

/// <summary>The stored history id is too old for Gmail: a bounded rescan is needed.</summary>
public sealed class GmailHistoryExpiredException() : Exception("Gmail history id expired");

public interface IGmailApi
{
    Task<GmailProfile> GetProfileAsync(string accessToken, CancellationToken ct);
    Task<(IReadOnlyList<string> Ids, string? NextPageToken)> ListMessagesAsync(string accessToken, string query, string? pageToken, int pageSize, CancellationToken ct);
    Task<GmailMessageMeta> GetMetadataAsync(string accessToken, string id, CancellationToken ct);
    Task<GmailFullMessage> GetFullAsync(string accessToken, string id, CancellationToken ct);
    Task<byte[]> GetAttachmentAsync(string accessToken, string messageId, string attachmentId, CancellationToken ct);
    Task<GmailHistoryPage> ListHistoryAsync(string accessToken, string startHistoryId, string? pageToken, CancellationToken ct);
}

public interface IGoogleOAuth
{
    string BuildAuthorizationUrl(GoogleOAuthConfig config, string state, string codeChallenge);
    Task<GoogleTokens> ExchangeCodeAsync(GoogleOAuthConfig config, string code, string codeVerifier, CancellationToken ct);
    Task<GoogleTokens> RefreshAsync(GoogleOAuthConfig config, string refreshToken, CancellationToken ct);
    Task RevokeAsync(string token, CancellationToken ct);
}

public static class GoogleScopes
{
    /// <summary>Read messages and their attachments. Restricted by Google: needs app verification
    /// before the OAuth consent screen leaves Testing mode.</summary>
    public const string GmailReadOnly = "https://www.googleapis.com/auth/gmail.readonly";
}

public static class Pkce
{
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }
}

public sealed class GoogleClient(HttpClient http) : IGoogleOAuth, IGmailApi
{
    public const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string GmailBase = "https://gmail.googleapis.com/gmail/v1/users/me";

    // ── OAuth ───────────────────────────────────────────────────────────────

    public string BuildAuthorizationUrl(GoogleOAuthConfig config, string state, string codeChallenge)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = config.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = GoogleScopes.GmailReadOnly,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "false",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        return AuthEndpoint + "?" + string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    public Task<GoogleTokens> ExchangeCodeAsync(GoogleOAuthConfig config, string code, string codeVerifier, CancellationToken ct) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["redirect_uri"] = config.RedirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier,
        }, null, ct);

    public Task<GoogleTokens> RefreshAsync(GoogleOAuthConfig config, string refreshToken, CancellationToken ct) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, refreshToken, ct);

    private async Task<GoogleTokens> TokenRequestAsync(Dictionary<string, string> form, string? keepRefreshToken, CancellationToken ct)
    {
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryReadString(body, "error") ?? "unknown_error";

            // invalid_grant: the code or refresh token was revoked, expired or already used
            if (error is "invalid_grant" or "unauthorized_client" or "invalid_client")
            {
                throw new GoogleAuthException(error, "Google refused the authorization");
            }

            throw new GmailApiException((int)response.StatusCode, error, (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        return new GoogleTokens(
            root.GetProperty("access_token").GetString()!,
            DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)),
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() : keepRefreshToken,
            root.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "");
    }

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var response = await http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);

        // 400 means Google no longer knows the token (already revoked or expired): the goal is met
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
        {
            throw new GmailApiException((int)response.StatusCode, "revoke_failed", (int)response.StatusCode >= 500);
        }
    }

    // ── Gmail ───────────────────────────────────────────────────────────────

    public async Task<GmailProfile> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(accessToken, $"{GmailBase}/profile", ct);
        return new GmailProfile(doc.RootElement.GetProperty("emailAddress").GetString()!, doc.RootElement.GetProperty("historyId").GetString()!);
    }

    public async Task<(IReadOnlyList<string> Ids, string? NextPageToken)> ListMessagesAsync(string accessToken, string query, string? pageToken, int pageSize, CancellationToken ct)
    {
        var url = $"{GmailBase}/messages?q={Uri.EscapeDataString(query)}&maxResults={Math.Clamp(pageSize, 1, 500)}"
            + (pageToken != null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");
        using var doc = await GetJsonAsync(accessToken, url, ct);
        var ids = doc.RootElement.TryGetProperty("messages", out var m)
            ? m.EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList()
            : [];
        return (ids, doc.RootElement.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null);
    }

    public async Task<GmailMessageMeta> GetMetadataAsync(string accessToken, string id, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(accessToken,
            $"{GmailBase}/messages/{Uri.EscapeDataString(id)}?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date", ct);
        return ParseMeta(doc.RootElement);
    }

    public async Task<GmailFullMessage> GetFullAsync(string accessToken, string id, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(accessToken, $"{GmailBase}/messages/{Uri.EscapeDataString(id)}?format=full", ct);
        var root = doc.RootElement;
        var plain = new StringBuilder();
        var html = new StringBuilder();
        var attachments = new List<GmailAttachmentRef>();

        if (root.TryGetProperty("payload", out var payload))
        {
            Walk(payload, plain, html, attachments);
        }

        var text = plain.Length > 0 ? plain.ToString() : HtmlToText(html.ToString());
        return new GmailFullMessage(ParseMeta(root), text, attachments);
    }

    public async Task<byte[]> GetAttachmentAsync(string accessToken, string messageId, string attachmentId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(accessToken, $"{GmailBase}/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}", ct);
        return Pkce.FromBase64Url(doc.RootElement.GetProperty("data").GetString()!);
    }

    public async Task<GmailHistoryPage> ListHistoryAsync(string accessToken, string startHistoryId, string? pageToken, CancellationToken ct)
    {
        var url = $"{GmailBase}/history?startHistoryId={Uri.EscapeDataString(startHistoryId)}&historyTypes=messageAdded&maxResults=500"
            + (pageToken != null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");

        try
        {
            using var doc = await GetJsonAsync(accessToken, url, ct);
            var root = doc.RootElement;
            var ids = new List<string>();

            if (root.TryGetProperty("history", out var history))
            {
                foreach (var h in history.EnumerateArray())
                {
                    if (h.TryGetProperty("messagesAdded", out var added))
                    {
                        ids.AddRange(added.EnumerateArray().Select(a => a.GetProperty("message").GetProperty("id").GetString()!));
                    }
                }
            }

            return new GmailHistoryPage(ids.Distinct().ToList(), root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null,
                root.GetProperty("historyId").GetString()!);
        }
        catch (GmailApiException ex) when (ex.Status == 404)
        {
            throw new GmailHistoryExpiredException();
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string accessToken, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            return JsonDocument.Parse(body);
        }

        var status = (int)response.StatusCode;
        var reason = ReadGoogleReason(body) ?? response.StatusCode.ToString();

        if (status == 401)
        {
            throw new GmailApiException(401, "unauthorized", false);
        }

        // Gmail reports rate limits as 429, or as 403 with a rate-limit reason
        var retryable = status == 429 || status >= 500 || (status == 403 && reason.Contains("RateLimit", StringComparison.OrdinalIgnoreCase));
        throw new GmailApiException(status, reason, retryable);
    }

    private static string? ReadGoogleReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                    && errors[0].TryGetProperty("reason", out var r))
                {
                    return r.GetString();
                }

                // Newer Google errors carry the reason in details[] (ErrorInfo), e.g. SERVICE_DISABLED
                if (err.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in details.EnumerateArray())
                    {
                        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("reason", out var dr) && dr.GetString() is { Length: > 0 } reason)
                        {
                            return reason;
                        }
                    }
                }

                return err.TryGetProperty("status", out var s) ? s.GetString() : null;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? TryReadString(string body, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GmailMessageMeta ParseMeta(JsonElement root)
    {
        string Header(string name)
        {
            if (root.TryGetProperty("payload", out var p) && p.TryGetProperty("headers", out var headers))
            {
                foreach (var h in headers.EnumerateArray())
                {
                    if (string.Equals(h.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        return h.GetProperty("value").GetString() ?? "";
                    }
                }
            }

            return "";
        }

        var received = root.TryGetProperty("internalDate", out var d) && long.TryParse(d.GetString(), out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : DateTime.UtcNow;

        return new GmailMessageMeta(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("threadId", out var t) ? t.GetString() ?? "" : "",
            Header("From"),
            Header("Subject"),
            received,
            root.TryGetProperty("snippet", out var s) ? WebUtility.HtmlDecode(s.GetString() ?? "") : "",
            root.TryGetProperty("historyId", out var hid) ? hid.GetString() : null);
    }

    private static void Walk(JsonElement part, StringBuilder plain, StringBuilder html, List<GmailAttachmentRef> attachments)
    {
        var mime = part.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
        var fileName = part.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : "";
        var body = part.TryGetProperty("body", out var b) ? b : default;

        if (fileName.Length > 0 && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("attachmentId", out var att))
        {
            attachments.Add(new GmailAttachmentRef(fileName, mime, body.TryGetProperty("size", out var size) ? size.GetInt64() : 0, att.GetString()!));
        }
        else if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } encoded)
        {
            var text = Encoding.UTF8.GetString(Pkce.FromBase64Url(encoded));

            if (mime.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                plain.AppendLine(text);
            }
            else if (mime.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            {
                html.AppendLine(text);
            }
        }

        if (part.TryGetProperty("parts", out var parts))
        {
            foreach (var child in parts.EnumerateArray())
            {
                Walk(child, plain, html, attachments);
            }
        }
    }

    private static readonly Regex DropBlocks = new(@"(?is)<(script|style|head)[^>]*>.*?</\1>", RegexOptions.Compiled);
    private static readonly Regex LineBreaks = new(@"(?i)<\s*(br|/p|/div|/tr|/li|/h\d)\s*/?>", RegexOptions.Compiled);
    private static readonly Regex Cells = new(@"(?i)</t[dh]\s*>", RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>Plain text from an HTML email: scripts and styles dropped, rows and paragraphs kept as lines.</summary>
    public static string HtmlToText(string html)
    {
        var t = DropBlocks.Replace(html, " ");
        t = LineBreaks.Replace(t, "\n");
        t = Cells.Replace(t, " ");
        t = WebUtility.HtmlDecode(Tags.Replace(t, " "));
        return string.Join("\n", t.Split('\n').Select(l => Regex.Replace(l, @"[ \t ]+", " ").Trim()).Where(l => l.Length > 0));
    }
}

/// <summary>Retries a Gmail call on rate limits and server errors with exponential backoff.</summary>
public static class Retry
{
    public static async Task<T> GmailAsync<T>(Func<Task<T>> call, CancellationToken ct, int attempts = 4, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (GmailApiException ex) when (ex.Retryable && attempt < attempts)
            {
                await delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250)), ct);
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                await delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    /// <summary>How long a connection waits after repeated failed syncs: 15 minutes doubling, at most 6 hours.</summary>
    public static TimeSpan SyncBackoff(int failures) => TimeSpan.FromMinutes(Math.Min(360, 15 * Math.Pow(2, Math.Max(0, failures - 1))));
}
