using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

/// <summary>The store contract in memory, with the same user scoping as the Postgres store.</summary>
public sealed class InMemoryCompanionStore : ICompanionStore
{
    private readonly object _gate = new();
    public readonly Dictionary<string, GmailConnection> Connections = new();
    public readonly Dictionary<string, OAuthState> States = new();
    public readonly Dictionary<(string, string), ProcessedMessage> Messages = new();
    public readonly Dictionary<string, FinancialItem> Items = new();
    public readonly Dictionary<string, CompanionSettings> Settings = new();
    public readonly List<(string UserId, AuditEntry Entry)> Audit = new();

    public Task<GmailConnection?> GetConnectionAsync(string userId, CancellationToken ct) { lock (_gate) return Task.FromResult(Connections.GetValueOrDefault(userId)); }

    public Task SaveConnectionAsync(GmailConnection c, CancellationToken ct)
    {
        lock (_gate)
        {
            // Like the SQL upsert, saving never changes who holds the sync lock
            var lockUntil = Connections.GetValueOrDefault(c.UserId)?.SyncLockUntilUtc;
            Connections[c.UserId] = c with { SyncLockUntilUtc = lockUntil };
        }

        return Task.CompletedTask;
    }

    public Task DeleteConnectionAsync(string userId, CancellationToken ct) { lock (_gate) Connections.Remove(userId); return Task.CompletedTask; }

    public Task<IReadOnlyList<string>> DueConnectionsAsync(DateTime now, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(Connections.Values
                .Where(c => c.Status is ConnectionStatus.Connected or ConnectionStatus.Error or ConnectionStatus.Syncing && c.RefreshTokenEnc != null && c.NextAttemptUtc <= now
                    && (c.SyncLockUntilUtc == null || c.SyncLockUntilUtc < now))
                .OrderBy(c => c.NextAttemptUtc).Take(max).Select(c => c.UserId).ToList());
        }
    }

    public Task<bool> TryClaimSyncAsync(string userId, DateTime now, DateTime until, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!Connections.TryGetValue(userId, out var c) || c.SyncLockUntilUtc is { } l && l >= now) return Task.FromResult(false);
            Connections[userId] = c with { SyncLockUntilUtc = until };
            return Task.FromResult(true);
        }
    }

    public Task ReleaseSyncAsync(string userId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Connections.TryGetValue(userId, out var c)) Connections[userId] = c with { SyncLockUntilUtc = null };
        }

        return Task.CompletedTask;
    }

    public Task SaveStateAsync(OAuthState s, CancellationToken ct) { lock (_gate) States[s.State] = s; return Task.CompletedTask; }

    public Task<OAuthState?> TakeStateAsync(string state, DateTime now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!States.Remove(state, out var s)) return Task.FromResult<OAuthState?>(null);
            return Task.FromResult(s.ExpiresUtc < now ? null : s);
        }
    }

    public Task<ProcessedMessage?> GetMessageAsync(string userId, string gmailId, CancellationToken ct) { lock (_gate) return Task.FromResult(Messages.GetValueOrDefault((userId, gmailId))); }

    public Task SaveMessageAsync(ProcessedMessage m, CancellationToken ct) { lock (_gate) Messages[(m.UserId, m.GmailId)] = m; return Task.CompletedTask; }

    public Task<IReadOnlyList<FinancialItem>> GetItemsAsync(string userId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<FinancialItem>>(Items.Values.Where(i => i.UserId == userId).OrderByDescending(i => i.UpdatedUtc).ToList());
    }

    public Task<FinancialItem?> GetItemAsync(string userId, string itemId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(Items.TryGetValue(itemId, out var i) && i.UserId == userId ? i : null);
    }

    public Task SaveItemAsync(FinancialItem item, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Items.TryGetValue(item.Id, out var existing) && existing.UserId != item.UserId) return Task.CompletedTask;

            // Round-trip through JSON like the database does, so serialisation is tested too
            Items[item.Id] = JsonSerializer.Deserialize<FinancialItem>(JsonSerializer.Serialize(item, CompanionJson.Options), CompanionJson.Options)!;
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteItemAsync(string userId, string itemId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(Items.TryGetValue(itemId, out var i) && i.UserId == userId && Items.Remove(itemId));
    }

    public Task<CompanionSettings> GetSettingsAsync(string userId, CancellationToken ct) { lock (_gate) return Task.FromResult(Settings.GetValueOrDefault(userId) ?? new CompanionSettings()); }

    public Task SaveSettingsAsync(string userId, CompanionSettings s, CancellationToken ct) { lock (_gate) Settings[userId] = s; return Task.CompletedTask; }

    public Task AddAuditAsync(string userId, string action, string? itemId, CancellationToken ct) { lock (_gate) Audit.Add((userId, new AuditEntry(DateTime.UtcNow, action, itemId))); return Task.CompletedTask; }

    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string userId, int max, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<AuditEntry>>(Audit.Where(a => a.UserId == userId).Select(a => a.Entry).Take(max).ToList());
    }

    public Task DeleteAllForUserAsync(string userId, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var id in Items.Values.Where(i => i.UserId == userId).Select(i => i.Id).ToList()) Items.Remove(id);
            foreach (var k in Messages.Keys.Where(k => k.Item1 == userId).ToList()) Messages.Remove(k);
            foreach (var s in States.Values.Where(s => s.UserId == userId).Select(s => s.State).ToList()) States.Remove(s);
            Settings.Remove(userId);
            Connections.Remove(userId);
            Audit.RemoveAll(a => a.UserId == userId);
        }

        return Task.CompletedTask;
    }

    public Task PruneAsync(DateTime now, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var s in States.Values.Where(s => s.ExpiresUtc < now).Select(s => s.State).ToList()) States.Remove(s);
        }

        return Task.CompletedTask;
    }
}

public sealed record FakeMail(string Id, string From, string Subject, string Body, DateTime Received, long HistoryId, byte[]? Pdf = null, string? Html = null);

/// <summary>A stand-in for Google's token, revoke and Gmail endpoints, answering like the real ones.</summary>
public sealed class FakeGoogle : HttpMessageHandler
{
    public readonly List<FakeMail> Mailbox = [];
    public long HistoryCounter = 1000;
    public string GrantedScope = GoogleScopes.GmailReadOnly;
    public bool IssueRefreshToken = true;
    public bool HistoryExpired;
    public string? RefreshError;
    public readonly Queue<HttpStatusCode> GmailFailures = new();

    /// <summary>A raw error Google returns for the profile call, e.g. the Gmail API being disabled.</summary>
    public (int Status, string Body)? ProfileError;
    public string CurrentAccessToken = "access-0";
    public readonly HashSet<string> ValidAccessTokens = [];

    /// <summary>Google stops accepting every access token issued so far (refresh tokens still work).</summary>
    public void InvalidateAccessTokens() => ValidAccessTokens.Clear();
    public int AccessCounter;
    public int FullFetches;
    public int MetadataFetches;
    public int RefreshCalls;
    public readonly List<string> Revoked = [];
    public readonly List<string> TokenRequests = [];

    public void Add(string id, string from, string subject, string body, DateTime received, byte[]? pdf = null, string? html = null) =>
        Mailbox.Add(new FakeMail(id, from, subject, body, received, ++HistoryCounter, pdf, html));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;

        if (url.AbsoluteUri.StartsWith(GoogleClient.TokenEndpoint))
        {
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            TokenRequests.Add(form["grant_type"]!);

            if (form["grant_type"] == "authorization_code")
            {
                if (form["code"] != "good-code" || string.IsNullOrEmpty(form["code_verifier"]))
                {
                    return Json(400, new { error = "invalid_grant" });
                }

                return Json(200, Tokens(IssueRefreshToken ? "refresh-1" : null));
            }

            RefreshCalls++;
            return RefreshError != null ? Json(400, new { error = RefreshError }) : Json(200, Tokens(null));
        }

        if (url.AbsoluteUri.StartsWith(GoogleClient.RevokeEndpoint))
        {
            Revoked.Add(HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct))["token"]!);
            return Json(200, new { });
        }

        if (request.Headers.Authorization?.Parameter is not { } bearer || !ValidAccessTokens.Contains(bearer))
        {
            return Json(401, new { error = new { code = 401, status = "UNAUTHENTICATED" } });
        }

        if (GmailFailures.TryDequeue(out var failure))
        {
            return Json((int)failure, new { error = new { code = (int)failure, errors = new[] { new { reason = failure == HttpStatusCode.TooManyRequests ? "rateLimitExceeded" : "backendError" } } } });
        }

        var path = url.AbsolutePath.Replace("/gmail/v1/users/me", "");
        var query = HttpUtility.ParseQueryString(url.Query);

        if (path == "/profile" && ProfileError is { } pe)
        {
            return new HttpResponseMessage((HttpStatusCode)pe.Status) { Content = new StringContent(pe.Body, System.Text.Encoding.UTF8, "application/json") };
        }

        if (path == "/profile") return Json(200, new { emailAddress = "test.user@example.com", historyId = HistoryCounter.ToString() });

        if (path == "/messages")
        {
            var days = int.Parse(query["q"]!.Split(' ')[0].Replace("newer_than:", "").TrimEnd('d'));
            var ids = Mailbox.Where(m => m.Received >= DateTime.UtcNow.AddDays(-days)).Select(m => new { id = m.Id, threadId = "t" + m.Id }).ToList();
            return Json(200, new { messages = ids, resultSizeEstimate = ids.Count });
        }

        if (path == "/history")
        {
            if (HistoryExpired) return Json(404, new { error = new { code = 404, status = "NOT_FOUND" } });
            var start = long.Parse(query["startHistoryId"]!);
            var added = Mailbox.Where(m => m.HistoryId > start).Select(m => new { messagesAdded = new[] { new { message = new { id = m.Id } } } }).ToList();
            return Json(200, new { history = added, historyId = HistoryCounter.ToString() });
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts is ["messages", var id, "attachments", _])
        {
            var mail = Mailbox.First(m => m.Id == id);
            return Json(200, new { data = Pkce.Base64Url(mail.Pdf!), size = mail.Pdf!.Length });
        }

        if (parts is ["messages", var mid])
        {
            var mail = Mailbox.FirstOrDefault(m => m.Id == mid);
            if (mail == null) return Json(404, new { error = new { code = 404, status = "NOT_FOUND" } });

            var headers = new[] { new { name = "From", value = mail.From }, new { name = "Subject", value = mail.Subject }, new { name = "Date", value = mail.Received.ToString("R") } };
            var meta = new { id = mail.Id, threadId = "t" + mail.Id, snippet = mail.Body[..Math.Min(120, mail.Body.Length)], internalDate = new DateTimeOffset(mail.Received).ToUnixTimeMilliseconds().ToString(), historyId = mail.HistoryId.ToString() };

            if (query["format"] == "metadata")
            {
                MetadataFetches++;
                return Json(200, new { meta.id, meta.threadId, meta.snippet, meta.internalDate, meta.historyId, payload = new { headers } });
            }

            FullFetches++;
            var partsList = new List<object>
            {
                mail.Html != null
                    ? new { mimeType = "text/html", filename = "", body = new { data = Pkce.Base64Url(Encoding.UTF8.GetBytes(mail.Html)), size = mail.Html.Length } }
                    : new { mimeType = "text/plain", filename = "", body = new { data = Pkce.Base64Url(Encoding.UTF8.GetBytes(mail.Body)), size = mail.Body.Length } },
            };

            if (mail.Pdf != null)
            {
                partsList.Add(new { mimeType = "application/pdf", filename = "document.pdf", body = new { attachmentId = "att-1", size = mail.Pdf.Length } });
            }

            return Json(200, new { meta.id, meta.threadId, meta.snippet, meta.internalDate, meta.historyId, payload = new { mimeType = "multipart/mixed", headers, parts = partsList } });
        }

        return Json(404, new { error = new { code = 404, status = "NOT_FOUND" } });
    }

    private object Tokens(string? refresh)
    {
        CurrentAccessToken = $"access-{++AccessCounter}";
        ValidAccessTokens.Add(CurrentAccessToken);
        return refresh == null
            ? new { access_token = CurrentAccessToken, expires_in = 3599, scope = GrantedScope, token_type = "Bearer" }
            : (object)new { access_token = CurrentAccessToken, expires_in = 3599, refresh_token = refresh, scope = GrantedScope, token_type = "Bearer" };
    }

    private static HttpResponseMessage Json(int status, object body) =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

public sealed class FakeAi(Func<string, AiRawAnalysis>? answer = null, bool fail = false) : IAiAnalyzer
{
    public int Calls;
    public string? LastInput;
    public string ModelName => "fake-model";

    public Task<AiRawAnalysis> AnalyzeAsync(string input, CancellationToken ct)
    {
        Calls++;
        LastInput = input;
        if (fail) throw new InvalidOperationException("provider down");
        return Task.FromResult(answer?.Invoke(input) ?? new AiRawAnalysis(true, "Invoice", "An electricity bill.", "It asks for a payment.", ["Amount due 64,20 €"], [], [], ["Pay by the due date."], "The amount and due date."));
    }
}

/// <summary>Builds a sync engine over the fakes, with a controllable clock and no real waiting.</summary>
public sealed class CompanionRig
{
    public readonly InMemoryCompanionStore Store = new();
    public readonly FakeGoogle Google = new();
    public readonly TokenProtector Keys = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    public readonly List<string> Logs = [];
    public DateTime Now = DateTime.UtcNow;
    public readonly SyncEngine Engine;
    public readonly CompanionService Service;
    public readonly GoogleClient Client;
    public FakeAi? Ai;

    public static readonly GoogleOAuthConfig Config = new("client-id.apps.googleusercontent.com", "client-secret", "https://omabilo.example/api/oauth/google/callback");

    public CompanionRig(FakeAi? ai = null, SyncOptions? options = null)
    {
        Ai = ai;
        Client = new GoogleClient(new HttpClient(Google));
        Engine = new SyncEngine(Store, Client, Client, () => Config, () => Keys, new PdfAttachmentReader(), ai, options ?? new SyncOptions(),
            () => Now, Logs.Add, (_, _) => Task.CompletedTask);
        Service = new CompanionService(Store, Engine, () => Now);
    }

    /// <summary>Runs the real consent flow against the fake Google and returns the user's connection.</summary>
    public async Task<GmailConnection> ConnectAsync(string userId)
    {
        var url = await Engine.BeginConnectAsync(userId, default);
        var state = HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;
        var result = await Engine.CompleteConnectAsync(state, "good-code", null, default);
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        return Store.Connections[userId];
    }
}
