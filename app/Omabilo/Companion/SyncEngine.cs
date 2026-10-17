// Connects a person's Gmail and keeps their financial records in step with it. Runs on the server:
// the IKON [Cron] sweep and the "Sync now" button both call SyncAsync, and a per-user lock in the
// database keeps two runs from overlapping. Logs carry ids and error codes, never message content.

public interface IAttachmentReader
{
    /// <summary>Readable text, or a plain reason it couldn't be read. Never throws for bad files.</summary>
    Task<(string? Text, string? Problem)> ReadAsync(string fileName, string mimeType, byte[] data, CancellationToken ct);
}

public sealed record SyncOptions
{
    public int MaxInitialMessages { get; init; } = 300;
    public int MaxIncrementalMessages { get; init; } = 500;
    public long MaxAttachmentBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxAiCallsPerSync { get; init; } = 25;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record ConnectResult(bool Ok, string? UserId, string Message, string? Code = null);

public sealed record SyncResult(bool Ok, int Scanned, int NewItems, int Updated, string Message, string? Code = null);

public sealed class SyncEngine(
    ICompanionStore store,
    IGoogleOAuth oauth,
    IGmailApi gmail,
    Func<GoogleOAuthConfig?> config,
    Func<TokenProtector?> protector,
    IAttachmentReader attachments,
    IAiAnalyzer? ai,
    SyncOptions options,
    Func<DateTime> clock,
    Action<string> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>What still has to be set up before Gmail can be connected, or empty when ready.</summary>
    public IReadOnlyList<string> MissingConfiguration()
    {
        var missing = new List<string>();
        if (config() == null) missing.Add("Google OAuth client (secrets GOOGLE_OAUTH_CLIENT_ID and GOOGLE_OAUTH_CLIENT_SECRET)");
        if (protector() == null) missing.Add("Token encryption key (secret OMABILO_TOKEN_KEY: base64 of 32 random bytes)");
        return missing;
    }

    // ── Connecting ──────────────────────────────────────────────────────────

    /// <summary>Starts the Google consent flow for a signed-in user: a one-time state and PKCE pair, stored for 10 minutes.</summary>
    public async Task<string> BeginConnectAsync(string userId, CancellationToken ct)
    {
        var cfg = config() ?? throw new InvalidOperationException("Google OAuth is not configured");
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewState();

        await store.SaveStateAsync(new OAuthState(state, userId, verifier, cfg.RedirectUri, clock().Add(StateLifetime)), ct);
        await store.AddAuditAsync(userId, "gmail_connect_started", null, ct);
        return oauth.BuildAuthorizationUrl(cfg, state, Pkce.Challenge(verifier));
    }

    /// <summary>Handles Google's redirect. The state decides whose connection this is; nothing in the
    /// request itself can name a user.</summary>
    public async Task<ConnectResult> CompleteConnectAsync(string? state, string? code, string? error, CancellationToken ct)
    {
        var cfg = config();
        var keys = protector();

        if (cfg == null || keys == null)
        {
            return new(false, null, "Gmail connection isn't set up on this server yet.", "not_configured");
        }

        if (string.IsNullOrEmpty(state) || await store.TakeStateAsync(state, clock(), ct) is not { } saved)
        {
            return new(false, null, "This connection link has expired or was already used. Start again from Omabilo.", "invalid_state");
        }

        if (!string.IsNullOrEmpty(error))
        {
            await store.AddAuditAsync(saved.UserId, "gmail_connect_denied", null, ct);
            return new(false, saved.UserId, error == "access_denied"
                ? "You didn't give Omabilo access to Gmail, so nothing was connected."
                : "Google didn't complete the connection. Please try again.", error);
        }

        if (string.IsNullOrEmpty(code))
        {
            return new(false, saved.UserId, "Google didn't return an authorization code. Please try again.", "no_code");
        }

        GoogleTokens tokens;

        try
        {
            tokens = await oauth.ExchangeCodeAsync(cfg with { RedirectUri = saved.RedirectUri }, code, saved.CodeVerifier, ct);
        }
        catch (GoogleAuthException ex)
        {
            log($"gmail connect: token exchange refused ({ex.Code}) for user {saved.UserId}");
            return new(false, saved.UserId, "Google refused the connection. Please try again.", ex.Code);
        }

        if (!tokens.Scope.Split(' ').Contains(GoogleScopes.GmailReadOnly))
        {
            await TryRevokeAsync(tokens.AccessToken, ct);
            return new(false, saved.UserId, "Omabilo needs permission to read your Gmail. Please tick the Gmail box on Google's screen and try again.", "scope_missing");
        }

        if (tokens.RefreshToken == null)
        {
            return new(false, saved.UserId, "Google didn't allow ongoing access. Remove Omabilo in your Google account's third-party access, then connect again.", "no_refresh_token");
        }

        var profile = await Retry.GmailAsync(() => gmail.GetProfileAsync(tokens.AccessToken, ct), ct, delay: delay);
        var now = clock();

        await store.SaveConnectionAsync(new GmailConnection
        {
            UserId = saved.UserId,
            GoogleEmail = profile.EmailAddress,
            GrantedScopes = tokens.Scope,
            RefreshTokenEnc = keys.Protect(tokens.RefreshToken, saved.UserId),
            AccessTokenEnc = keys.Protect(tokens.AccessToken, saved.UserId),
            AccessExpiresUtc = tokens.ExpiresUtc,
            Status = ConnectionStatus.Connected,
            NextAttemptUtc = now,
            ConnectedUtc = now,
        }, ct);
        await store.AddAuditAsync(saved.UserId, "gmail_connected", null, ct);

        return new(true, saved.UserId, $"Gmail ({profile.EmailAddress}) is connected. Omabilo will now look for financial messages.");
    }

    /// <summary>Revokes Google's grant (best effort) and deletes the stored tokens. Records stay unless asked.</summary>
    public async Task DisconnectAsync(string userId, CancellationToken ct)
    {
        if (await store.GetConnectionAsync(userId, ct) is { } c && protector() is { } keys)
        {
            if (keys.Unprotect(c.RefreshTokenEnc, userId) is { } refresh)
            {
                await TryRevokeAsync(refresh, ct);
            }
        }

        await store.DeleteConnectionAsync(userId, ct);
        await store.AddAuditAsync(userId, "gmail_disconnected", null, ct);
    }

    private async Task TryRevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            await oauth.RevokeAsync(token, ct);
        }
        catch (Exception ex)
        {
            log($"gmail revoke failed: {ex.GetType().Name}");
        }
    }

    // ── Syncing ─────────────────────────────────────────────────────────────

    /// <summary>One cron tick: every connection that is due, one at a time.</summary>
    public async Task<int> SyncDueAsync(int max, CancellationToken ct)
    {
        var due = await store.DueConnectionsAsync(clock(), max, ct);

        foreach (var userId in due)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await SyncAsync(userId, ct);
            }
            catch (Exception ex)
            {
                log($"sync sweep: user {userId} failed with {ex.GetType().Name}");
            }
        }

        await store.PruneAsync(clock(), ct);
        return due.Count;
    }

    public async Task<SyncResult> SyncAsync(string userId, CancellationToken ct, bool fullRescan = false)
    {
        var now = clock();

        if (await store.GetConnectionAsync(userId, ct) is not { } c)
        {
            return new(false, 0, 0, 0, "Gmail isn't connected.", "not_connected");
        }

        if (c.Status == ConnectionStatus.NeedsReconnect)
        {
            return new(false, 0, 0, 0, "Gmail needs to be reconnected.", "needs_reconnect");
        }

        if (!await store.TryClaimSyncAsync(userId, now, now.Add(options.LockDuration), ct))
        {
            return new(false, 0, 0, 0, "A sync is already running.", "busy");
        }

        var scanned = 0;
        var created = 0;
        var updated = 0;
        Refreshed? refreshed = null;

        try
        {
            var (token, fresh) = await AccessTokenAsync(c, ct);
            refreshed = fresh;

            // Google can reject an access token before its stated expiry (revoked session, password
            // change): renew it once and carry on rather than failing the sync
            try
            {
                await Retry.GmailAsync(() => gmail.GetProfileAsync(token, ct), ct, delay: delay);
            }
            catch (GmailApiException ex) when (ex.Status == 401)
            {
                (token, fresh) = await AccessTokenAsync(c with { AccessExpiresUtc = null }, ct);
                refreshed = fresh;
            }
            var settings = await store.GetSettingsAsync(userId, ct);
            var budget = new AiBudget(settings.AiEnabled && ai != null ? options.MaxAiCallsPerSync : 0);
            string newHistory;
            var ids = new List<string>();
            var needScan = fullRescan || !c.InitialScanDone || c.HistoryId == null;

            if (!needScan)
            {
                try
                {
                    (ids, newHistory) = await HistorySinceAsync(token, c.HistoryId!, ct);
                }
                catch (GmailHistoryExpiredException)
                {
                    log($"sync: history expired for user {userId}, rescanning");
                    await store.AddAuditAsync(userId, "gmail_history_expired_rescan", null, ct);
                    needScan = true;
                    newHistory = "";
                }
            }
            else
            {
                newHistory = "";
            }

            if (needScan)
            {
                // Take the history id before listing, so nothing arriving during the scan is missed
                var profile = await Retry.GmailAsync(() => gmail.GetProfileAsync(token, ct), ct, delay: delay);
                newHistory = profile.HistoryId;
                var days = c.InitialScanDone && c.LastSuccessUtc is { } last && !fullRescan
                    ? Math.Clamp((int)Math.Ceiling((now - last).TotalDays) + 1, 1, settings.ScanDays)
                    : Math.Clamp(settings.ScanDays, 1, 365);
                ids = await ListRecentAsync(token, days, options.MaxInitialMessages, ct);
            }

            foreach (var id in ids.Take(options.MaxIncrementalMessages))
            {
                if (ct.IsCancellationRequested) break;

                if (await store.GetMessageAsync(userId, id, ct) != null)
                {
                    continue;
                }

                scanned++;
                var outcome = await ProcessMessageAsync(userId, token, id, settings, budget, ct);
                if (outcome == Outcome.Created) created++;
                if (outcome == Outcome.Updated) updated++;
            }

            var done = clock();
            await store.SaveConnectionAsync(c with
            {
                AccessTokenEnc = refreshed?.AccessEnc ?? c.AccessTokenEnc,
                AccessExpiresUtc = refreshed?.Expires ?? c.AccessExpiresUtc,
                HistoryId = newHistory.Length > 0 ? newHistory : c.HistoryId,
                Status = ConnectionStatus.Connected,
                LastSyncUtc = done,
                PreviousSuccessUtc = c.LastSuccessUtc,
                LastSuccessUtc = done,
                LastError = null,
                FailureCount = 0,
                NextAttemptUtc = done.Add(options.PollInterval),
                LastSyncNewItems = created,
                LastSyncScanned = scanned,
                InitialScanDone = true,
            }, ct);
            await store.AddAuditAsync(userId, "gmail_sync_ok", null, ct);

            return new(true, scanned, created, updated, created == 0 && updated == 0
                ? "Up to date. No new financial messages."
                : $"Found {created} new item{(created == 1 ? "" : "s")}{(updated > 0 ? $" and updated {updated}" : "")}.");
        }
        catch (GoogleAuthException ex)
        {
            log($"sync: user {userId} needs to reconnect ({ex.Code})");
            await store.SaveConnectionAsync(c with
            {
                Status = ConnectionStatus.NeedsReconnect,
                RefreshTokenEnc = null,
                AccessTokenEnc = null,
                LastSyncUtc = clock(),
                LastError = "Google access was revoked or expired",
            }, ct);
            await store.AddAuditAsync(userId, "gmail_needs_reconnect", null, ct);
            return new(false, scanned, created, updated, "Google no longer allows Omabilo to read this Gmail. Connect it again to continue.", "needs_reconnect");
        }
        catch (Exception ex) when (ex is GmailApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var failures = c.FailureCount + 1;
            var reason = ex is GmailApiException g ? $"Gmail error {g.Status} ({g.Reason})" : "Network problem reaching Gmail";
            log($"sync: user {userId} failed: {reason}");
            await store.SaveConnectionAsync(c with
            {
                AccessTokenEnc = refreshed?.AccessEnc ?? c.AccessTokenEnc,
                AccessExpiresUtc = refreshed?.Expires ?? c.AccessExpiresUtc,
                Status = ConnectionStatus.Error,
                LastSyncUtc = clock(),
                LastError = reason,
                FailureCount = failures,
                NextAttemptUtc = clock().Add(Retry.SyncBackoff(failures)),
            }, ct);
            await store.AddAuditAsync(userId, "gmail_sync_failed", null, ct);
            return new(false, scanned, created, updated, $"Sync didn't finish: {reason}. Omabilo will try again automatically.", "sync_failed");
        }
        finally
        {
            await store.ReleaseSyncAsync(userId, CancellationToken.None);
        }
    }

    private sealed record Refreshed(string AccessEnc, DateTime Expires);

    private async Task<(string Token, Refreshed? Refreshed)> AccessTokenAsync(GmailConnection c, CancellationToken ct)
    {
        var keys = protector() ?? throw new InvalidOperationException("Token encryption key is not configured");
        var cfg = config() ?? throw new InvalidOperationException("Google OAuth is not configured");

        if (c.AccessExpiresUtc is { } exp && exp > clock().AddMinutes(1) && keys.Unprotect(c.AccessTokenEnc, c.UserId) is { } access)
        {
            return (access, null);
        }

        var refresh = keys.Unprotect(c.RefreshTokenEnc, c.UserId) ?? throw new GoogleAuthException("missing_refresh_token", "No usable refresh token");
        var tokens = await oauth.RefreshAsync(cfg, refresh, ct);
        return (tokens.AccessToken, new Refreshed(keys.Protect(tokens.AccessToken, c.UserId), tokens.ExpiresUtc));
    }

    private async Task<(List<string>, string)> HistorySinceAsync(string token, string historyId, CancellationToken ct)
    {
        var ids = new List<string>();
        string? page = null;
        var latest = historyId;

        do
        {
            var p = page;
            var result = await Retry.GmailAsync(() => gmail.ListHistoryAsync(token, historyId, p, ct), ct, delay: delay);
            ids.AddRange(result.AddedMessageIds);
            latest = result.HistoryId;
            page = result.NextPageToken;
        }
        while (page != null && ids.Count < options.MaxIncrementalMessages);

        return (ids.Distinct().ToList(), latest);
    }

    private async Task<List<string>> ListRecentAsync(string token, int days, int max, CancellationToken ct)
    {
        var ids = new List<string>();
        string? page = null;

        do
        {
            var p = page;
            var (batch, next) = await Retry.GmailAsync(() => gmail.ListMessagesAsync(token, $"newer_than:{days}d -in:chats", p, Math.Min(100, max - ids.Count), ct), ct, delay: delay);
            ids.AddRange(batch);
            page = next;
        }
        while (page != null && ids.Count < max);

        return ids.Take(max).ToList();
    }

    // ── One message ─────────────────────────────────────────────────────────

    private enum Outcome { Skipped, NotFinancial, Created, Updated, Failed }

    private sealed class AiBudget(int left)
    {
        public bool TryTake()
        {
            if (left <= 0) return false;
            left--;
            return true;
        }
    }

    private async Task<Outcome> ProcessMessageAsync(string userId, string token, string id, CompanionSettings settings, AiBudget budget, CancellationToken ct)
    {
        try
        {
            var meta = await Retry.GmailAsync(() => gmail.GetMetadataAsync(token, id, ct), ct, delay: delay);

            // Clearly unrelated mail is never downloaded in full
            var first = MessageClassifier.Classify(meta.From, meta.Subject, meta.Snippet);

            if (!first.IsFinancial && first.Confidence >= 0.9)
            {
                await store.SaveMessageAsync(new ProcessedMessage(userId, id, meta.ReceivedUtc, false, FinKind.NotFinancial, null, null, clock()), ct);
                return Outcome.NotFinancial;
            }

            var full = await Retry.GmailAsync(() => gmail.GetFullAsync(token, id, ct), ct, delay: delay);
            var mail = await ToMailAsync(token, full, ct);
            var cls = MessageClassifier.Classify(mail.From, mail.Subject, RecordExtractor.FullText(mail));

            if (!cls.IsFinancial)
            {
                await store.SaveMessageAsync(new ProcessedMessage(userId, id, meta.ReceivedUtc, false, FinKind.NotFinancial, null, null, clock()), ct);
                return Outcome.NotFinancial;
            }

            var item = RecordExtractor.Extract(userId, mail, cls, clock());
            item = await ExplainAsync(item, mail, settings, budget, ct);
            var (saved, outcome) = await FileItemAsync(userId, item, ct);

            await store.SaveMessageAsync(new ProcessedMessage(userId, id, meta.ReceivedUtc, true, item.Kind, saved.Id, null, clock()), ct);
            return outcome;
        }
        catch (Exception ex) when (ex is not (GoogleAuthException or OperationCanceledException) && !(ex is GmailApiException { Status: 401 or 429 } or GmailApiException { Retryable: true }))
        {
            // One unreadable message never stops the sync; it is recorded so it isn't retried forever
            log($"sync: message {id} for user {userId} failed with {ex.GetType().Name}");
            await store.SaveMessageAsync(new ProcessedMessage(userId, id, clock(), false, FinKind.NotFinancial, null, ex.GetType().Name, clock()), ct);
            return Outcome.Failed;
        }
    }

    private async Task<MailMessage> ToMailAsync(string token, GmailFullMessage full, CancellationToken ct)
    {
        var read = new List<MailAttachment>();

        foreach (var a in full.Attachments.Take(5))
        {
            var supported = a.MimeType is "application/pdf" or "text/plain" || a.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

            if (!supported)
            {
                read.Add(new MailAttachment(a.FileName, a.MimeType, a.Size, null, "This file type isn't read (only PDF and text)."));
                continue;
            }

            if (a.Size > options.MaxAttachmentBytes)
            {
                read.Add(new MailAttachment(a.FileName, a.MimeType, a.Size, null, "The file is too large to read (over 10 MB)."));
                continue;
            }

            try
            {
                var bytes = await Retry.GmailAsync(() => gmail.GetAttachmentAsync(token, full.Meta.Id, a.AttachmentId, ct), ct, delay: delay);
                var (text, problem) = await attachments.ReadAsync(a.FileName, a.MimeType, bytes, ct);
                read.Add(new MailAttachment(a.FileName, a.MimeType, a.Size, text, problem));
            }
            catch (GmailApiException ex) when (!ex.Retryable)
            {
                read.Add(new MailAttachment(a.FileName, a.MimeType, a.Size, null, "Gmail didn't return this attachment."));
            }
        }

        return new MailMessage(full.Meta.Id, full.Meta.ThreadId, full.Meta.From, full.Meta.Subject, full.Meta.ReceivedUtc, full.Meta.Snippet, full.BodyText, read);
    }

    /// <summary>Adds an AI explanation when the person turned AI on. A failure keeps the rule-based result and says so.</summary>
    public async Task<FinancialItem> ExplainAsync(FinancialItem item, MailMessage mail, CompanionSettings settings, CancellationToken ct) =>
        await ExplainAsync(item, mail, settings, new AiBudget(1), ct);

    private async Task<FinancialItem> ExplainAsync(FinancialItem item, MailMessage mail, CompanionSettings settings, AiBudget budget, CancellationToken ct)
    {
        if (!settings.AiEnabled || ai == null || !budget.TryTake())
        {
            return item;
        }

        var text = RecordExtractor.FullText(mail);

        try
        {
            var raw = await ai.AnalyzeAsync(AiGuard.BuildInput(RecordExtractor.SenderName(mail.From), mail.Subject, AiGuard.Redact(text)), ct);
            var explanation = AiGuard.Validate(raw, text, ai.ModelName, clock());

            if (explanation == null)
            {
                return item with { ClassifiedBy = "rules (AI answer was unusable)" };
            }

            var flags = item.Flags.ToList();

            // The model can raise doubt, never remove it or change a number
            if (!raw.IsFinancial && item.Confidence < 0.75)
            {
                flags.Add("The AI check thinks this may not be a financial message. Confirm or delete it.");
            }

            return item with { Ai = explanation, ClassifiedBy = "rules + AI explanation", Flags = flags };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"ai explain failed: {ex.GetType().Name}");
            return item with { ClassifiedBy = "rules (AI unavailable)" };
        }
    }

    /// <summary>Files an item against what the person already has: a payment confirmation marks its
    /// bill paid, a reminder joins its invoice, and a likely duplicate is flagged rather than merged.</summary>
    private async Task<(FinancialItem Saved, Outcome Outcome)> FileItemAsync(string userId, FinancialItem item, CancellationToken ct)
    {
        var existing = await store.GetItemsAsync(userId, ct);
        var now = clock();

        if (item.Kind == FinKind.PaymentConfirmation && ItemRules.MatchPaymentConfirmation(existing, item) is { } bill)
        {
            var source = item.Sources[0];
            var paid = bill with
            {
                UserState = bill.UserState is UserState.Disputed or UserState.NotApplicable ? bill.UserState : UserState.ConfirmedPaid,
                Evidence = [.. bill.Evidence, $"Payment confirmation from {item.Counterparty} on {InvoiceExplainer.Day(source.ReceivedUtc)}."],
                Sources = [.. bill.Sources, source with { Role = "payment confirmation" }],
                UpdatedUtc = now,
            };
            await store.SaveItemAsync(paid, ct);
            await store.AddAuditAsync(userId, "item_payment_confirmed", bill.Id, ct);
            return (paid, Outcome.Updated);
        }

        if (ItemRules.FindSameObligation(existing, item) is { } same)
        {
            var merged = ItemRules.MergeInto(same, item, now);
            await store.SaveItemAsync(merged, ct);
            await store.AddAuditAsync(userId, "item_merged", same.Id, ct);
            return (merged, Outcome.Updated);
        }

        if (ItemRules.FindSamePurchase(existing, item) is { } purchase)
        {
            var merged = purchase with
            {
                Sources = purchase.Sources.Any(s => s.GmailId == item.Sources[0].GmailId) ? purchase.Sources : [.. purchase.Sources, item.Sources[0] with { Role = "follow-up" }],
                UpdatedUtc = now,
            };
            await store.SaveItemAsync(merged, ct);
            return (merged, Outcome.Updated);
        }

        if (ItemRules.FindProbableDuplicate(existing, item) is { } dup)
        {
            item = item with { PossibleDuplicateOf = dup.Id, Flags = [.. item.Flags, $"Looks like a duplicate of “{dup.Title}”. It isn't counted twice; keep it only if it's a separate charge."] };
        }

        await store.SaveItemAsync(item, ct);
        await store.AddAuditAsync(userId, "item_created", item.Id, ct);
        return (item, Outcome.Created);
    }

    /// <summary>Reads an item's messages again with the current rules, keeping the person's edits and decisions.</summary>
    public async Task<FinancialItem?> ReprocessAsync(string userId, string itemId, CancellationToken ct)
    {
        if (await store.GetItemAsync(userId, itemId, ct) is not { } old || await store.GetConnectionAsync(userId, ct) is not { } c)
        {
            return null;
        }

        var (token, _) = await AccessTokenAsync(c, ct);
        var original = old.Sources.FirstOrDefault(s => s.Role == "original") ?? old.Sources.FirstOrDefault();

        if (original == null)
        {
            return old;
        }

        var full = await Retry.GmailAsync(() => gmail.GetFullAsync(token, original.GmailId, ct), ct, delay: delay);
        var mail = await ToMailAsync(token, full, ct);
        var cls = MessageClassifier.Classify(mail.From, mail.Subject, RecordExtractor.FullText(mail));
        var settings = await store.GetSettingsAsync(userId, ct);
        var fresh = RecordExtractor.Extract(userId, mail, cls, clock());
        fresh = await ExplainAsync(fresh, mail, settings, new AiBudget(1), ct);

        var result = CompanionEdits.KeepEdits(old, fresh with
        {
            Id = old.Id,
            CreatedUtc = old.CreatedUtc,
            UpdatedUtc = clock(),
            UserState = old.UserState,
            UserNote = old.UserNote,
            Sources = old.Sources,
            PossibleDuplicateOf = old.PossibleDuplicateOf,
            Evidence = old.Evidence.Concat(fresh.Evidence).Distinct().ToList(),
        });

        await store.SaveItemAsync(result, ct);
        await store.AddAuditAsync(userId, "item_reprocessed", itemId, ct);
        return result;
    }
}
