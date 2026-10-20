using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

/// <summary>The store contract in memory, with the same user scoping as the Postgres store.</summary>
public sealed class InMemoryCompanionStore : ICompanionStore
{
    private readonly object _gate = new();
    /// <summary>Keyed like the watch rows: the user id for a main account, "user#account" for a further one.</summary>
    public readonly Dictionary<string, GmailConnection> Connections = new();
    public readonly Dictionary<(string, ProfileImageKind), ProfileImage> ProfileImages = new();
    public readonly Dictionary<string, OAuthState> States = new();
    public readonly Dictionary<(string, string), ProcessedMessage> Messages = new();
    public readonly Dictionary<string, FinancialItem> Items = new();
    public readonly Dictionary<string, DebtRecord> Debts = new();
    public readonly Dictionary<string, GoalRecord> Goals = new();
    public readonly Dictionary<string, Contribution> Contributions = new();
    public readonly Dictionary<string, CompanionSettings> Settings = new();
    public readonly List<(string UserId, AuditEntry Entry)> Audit = new();
    public readonly Dictionary<string, GmailWatch> Watches = new();

    private static string K(MailboxRef m) => Mailboxes.Key(m.UserId, m.AccountId);

    public Task<GmailWatch?> GetWatchAsync(MailboxRef m, CancellationToken ct) { lock (_gate) return Task.FromResult(Watches.GetValueOrDefault(K(m))); }

    public Task SaveWatchAsync(GmailWatch w, CancellationToken ct) { lock (_gate) Watches[Mailboxes.Key(w.UserId, w.AccountId)] = w; return Task.CompletedTask; }

    public Task<IReadOnlyList<MailboxRef>> MailboxesForGmailAsync(string email, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<MailboxRef>>(Connections.Values
                .Where(c => string.Equals(c.GoogleEmail, email, StringComparison.OrdinalIgnoreCase) && c.RefreshTokenEnc != null && c.Status != ConnectionStatus.NeedsReconnect)
                .Select(c => c.Ref()).ToList());
        }
    }

    public Task MarkSyncDueAsync(MailboxRef m, DateTime now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Connections.TryGetValue(K(m), out var c) && c.NextAttemptUtc > now) Connections[K(m)] = c with { NextAttemptUtc = now };
        }

        return Task.CompletedTask;
    }

    public Task<PushOverview> PushOverviewAsync(DateTime now, CancellationToken ct)
    {
        lock (_gate)
        {
            var w = Watches.Values.ToList();
            return Task.FromResult(new PushOverview(w.Count(x => x.ExpiresUtc > now && x.LastError == null), w.Count(x => x.LastError != null),
                w.Max(x => x.LastPushUtc), w.Sum(x => x.PushCount), w.Sum(x => x.DuplicatePushCount)));
        }
    }

    public Task<IReadOnlyList<GmailConnection>> GetConnectionsAsync(string userId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<GmailConnection>>(Connections.Values.Where(c => c.UserId == userId)
                .OrderBy(c => !Mailboxes.IsMain(c.AccountId)).ThenBy(c => c.ConnectedUtc).ThenBy(c => c.AccountId, StringComparer.Ordinal).ToList());
        }
    }

    public Task<GmailConnection?> GetConnectionAsync(string userId, string accountId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(Connections.GetValueOrDefault(Mailboxes.Key(userId, accountId)) is { } c && c.UserId == userId ? c : null);
    }

    public Task SaveConnectionAsync(GmailConnection c, CancellationToken ct)
    {
        lock (_gate)
        {
            // Like the database's unique (user, address) index: one person can't hold the same mailbox twice
            if (Connections.Values.Any(o => o.UserId == c.UserId && o.AccountId != c.AccountId && c.GoogleEmail != null
                    && string.Equals(o.GoogleEmail, c.GoogleEmail, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("duplicate mailbox for this user");
            }

            // Like the SQL upsert, saving never changes who holds the sync lock
            var key = Mailboxes.Key(c.UserId, c.AccountId);
            var lockUntil = Connections.GetValueOrDefault(key)?.SyncLockUntilUtc;
            Connections[key] = c with { SyncLockUntilUtc = lockUntil };
        }

        return Task.CompletedTask;
    }

    public Task DeleteConnectionAsync(string userId, string accountId, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = Mailboxes.Key(userId, accountId);
            Connections.Remove(key);
            Watches.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MailboxRef>> DueConnectionsAsync(DateTime now, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<MailboxRef>>(Connections.Values
                .Where(c => c.Status is ConnectionStatus.Connected or ConnectionStatus.Error or ConnectionStatus.Syncing && c.RefreshTokenEnc != null && c.NextAttemptUtc <= now
                    && (c.SyncLockUntilUtc == null || c.SyncLockUntilUtc < now))
                .OrderBy(c => c.NextAttemptUtc).Take(max).Select(c => c.Ref()).ToList());
        }
    }

    public Task<bool> TryClaimSyncAsync(MailboxRef m, DateTime now, DateTime until, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!Connections.TryGetValue(K(m), out var c) || c.SyncLockUntilUtc is { } l && l >= now) return Task.FromResult(false);
            Connections[K(m)] = c with { SyncLockUntilUtc = until };
            return Task.FromResult(true);
        }
    }

    public Task ReleaseSyncAsync(MailboxRef m, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Connections.TryGetValue(K(m), out var c)) Connections[K(m)] = c with { SyncLockUntilUtc = null };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProfileImage>> GetProfileImagesAsync(string userId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ProfileImage>>(ProfileImages.Values.Where(i => i.UserId == userId).ToList());
    }

    public Task<ProfileImage?> GetProfileImageAsync(string userId, ProfileImageKind kind, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(ProfileImages.GetValueOrDefault((userId, kind)));
    }

    public Task SaveProfileImageAsync(ProfileImage i, CancellationToken ct) { lock (_gate) ProfileImages[(i.UserId, i.Kind)] = i; return Task.CompletedTask; }

    public Task<bool> DeleteProfileImageAsync(string userId, ProfileImageKind kind, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(ProfileImages.Remove((userId, kind)));
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
            foreach (var id in Debts.Values.Where(d => d.UserId == userId).Select(d => d.Id).ToList()) Debts.Remove(id);
            foreach (var id in Goals.Values.Where(g => g.UserId == userId).Select(g => g.Id).ToList()) Goals.Remove(id);
            foreach (var id in Contributions.Values.Where(c => c.UserId == userId).Select(c => c.Id).ToList()) Contributions.Remove(id);
            Settings.Remove(userId);
            foreach (var k in Connections.Where(c => c.Value.UserId == userId).Select(c => c.Key).ToList()) Connections.Remove(k);
            foreach (var k in Watches.Where(w => w.Value.UserId == userId).Select(w => w.Key).ToList()) Watches.Remove(k);
            foreach (var k in ProfileImages.Keys.Where(k => k.Item1 == userId).ToList()) ProfileImages.Remove(k);
            Forwards.Remove(userId);
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

    public readonly Dictionary<string, ForwardAddress> Forwards = new();

    public Task<bool> TryClaimMessageAsync(ProcessedMessage m, CancellationToken ct) { lock (_gate) return Task.FromResult(Messages.TryAdd((m.UserId, m.GmailId), m)); }

    public Task<ForwardAddress?> GetForwardAsync(string userId, CancellationToken ct) { lock (_gate) return Task.FromResult(Forwards.GetValueOrDefault(userId)); }

    public Task<ForwardAddress?> FindForwardAsync(string token, CancellationToken ct) { lock (_gate) return Task.FromResult(Forwards.Values.FirstOrDefault(f => f.Token == token)); }

    public Task<ForwardAddress> GetOrCreateForwardAsync(ForwardAddress candidate, CancellationToken ct)
    {
        lock (_gate)
        {
            Forwards.TryAdd(candidate.UserId, candidate);
            return Task.FromResult(Forwards[candidate.UserId]);
        }
    }

    public Task SaveForwardAsync(ForwardAddress a, CancellationToken ct) { lock (_gate) Forwards[a.UserId] = a; return Task.CompletedTask; }

    // Like the database: an id clash never overwrites another person's row
    public Task<IReadOnlyList<DebtRecord>> GetDebtsAsync(string userId, CancellationToken ct) { lock (_gate) return Task.FromResult<IReadOnlyList<DebtRecord>>(Debts.Values.Where(d => d.UserId == userId).ToList()); }

    public Task SaveDebtAsync(DebtRecord d, CancellationToken ct) { lock (_gate) if (!Debts.TryGetValue(d.Id, out var e) || e.UserId == d.UserId) Debts[d.Id] = d; return Task.CompletedTask; }

    public Task<bool> DeleteDebtAsync(string userId, string id, CancellationToken ct) { lock (_gate) return Task.FromResult(Debts.TryGetValue(id, out var d) && d.UserId == userId && Debts.Remove(id)); }

    public Task<IReadOnlyList<GoalRecord>> GetGoalsAsync(string userId, CancellationToken ct) { lock (_gate) return Task.FromResult<IReadOnlyList<GoalRecord>>(Goals.Values.Where(g => g.UserId == userId).ToList()); }

    public Task SaveGoalAsync(GoalRecord g, CancellationToken ct) { lock (_gate) if (!Goals.TryGetValue(g.Id, out var e) || e.UserId == g.UserId) Goals[g.Id] = g; return Task.CompletedTask; }

    public Task<bool> DeleteGoalAsync(string userId, string id, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var c in Contributions.Values.Where(c => c.UserId == userId && c.GoalId == id).Select(c => c.Id).ToList()) Contributions.Remove(c);
            return Task.FromResult(Goals.TryGetValue(id, out var g) && g.UserId == userId && Goals.Remove(id));
        }
    }

    public Task<IReadOnlyList<Contribution>> GetContributionsAsync(string userId, CancellationToken ct) { lock (_gate) return Task.FromResult<IReadOnlyList<Contribution>>(Contributions.Values.Where(c => c.UserId == userId).ToList()); }

    public Task SaveContributionAsync(Contribution c, CancellationToken ct) { lock (_gate) if (!Contributions.TryGetValue(c.Id, out var e) || e.UserId == c.UserId) Contributions[c.Id] = c; return Task.CompletedTask; }

    public Task<bool> DeleteContributionAsync(string userId, string id, CancellationToken ct) { lock (_gate) return Task.FromResult(Contributions.TryGetValue(id, out var c) && c.UserId == userId && Contributions.Remove(id)); }
}

public sealed record FakeMail(string Id, string From, string Subject, string Body, DateTime Received, long HistoryId, byte[]? Pdf = null, string? Html = null,
    IReadOnlyList<FakeFile>? Files = null)
{
    /// <summary>The attachments: Files, or the single "document.pdf" the older tests pass as Pdf.</summary>
    public IReadOnlyList<FakeFile> AllFiles => Files ?? (Pdf != null ? [new FakeFile("document.pdf", "application/pdf", Pdf)] : []);
}

/// <summary>An attachment. CarriedInMessage puts the bytes in the message (as Gmail does for small
/// files) instead of behind an attachment id; Inline marks a picture in the email's layout.</summary>
public sealed record FakeFile(string Name, string MimeType, byte[] Data, bool CarriedInMessage = false, bool Inline = false, long? ReportedSize = null);

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

    /// <summary>Errors returned for attachment downloads only, one per request.</summary>
    public readonly Queue<HttpStatusCode> AttachmentFailures = new();
    public int AttachmentDownloads;

    /// <summary>A raw error Google returns for the profile call, e.g. the Gmail API being disabled.</summary>
    public (int Status, string Body)? ProfileError;

    /// <summary>The mailbox address the profile reports, so tests can connect different mailboxes.</summary>
    public string EmailAddress = "test.user@example.com";
    public readonly List<string> WatchTopics = [];
    public int StopCalls;
    public (int Status, string Reason)? WatchError;
    public DateTime WatchExpires = DateTime.UtcNow.AddDays(7);
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

    public void Add(string id, string from, string subject, string body, DateTime received, byte[]? pdf = null, string? html = null, IReadOnlyList<FakeFile>? files = null) =>
        Mailbox.Add(new FakeMail(id, from, subject, body, received, ++HistoryCounter, pdf, html, files));

    /// <summary>Further mailboxes by address. A token belongs to the account that was signed in
    /// (EmailAddress) when its code was exchanged; requests with it see that account's mail.</summary>
    public readonly Dictionary<string, List<FakeMail>> OtherMailboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tokenAccount = new();

    /// <summary>Accounts whose grant was revoked in Google: refreshing their token fails, others still work.</summary>
    public readonly HashSet<string> RevokedAccounts = new(StringComparer.OrdinalIgnoreCase);

    public void AddTo(string email, string id, string from, string subject, string body, DateTime received, byte[]? pdf = null) =>
        (OtherMailboxes.TryGetValue(email, out var box) ? box : OtherMailboxes[email] = []).Add(new FakeMail(id, from, subject, body, received, ++HistoryCounter, pdf));

    private string? AccountOf(string token) => _tokenAccount.GetValueOrDefault(token);

    private List<FakeMail> MailFor(string? account) => account != null && OtherMailboxes.TryGetValue(account, out var box) ? box : Mailbox;

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

                // The first account keeps the refresh token the older tests expect; each further one gets its own
                var refreshToken = IssueRefreshToken ? (OtherMailboxes.ContainsKey(EmailAddress) ? $"refresh-{EmailAddress}" : "refresh-1") : null;
                if (refreshToken != null) _tokenAccount[refreshToken] = EmailAddress;
                return Json(200, Tokens(refreshToken, EmailAddress));
            }

            RefreshCalls++;
            var owner = AccountOf(form["refresh_token"] ?? "");
            if (owner != null && RevokedAccounts.Contains(owner)) return Json(400, new { error = "invalid_grant" });
            return RefreshError != null ? Json(400, new { error = RefreshError }) : Json(200, Tokens(null, owner ?? EmailAddress));
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
        var account = AccountOf(bearer);
        var mailbox = MailFor(account);

        if (path == "/profile" && ProfileError is { } pe)
        {
            return new HttpResponseMessage((HttpStatusCode)pe.Status) { Content = new StringContent(pe.Body, System.Text.Encoding.UTF8, "application/json") };
        }

        if (path == "/profile") return Json(200, new { emailAddress = account != null && OtherMailboxes.ContainsKey(account) ? account : EmailAddress, historyId = HistoryCounter.ToString() });

        if (path == "/watch" && request.Method == HttpMethod.Post)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            WatchTopics.Add(body.RootElement.GetProperty("topicName").GetString()!);
            if (WatchError is { } we) return Json(we.Status, new { error = new { code = we.Status, errors = new[] { new { reason = we.Reason } } } });
            return Json(200, new { historyId = HistoryCounter.ToString(), expiration = new DateTimeOffset(WatchExpires).ToUnixTimeMilliseconds().ToString() });
        }

        if (path == "/stop" && request.Method == HttpMethod.Post)
        {
            StopCalls++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (path == "/messages")
        {
            var days = int.Parse(query["q"]!.Split(' ')[0].Replace("newer_than:", "").TrimEnd('d'));
            var ids = mailbox.Where(m => m.Received >= DateTime.UtcNow.AddDays(-days)).Select(m => new { id = m.Id, threadId = "t" + m.Id }).ToList();
            return Json(200, new { messages = ids, resultSizeEstimate = ids.Count });
        }

        if (path == "/history")
        {
            if (HistoryExpired) return Json(404, new { error = new { code = 404, status = "NOT_FOUND" } });
            var start = long.Parse(query["startHistoryId"]!);
            var added = mailbox.Where(m => m.HistoryId > start).Select(m => new { messagesAdded = new[] { new { message = new { id = m.Id } } } }).ToList();
            return Json(200, new { history = added, historyId = HistoryCounter.ToString() });
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts is ["messages", var id, "attachments", var attId])
        {
            AttachmentDownloads++;

            if (AttachmentFailures.TryDequeue(out var attFailure))
            {
                return Json((int)attFailure, new { error = new { code = (int)attFailure, errors = new[] { new { reason = "backendError" } } } });
            }

            var file = mailbox.First(m => m.Id == id).AllFiles[int.Parse(attId.Replace("att-", ""))];
            return Json(200, new { data = Pkce.Base64Url(file.Data), size = file.Data.Length });
        }

        if (parts is ["messages", var mid])
        {
            var mail = mailbox.FirstOrDefault(m => m.Id == mid);
            if (mail == null) return Json(404, new { error = new { code = 404, status = "NOT_FOUND" } });

            var headers = new[]
            {
                new { name = "From", value = mail.From }, new { name = "Subject", value = mail.Subject }, new { name = "Date", value = mail.Received.ToString("R") },
                new { name = "Content-Type", value = mail.AllFiles.Count > 0 ? "multipart/mixed; boundary=\"b1\"" : "multipart/alternative; boundary=\"b0\"" },
            };
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

            for (var i = 0; i < mail.AllFiles.Count; i++)
            {
                var f = mail.AllFiles[i];
                var partHeaders = new[] { new { name = "Content-Disposition", value = f.Inline ? $"inline; filename=\"{f.Name}\"" : $"attachment; filename=\"{f.Name}\"" } };
                partsList.Add(f.CarriedInMessage
                    ? new { mimeType = f.MimeType, filename = f.Name, headers = partHeaders, body = (object)new { data = Pkce.Base64Url(f.Data), size = f.ReportedSize ?? f.Data.Length } }
                    : new { mimeType = f.MimeType, filename = f.Name, headers = partHeaders, body = (object)new { attachmentId = $"att-{i}", size = f.ReportedSize ?? f.Data.Length } });
            }

            return Json(200, new { meta.id, meta.threadId, meta.snippet, meta.internalDate, meta.historyId, payload = new { mimeType = "multipart/mixed", headers, parts = partsList } });
        }

        return Json(404, new { error = new { code = 404, status = "NOT_FOUND" } });
    }

    private object Tokens(string? refresh, string account)
    {
        CurrentAccessToken = $"access-{++AccessCounter}";
        ValidAccessTokens.Add(CurrentAccessToken);
        _tokenAccount[CurrentAccessToken] = account;
        return refresh == null
            ? new { access_token = CurrentAccessToken, expires_in = 3599, scope = GrantedScope, token_type = "Bearer" }
            : (object)new { access_token = CurrentAccessToken, expires_in = 3599, refresh_token = refresh, scope = GrantedScope, token_type = "Bearer" };
    }

    private static HttpResponseMessage Json(int status, object body) =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

/// <summary>OCR that "reads" a scan by returning prepared text, or fails.</summary>
public sealed class FakeOcr(string text, bool fail = false) : IDocumentOcr
{
    public int Calls;
    public string Name => "fake-ocr";

    public Task<string> ReadAsync(byte[] data, string mimeType, CancellationToken ct)
    {
        Calls++;
        if (fail) throw new InvalidOperationException("ocr down");
        return Task.FromResult(text);
    }
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

    public string? PushTopic;

    /// <summary>Null: no plan check at all (most tests). Otherwise who has Premium.</summary>
    public HashSet<string>? PremiumUsers;

    public CompanionRig(FakeAi? ai = null, SyncOptions? options = null, IDocumentOcr? ocr = null, IAttachmentReader? reader = null)
    {
        Ai = ai;
        Client = new GoogleClient(new HttpClient(Google));
        Engine = new SyncEngine(Store, Client, Client, () => Config, () => Keys, reader ?? new PdfAttachmentReader(), ai, options ?? new SyncOptions(),
            () => Now, Logs.Add, (_, _) => Task.CompletedTask, () => PushTopic, ocr,
            (userId, _) => Task.FromResult(PremiumUsers?.Contains(userId) ?? true));
        Service = new CompanionService(Store, Engine, () => Now);
    }

    /// <summary>Runs the real consent flow against the fake Google and returns the user's connection.</summary>
    public async Task<GmailConnection> ConnectAsync(string userId)
    {
        var url = await Engine.BeginConnectAsync(userId, default);
        var state = HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;
        var result = await Engine.CompleteConnectAsync(state, "good-code", null, default);
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        return Store.Connections[Mailboxes.Key(userId, result.AccountId!)];
    }
}
