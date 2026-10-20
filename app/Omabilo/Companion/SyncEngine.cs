// Connects a person's Gmail accounts and keeps their financial records in step with them. A person
// can connect several accounts; each is synced on its own (own tokens, history, lock and watch) and
// all of them file into that one person's records. Runs on the server: the IKON [Cron] sweep and the
// "Sync now" button both sync, and a per-mailbox lock in the database keeps two runs from overlapping.
// Logs carry ids and error codes, never message content or tokens.

/// <summary>One attachment's outcome: its text when it could be read, else a status and a plain reason.</summary>
public sealed record AttachmentReadResult(AttachmentStatus Status, string? Text, int Pages, string? Problem);

public interface IAttachmentReader
{
    /// <summary>Readable text, or a status and plain reason it couldn't be read. Never throws for bad files.</summary>
    Task<AttachmentReadResult> ReadAsync(string fileName, string mimeType, byte[] data, CancellationToken ct);
}

/// <summary>Optical character recognition for scanned PDFs and photos of bills. Used only for people
/// who turned on AI reading, because the file goes to an outside (EU) service.</summary>
public interface IDocumentOcr
{
    string Name { get; }

    /// <summary>The recognised text. Throws when the service fails; the attachment is then marked unread.</summary>
    Task<string> ReadAsync(byte[] data, string mimeType, CancellationToken ct);
}

public sealed record SyncOptions
{
    public int MaxInitialMessages { get; init; } = 300;
    public int MaxIncrementalMessages { get; init; } = 500;
    public long MaxAttachmentBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Files read per email; more are listed as not read.</summary>
    public int MaxAttachments { get; init; } = 5;

    /// <summary>Longest wait for one attachment's download, or for reading it.</summary>
    public TimeSpan AttachmentTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Further tries for an attachment that failed for a passing reason (network, timeout).</summary>
    public int MaxAttachmentRetries { get; init; } = 3;

    /// <summary>Items re-read per sync (retries and items read by older rules).</summary>
    public int RefreshBatch { get; init; } = 10;

    /// <summary>A short pause between emails while scanning, so a first scan stays under Gmail's per-user rate limit.</summary>
    public TimeSpan MessagePause { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a mailbox waits after Gmail asked Omabilo to slow down.</summary>
    public TimeSpan RateLimitPause { get; init; } = TimeSpan.FromMinutes(3);
    public int MaxAiCallsPerSync { get; init; } = 25;
    /// <summary>How often a mailbox is checked when no Gmail push watch is active.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>With an active push watch, the periodic check only reconciles missed notices.</summary>
    public TimeSpan PushReconcileInterval { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>Gmail watches lapse after 7 days; Google recommends renewing daily.</summary>
    public TimeSpan WatchRenewAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>A push that finds the mailbox busy waits this long between tries.</summary>
    public TimeSpan PushBusyWait { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record ConnectResult(bool Ok, string? UserId, string Message, string? Code = null, string? AccountId = null);

public sealed record SyncResult(bool Ok, int Scanned, int NewItems, int Updated, string Message, string? Code = null);

/// <summary>What one push notice led to: the mailboxes to sync now, and how many were duplicates or not ours.</summary>
public sealed record PushOutcome(IReadOnlyList<MailboxRef> ToSync, int Duplicates, int UnknownMailbox);

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
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<string?>? pushTopic = null,
    IDocumentOcr? ocr = null,
    Func<string, CancellationToken, Task<bool>>? hasPremium = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = delay ?? Task.Delay;

    /// <summary>The Pub/Sub topic Gmail should notify, or null when push is not set up (polling only).</summary>
    public string? PushTopic => pushTopic?.Invoke();

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

        GmailProfile profile;

        try
        {
            profile = await Retry.GmailAsync(() => gmail.GetProfileAsync(tokens.AccessToken, ct), ct, delay: delay);
        }
        catch (GmailApiException ex)
        {
            // Status and Google's reason code only: never a token, code or message content
            log($"gmail connect: profile check failed ({ex.Status} {ex.Reason}) for user {saved.UserId}");
            await TryRevokeAsync(tokens.RefreshToken, ct);
            await store.AddAuditAsync(saved.UserId, "gmail_connect_failed", null, ct);
            return ProfileFailure(saved.UserId, ex);
        }

        var now = clock();

        // The same address connected again is a reconnection of that account; another address is
        // added beside the ones already connected, never in their place
        var accounts = await store.GetConnectionsAsync(saved.UserId, ct);
        var existing = accounts.FirstOrDefault(a => string.Equals(a.GoogleEmail, profile.EmailAddress, StringComparison.OrdinalIgnoreCase));

        if (existing == null && accounts.Count >= Mailboxes.MaxPerUser)
        {
            await TryRevokeAsync(tokens.RefreshToken, ct);
            await store.AddAuditAsync(saved.UserId, "gmail_account_limit", null, ct);
            return new(false, saved.UserId, $"You already have {Mailboxes.MaxPerUser} Gmail accounts connected. Disconnect one before adding another. Nothing was changed.", "account_limit");
        }

        var accountId = existing?.AccountId ?? (accounts.Count == 0 ? Mailboxes.Main : Mailboxes.NewId());
        var purpose = Mailboxes.Key(saved.UserId, accountId);

        await store.SaveConnectionAsync(new GmailConnection
        {
            UserId = saved.UserId,
            AccountId = accountId,
            GoogleEmail = profile.EmailAddress,
            GrantedScopes = tokens.Scope,
            RefreshTokenEnc = keys.Protect(tokens.RefreshToken, purpose),
            AccessTokenEnc = keys.Protect(tokens.AccessToken, purpose),
            AccessExpiresUtc = tokens.ExpiresUtc,
            Status = ConnectionStatus.Connected,
            NextAttemptUtc = now,
            ConnectedUtc = existing?.ConnectedUtc ?? now,
        }, ct);
        await store.AddAuditAsync(saved.UserId, existing != null ? "gmail_reconnected" : accounts.Count == 0 ? "gmail_connected" : "gmail_account_added", null, ct);

        // The free Gmail trial starts at the first connection only
        var settings = await store.GetSettingsAsync(saved.UserId, ct);

        if (settings.GmailTrialStartUtc == null)
        {
            await store.SaveSettingsAsync(saved.UserId, settings with { GmailTrialStartUtc = now }, ct);
        }

        var message = existing != null
            ? $"Gmail ({profile.EmailAddress}) is connected again. Omabilo carries on where it left off."
            : accounts.Count == 0
                ? $"Gmail ({profile.EmailAddress}) is connected. Omabilo will now look for financial messages."
                : $"Gmail ({profile.EmailAddress}) is added. Omabilo now reads {accounts.Count + 1} Gmail accounts into one inbox.";
        return new(true, saved.UserId, message, AccountId: accountId);
    }

    /// <summary>Google issued tokens but Gmail itself refused: say which side has to change.</summary>
    internal static ConnectResult ProfileFailure(string userId, GmailApiException ex)
    {
        var reason = ex.Reason ?? "";

        if (ex.Status == 403 && (reason.Equals("accessNotConfigured", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("SERVICE_DISABLED", StringComparison.OrdinalIgnoreCase)))
        {
            return new(false, userId,
                "Google approved the sign-in, but the Gmail API isn't enabled in the Google Cloud project that owns Omabilo's OAuth client. " +
                "The app owner needs to enable it in that project (APIs & Services → Library → Gmail API → Enable), wait a few minutes and try again. Nothing was connected.",
                "gmail_api_disabled");
        }

        if (ex.Status == 403)
        {
            return new(false, userId, $"Google didn't let Omabilo read this mailbox (Google's reason: {reason}). Nothing was connected.", "gmail_forbidden");
        }

        return new(false, userId, $"Gmail didn't answer properly (error {ex.Status}). Please try again in a few minutes. Nothing was connected.", "gmail_unavailable");
    }

    /// <summary>Disconnects every Gmail account the person connected.</summary>
    public async Task DisconnectAsync(string userId, CancellationToken ct)
    {
        foreach (var c in await store.GetConnectionsAsync(userId, ct))
        {
            await DisconnectAsync(userId, c.AccountId, ct);
        }
    }

    /// <summary>Revokes Google's grant for one account (best effort) and deletes its stored tokens. The
    /// person's other accounts and every record stay.</summary>
    public async Task DisconnectAsync(string userId, string accountId, CancellationToken ct)
    {
        var c = await store.GetConnectionAsync(userId, accountId, ct);

        if (c == null)
        {
            return;
        }

        if (protector() is { } keys)
        {
            if (await store.GetWatchAsync(c.Ref(), ct) is { ExpiresUtc: { } until } && until > clock())
            {
                try
                {
                    var (token, _) = await AccessTokenAsync(c, ct);
                    await gmail.StopWatchAsync(token, ct);
                }
                catch (Exception ex)
                {
                    // Revoking below ends the notices anyway: Gmail stops watching a mailbox it may no longer read
                    log($"gmail stop watch failed: {ex.GetType().Name}");
                }
            }

            if (keys.Unprotect(c.RefreshTokenEnc, Purpose(c)) is { } refresh)
            {
                await TryRevokeAsync(refresh, ct);
            }
        }

        await store.DeleteConnectionAsync(userId, accountId, ct);
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

        foreach (var mailbox in due)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await SyncMailboxAsync(mailbox, ct);
            }
            catch (Exception ex)
            {
                log($"sync sweep: mailbox {mailbox} failed with {ex.GetType().Name}");
            }
        }

        await store.PruneAsync(clock(), ct);
        return due.Count;
    }

    /// <summary>Syncs every Gmail account the person connected, one after another ("Sync now").</summary>
    public async Task<SyncResult> SyncAsync(string userId, CancellationToken ct, bool fullRescan = false)
    {
        var accounts = await store.GetConnectionsAsync(userId, ct);

        if (accounts.Count == 0)
        {
            return new(false, 0, 0, 0, "Gmail isn't connected.", "not_connected");
        }

        var results = new List<(GmailConnection Account, SyncResult Result)>();

        foreach (var account in accounts)
        {
            if (ct.IsCancellationRequested) break;
            results.Add((account, await SyncMailboxAsync(account.Ref(), ct, fullRescan)));
        }

        if (results.Count == 1)
        {
            return results[0].Result;
        }

        var failed = results.FirstOrDefault(r => !r.Result.Ok);
        return new(failed.Result == null, results.Sum(r => r.Result.Scanned), results.Sum(r => r.Result.NewItems), results.Sum(r => r.Result.Updated),
            string.Join(" ", results.Select(r => $"{r.Account.GoogleEmail ?? "Gmail"}: {r.Result.Message}")), failed.Result?.Code);
    }

    /// <summary>Syncs one connected Gmail account into its owner's records.</summary>
    public async Task<SyncResult> SyncMailboxAsync(MailboxRef mailbox, CancellationToken ct, bool fullRescan = false)
    {
        var now = clock();
        var userId = mailbox.UserId;

        if (await store.GetConnectionAsync(userId, mailbox.AccountId, ct) is not { } c)
        {
            return new(false, 0, 0, 0, "Gmail isn't connected.", "not_connected");
        }

        if (c.Status == ConnectionStatus.NeedsReconnect)
        {
            return new(false, 0, 0, 0, "Gmail needs to be reconnected.", "needs_reconnect");
        }

        // After the free 30 days Gmail is read only for Premium; nothing is fetched from Google otherwise
        var plan = await store.GetSettingsAsync(userId, ct);
        if (hasPremium != null && !Membership.GmailFreeNow(plan, c, now) && !Membership.HasAccessCode(plan) && !await hasPremium(userId, ct))
        {
            // Checked again in an hour, so a paused mailbox never crowds out others in the sweep
            await store.SaveConnectionAsync(c with { NextAttemptUtc = now.AddHours(1) }, ct);
            return new(false, 0, 0, 0, $"Your free {Membership.GmailTrialDays}-day Gmail trial has ended. Get Premium to keep Gmail syncing.", "trial_ended");
        }

        if (!await store.TryClaimSyncAsync(mailbox, now, now.Add(options.LockDuration), ct))
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
                    log($"sync: history expired for mailbox {mailbox}, rescanning");
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

                // An email that failed earlier (a passing error, or one fixed since) is read again by a scan
                if (await store.GetMessageAsync(userId, Mailboxes.MessageKey(c.AccountId, id), ct) is { } known && !(needScan && known.Error is { } err && err != "processing"))
                {
                    continue;
                }

                if (needScan && scanned > 0)
                {
                    await _wait(options.MessagePause, ct);
                }

                scanned++;
                var outcome = await ProcessMessageAsync(userId, c.AccountId, token, id, c.GoogleEmail, settings, budget, ct);
                if (outcome == Outcome.Created) created++;
                if (outcome == Outcome.Updated) updated++;
            }

            updated += await RefreshItemsAsync(userId, c.AccountId, token, c.GoogleEmail, settings, budget, ct);

            var pushActive = await EnsureWatchAsync(mailbox, token, ct);
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
                NextAttemptUtc = done.Add(pushActive ? options.PushReconcileInterval : options.PollInterval),
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
            log($"sync: mailbox {mailbox} needs to reconnect ({ex.Code})");
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
        catch (GmailApiException ex) when (ex.Retryable && ex.Status is 429 or 403)
        {
            // Google asked to slow down: what was found so far is saved, and the scan carries on shortly
            log($"sync: mailbox {mailbox} paused by a Gmail rate limit after {scanned} message(s)");
            await store.SaveConnectionAsync(c with
            {
                AccessTokenEnc = refreshed?.AccessEnc ?? c.AccessTokenEnc,
                AccessExpiresUtc = refreshed?.Expires ?? c.AccessExpiresUtc,
                Status = ConnectionStatus.Connected,
                LastSyncUtc = clock(),
                LastError = RateLimitNote,
                NextAttemptUtc = clock().Add(options.RateLimitPause),
            }, ct);
            await store.AddAuditAsync(userId, "gmail_sync_rate_limited", null, ct);
            return new(false, scanned, created, updated,
                $"Gmail asked Omabilo to slow down. {(created > 0 ? $"{created} new item{(created == 1 ? "" : "s")} saved so far; " : "")}it carries on by itself in a few minutes.", "rate_limited");
        }
        catch (Exception ex) when (ex is GmailApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var failures = c.FailureCount + 1;
            var reason = ex is GmailApiException g ? $"Gmail error {g.Status} ({g.Reason})" : "Network problem reaching Gmail";
            log($"sync: mailbox {mailbox} failed: {reason}");
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
            await store.ReleaseSyncAsync(mailbox, CancellationToken.None);
        }
    }

    private sealed record Refreshed(string AccessEnc, DateTime Expires);

    /// <summary>Kept as the connection's last note while a rate-limit pause lasts.</summary>
    public const string RateLimitNote = "Paused: Gmail asked Omabilo to slow down";

    // ── Gmail push ──────────────────────────────────────────────────────────

    /// <summary>Starts or renews the Gmail watch when push is configured. Never fails the sync: without
    /// a watch the mailbox is simply polled more often. True when a watch is active.</summary>
    private async Task<bool> EnsureWatchAsync(MailboxRef mailbox, string token, CancellationToken ct)
    {
        var userId = mailbox.UserId;
        var topic = PushTopic;
        var watch = await store.GetWatchAsync(mailbox, ct) ?? new GmailWatch { UserId = userId, AccountId = mailbox.AccountId };
        var now = clock();

        if (topic == null)
        {
            return false;
        }

        var current = watch.Topic == topic && watch.LastError == null && watch.ExpiresUtc is { } exp && exp > now.AddHours(1);

        if (current && watch.RenewedUtc is { } renewed && now - renewed < options.WatchRenewAfter)
        {
            return true;
        }

        try
        {
            var result = await Retry.GmailAsync(() => gmail.WatchAsync(token, topic, ct), ct, delay: delay);
            await store.SaveWatchAsync(watch with { Topic = topic, ExpiresUtc = result.ExpiresUtc, RenewedUtc = now, LastError = null }, ct);

            if (!current)
            {
                await store.AddAuditAsync(userId, "gmail_watch_started", null, ct);
                log($"gmail watch active for mailbox {mailbox} until {result.ExpiresUtc:u}");
            }

            return true;
        }
        catch (GmailApiException ex)
        {
            // Typical: 403/404 when the topic is missing or Gmail may not publish to it
            var reason = $"Gmail watch failed: {ex.Status} {ex.Reason}";
            log($"{reason} for mailbox {mailbox}");

            if (watch.LastError != reason)
            {
                await store.AddAuditAsync(userId, "gmail_watch_failed", null, ct);
            }

            await store.SaveWatchAsync(watch with { Topic = topic, LastError = reason }, ct);
            return current;
        }
    }

    /// <summary>Records one Pub/Sub notice and says whom to sync. Duplicates (Pub/Sub delivers at least
    /// once) and notices older than what was already synced are counted and dropped.</summary>
    public async Task<PushOutcome> HandlePushAsync(GmailPushNotice notice, CancellationToken ct)
    {
        var mailboxes = await store.MailboxesForGmailAsync(notice.EmailAddress, ct);
        var toSync = new List<MailboxRef>();
        var duplicates = 0;
        var now = clock();

        foreach (var mailbox in mailboxes)
        {
            var watch = await store.GetWatchAsync(mailbox, ct) ?? new GmailWatch { UserId = mailbox.UserId, AccountId = mailbox.AccountId };
            var connection = await store.GetConnectionAsync(mailbox.UserId, mailbox.AccountId, ct);
            var synced = ulong.TryParse(connection?.HistoryId, out var h) ? h : 0;

            if (notice.HistoryId <= watch.LastPushHistoryId || notice.HistoryId <= synced)
            {
                duplicates++;
                await store.SaveWatchAsync(watch with { DuplicatePushCount = watch.DuplicatePushCount + 1 }, ct);
                continue;
            }

            await store.SaveWatchAsync(watch with { LastPushUtc = now, LastPushHistoryId = notice.HistoryId, PushCount = watch.PushCount + 1 }, ct);

            // A connection backing off after errors keeps its backoff; the notice is remembered for later
            if ((connection is { Status: ConnectionStatus.Error } || connection?.LastError == RateLimitNote) && connection!.NextAttemptUtc > now)
            {
                continue;
            }

            await store.MarkSyncDueAsync(mailbox, now, ct);
            toSync.Add(mailbox);
        }

        return new PushOutcome(toSync, duplicates, mailboxes.Count == 0 ? 1 : 0);
    }

    /// <summary>The sync a notice triggers. Waits if a sync is already running, and runs once more if
    /// another notice arrived while it worked, so nothing announced is left for the fallback sweep.</summary>
    public async Task<SyncResult> SyncAfterPushAsync(MailboxRef mailbox, CancellationToken ct)
    {
        SyncResult last = new(false, 0, 0, 0, "Not run", "not_run");

        for (var round = 0; round < 3; round++)
        {
            var started = clock();

            if (await store.GetWatchAsync(mailbox, ct) is { } w)
            {
                await store.SaveWatchAsync(w with { LastPushSyncStartedUtc = started }, ct);
            }

            for (var attempt = 0; attempt < 6; attempt++)
            {
                last = await SyncMailboxAsync(mailbox, ct);

                if (last.Code != "busy")
                {
                    break;
                }

                await _wait(options.PushBusyWait, ct);
            }

            if (!last.Ok || await store.GetWatchAsync(mailbox, ct) is not { LastPushUtc: { } pushed } || pushed <= started)
            {
                break;
            }
        }

        return last;
    }

    private async Task<(string Token, Refreshed? Refreshed)> AccessTokenAsync(GmailConnection c, CancellationToken ct)
    {
        var keys = protector() ?? throw new InvalidOperationException("Token encryption key is not configured");
        var cfg = config() ?? throw new InvalidOperationException("Google OAuth is not configured");

        if (c.AccessExpiresUtc is { } exp && exp > clock().AddMinutes(1) && keys.Unprotect(c.AccessTokenEnc, Purpose(c)) is { } access)
        {
            return (access, null);
        }

        var refresh = keys.Unprotect(c.RefreshTokenEnc, Purpose(c)) ?? throw new GoogleAuthException("missing_refresh_token", "No usable refresh token");
        var tokens = await oauth.RefreshAsync(cfg, refresh, ct);
        return (tokens.AccessToken, new Refreshed(keys.Protect(tokens.AccessToken, Purpose(c)), tokens.ExpiresUtc));
    }

    /// <summary>What a token is encrypted for: the owner, and the account for a further one, so a token
    /// copied to another person's or another account's row can't be decrypted there.</summary>
    private static string Purpose(GmailConnection c) => Mailboxes.Key(c.UserId, c.AccountId);

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

    private async Task<Outcome> ProcessMessageAsync(string userId, string accountId, string token, string id, string? ownerEmail, CompanionSettings settings, AiBudget budget,
        CancellationToken ct)
    {
        var key = Mailboxes.MessageKey(accountId, id);

        try
        {
            var meta = await Retry.GmailAsync(() => gmail.GetMetadataAsync(token, id, ct), ct, delay: delay);

            // Clearly unrelated mail is never downloaded in full, unless it carries files: a bill can
            // arrive as a PDF with an empty body and a subject like "Fwd:" or "Scan"
            if (!meta.MayHaveAttachments && !MessageClassifier.WorthReading(meta.Subject, meta.Snippet, meta.Labels))
            {
                await store.SaveMessageAsync(new ProcessedMessage(userId, key, meta.ReceivedUtc, false, FinKind.NotFinancial, null, null, clock()), ct);
                return Outcome.NotFinancial;
            }

            var full = await Retry.GmailAsync(() => gmail.GetFullAsync(token, id, ct), ct, delay: delay);
            var mail = await ToMailAsync(token, full, settings, ct);
            return (await FileMailAsync(userId, key, meta.ReceivedUtc, mail, ownerEmail, settings, budget, ct, accountId)).Outcome;
        }
        catch (Exception ex) when (ex is not (GoogleAuthException or OperationCanceledException) && !(ex is GmailApiException { Status: 401 or 429 } or GmailApiException { Retryable: true }))
        {
            // One unreadable message never stops the sync; it is recorded so it isn't retried forever
            log($"sync: message {id} for user {userId} failed with {ex.GetType().Name}");
            await store.SaveMessageAsync(new ProcessedMessage(userId, key, clock(), false, FinKind.NotFinancial, null, ex.GetType().Name, clock()), ct);
            return Outcome.Failed;
        }
    }

    /// <summary>Classifies a decoded email and files what it holds: the part Gmail and forwarded mail share.</summary>
    /// <param name="id">The processed-message key: a Gmail id (with its account for a further account), or a forwarded or uploaded document's id.</param>
    /// <param name="accountId">The Gmail account the message is in, so its record can name where it came from.</param>
    private async Task<(Outcome Outcome, int Created, string? FirstId)> FileMailAsync(string userId, string id, DateTime receivedUtc, MailMessage mail, string? ownerEmail,
        CompanionSettings settings, AiBudget budget, CancellationToken ct, string? accountId = null)
    {
        var cls = Classify(mail);

        if (!cls.IsFinancial && UnreadDocument(mail) is { } guess)
        {
            cls = guess;
        }
        else if (!cls.IsFinancial)
        {
            await store.SaveMessageAsync(new ProcessedMessage(userId, id, receivedUtc, false, FinKind.NotFinancial, null, null, clock()), ct);
            return (Outcome.NotFinancial, 0, null);
        }

        var result = Outcome.Updated;
        var created = 0;
        string? firstId = null;

        foreach (var extracted in ExtractAll(userId, mail, cls, ownerEmail))
        {
            var item = await ExplainAsync(WithAccount(extracted, accountId), mail, settings, budget, ct);

            // A forwarded email or uploaded file can't be fetched again, so nothing is planned for a later try
            item = BillUpload.NotInGmail(id) ? item with { AttachmentRetries = 0, AttachmentRetryAfterUtc = null } : WithRetryPlan(item, null);

            var (saved, outcome) = await FileItemAsync(userId, item, ct);
            firstId ??= saved.Id;

            if (outcome == Outcome.Created)
            {
                result = Outcome.Created;
                created++;
            }

            var read = saved.Attachments.Count(a => a.WasRead);
            log($"sync: message {id} for user {userId}: {item.Kind}, {item.Attachments.Count} attachment(s), {read} read, outcome {outcome}");
        }

        await store.SaveMessageAsync(new ProcessedMessage(userId, id, receivedUtc, true, cls.Kind, firstId, null, clock()), ct);
        return (result, created, firstId);
    }

    /// <summary>Marks where a further account's message came from; the main account's sources stay as before.</summary>
    private static FinancialItem WithAccount(FinancialItem item, string? accountId) =>
        Mailboxes.IsMain(accountId) ? item : item with { Sources = [.. item.Sources.Select(s => s with { Account = accountId })] };

    // ── Forwarded bills ─────────────────────────────────────────────────────

    /// <summary>The person's forwarding address, made the first time they ask for it.</summary>
    public async Task<ForwardAddress> ForwardAddressAsync(string userId, CancellationToken ct) =>
        await store.GetForwardAsync(userId, ct)
            ?? await store.GetOrCreateForwardAsync(new ForwardAddress { UserId = userId, Token = BillForwarding.NewToken(), CreatedUtc = clock() }, ct);

    /// <summary>Replaces the address: mail to the old one is ignored from now on.</summary>
    public async Task<ForwardAddress> NewForwardAddressAsync(string userId, CancellationToken ct)
    {
        var fresh = new ForwardAddress { UserId = userId, Token = BillForwarding.NewToken(), CreatedUtc = clock() };
        await store.SaveForwardAsync(fresh, ct);
        await store.AddAuditAsync(userId, "forward_address_replaced", null, ct);
        return fresh;
    }

    /// <summary>Files one email sent to a forwarding address for the person the address belongs to.
    /// The address alone decides whose it is; mail to an unknown address is never read further.</summary>
    public async Task<ForwardResult> ImportForwardedAsync(ForwardedEmail email, CancellationToken ct)
    {
        if (BillForwarding.TokenOf(email.Recipient) is not { } token || await store.FindForwardAsync(token, ct) is not { } address)
        {
            return new(ForwardOutcome.UnknownAddress, null);
        }

        var userId = address.UserId;
        var now = clock();

        if (BillForwarding.GmailConfirmation(email.From, email.Subject, email.BodyText) is { } confirm)
        {
            await store.SaveForwardAsync(address with
            {
                GmailConfirmCode = confirm.Code,
                GmailConfirmLink = confirm.Link,
                GmailConfirmFor = confirm.ForAddress,
                GmailConfirmUtc = now,
            }, ct);
            await store.AddAuditAsync(userId, "forward_gmail_confirmation", null, ct);
            return new(ForwardOutcome.GmailConfirmation, userId);
        }

        var id = BillForwarding.IdPrefix + email.Id;

        if (!await store.TryClaimMessageAsync(new ProcessedMessage(userId, id, email.ReceivedUtc, false, FinKind.NotFinancial, null, "processing", now), ct))
        {
            return new(ForwardOutcome.AlreadyDone, userId);
        }

        var settings = await store.GetSettingsAsync(userId, ct);

        // A hand-forwarded email comes from the person; an automatic forward keeps the biller as sender
        var owner = BillForwarding.IsManualForward(email.Subject, email.BodyText) ? InvoiceAnalysis.Address(email.From) : null;
        var full = new GmailFullMessage(
            new GmailMessageMeta(id, id, email.From, email.Subject, email.ReceivedUtc, BillForwarding.Snippet(email.BodyText), null, email.Files.Count > 0),
            email.BodyText,
            email.Files.Select((f, i) => new GmailAttachmentRef(f.FileName, f.MimeType, f.Size, $"file-{i}", f.Data)).ToList());

        try
        {
            var mail = await ToMailAsync("", full, settings, ct);
            var (outcome, created, _) = await FileMailAsync(userId, id, email.ReceivedUtc, mail, owner, settings,
                new AiBudget(settings.AiEnabled && ai != null ? 5 : 0), ct);

            await store.SaveForwardAsync(address with { LastReceivedUtc = now, ReceivedCount = address.ReceivedCount + 1 }, ct);
            await store.AddAuditAsync(userId, "forward_received", null, ct);
            return new(outcome == Outcome.NotFinancial ? ForwardOutcome.NotFinancial : ForwardOutcome.Filed, userId, created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"forwarded message {email.Id} for user {userId} failed with {ex.GetType().Name}");
            await store.SaveMessageAsync(new ProcessedMessage(userId, id, email.ReceivedUtc, false, FinKind.NotFinancial, null, ex.GetType().Name, clock()), ct);
            return new(ForwardOutcome.Failed, userId);
        }
    }

    // ── Uploaded documents ──────────────────────────────────────────────────

    /// <summary>Files a PDF the signed-in person uploaded, for that person only. The caller passes the
    /// user id from the server's session; the file itself is not kept, only what was read from it.</summary>
    public async Task<UploadResult> ImportUploadAsync(string userId, string fileName, byte[] data, CancellationToken ct, bool allowAi = true)
    {
        if (BillUpload.Reject(fileName, data) is { } rejected)
        {
            return new(rejected);
        }

        return await ImportDocumentAsync(userId, BillUpload.CleanFileName(fileName), BillUpload.MimeOf(fileName, data)!, data, allowAi, ct);
    }

    /// <summary>Files the text of a bill the person pasted (from an email, a web page or an e-invoice),
    /// read by the same rules as an uploaded document. The same text pasted twice is filed once.</summary>
    public async Task<UploadResult> ImportPastedTextAsync(string userId, string text, CancellationToken ct, bool allowAi = true)
    {
        text = text.Replace("\r\n", "\n").Trim();

        if (text.Length == 0)
        {
            return new(UploadOutcome.Empty);
        }

        if (text.Length > BillUpload.MaxPastedChars)
        {
            return new(UploadOutcome.TooLarge);
        }

        return await ImportDocumentAsync(userId, BillUpload.PastedName, "text/plain", System.Text.Encoding.UTF8.GetBytes(text), allowAi, ct);
    }

    private async Task<UploadResult> ImportDocumentAsync(string userId, string name, string mime, byte[] data, bool allowAi, CancellationToken ct)
    {
        var id = BillUpload.IdFor(data);
        var now = clock();

        if (!await store.TryClaimMessageAsync(new ProcessedMessage(userId, id, now, false, FinKind.NotFinancial, null, "processing", now), ct))
        {
            var seen = await store.GetMessageAsync(userId, id, ct);

            // Another upload of this file is still being read; one stuck after a restart is read again
            if (seen is { Error: "processing" } && now - seen.ProcessedUtc < TimeSpan.FromMinutes(10))
            {
                return new(UploadOutcome.AlreadyFiled);
            }

            if (seen is { ItemId: { } existing } && await store.GetItemAsync(userId, existing, ct) != null)
            {
                return new(UploadOutcome.AlreadyFiled, existing);
            }

            if (seen is { IsFinancial: false, Error: null })
            {
                return new(UploadOutcome.NotFinancial);
            }

            // It failed before, or the person deleted what it became: read it again
            await store.SaveMessageAsync(new ProcessedMessage(userId, id, now, false, FinKind.NotFinancial, null, "processing", now), ct);
        }

        // allowAi is false on the free plan: no OCR and no AI, whatever the saved setting says
        var saved = await store.GetSettingsAsync(userId, ct);
        var settings = allowAi ? saved : saved with { AiEnabled = false };
        var full = new GmailFullMessage(
            new GmailMessageMeta(id, id, "", Path.GetFileNameWithoutExtension(name), now, "", null, true),
            "",
            [new GmailAttachmentRef(name, mime, data.LongLength, "file-0", data)]);

        try
        {
            var mail = await ToMailAsync("", full, settings, ct);

            // A PDF with no readable text (a scan, a locked or damaged file) isn't saved as a guessed
            // record: the file isn't kept, so it could never be read later. The error lets it be uploaded again.
            if (mail.Attachments.FirstOrDefault() is { } doc && string.IsNullOrWhiteSpace(doc.Text))
            {
                await store.SaveMessageAsync(new ProcessedMessage(userId, id, now, false, FinKind.NotFinancial, null, $"unread:{doc.Status}", clock()), ct);
                log($"upload {id} for user {userId}: not read ({doc.Status})");
                return new(UploadOutcome.Unreadable, Unread: doc.Status);
            }

            var (outcome, created, itemId) = await FileMailAsync(userId, id, now, mail, null, settings,
                new AiBudget(settings.AiEnabled && ai != null ? 1 : 0), ct);

            await store.AddAuditAsync(userId, "document_uploaded", itemId, ct);
            log($"upload {id} for user {userId}: {outcome}, {created} new item(s)");
            return outcome == Outcome.NotFinancial ? new(UploadOutcome.NotFinancial) : new(UploadOutcome.Filed, itemId, created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"upload {id} for user {userId} failed with {ex.GetType().Name}");
            await store.SaveMessageAsync(new ProcessedMessage(userId, id, now, false, FinKind.NotFinancial, null, ex.GetType().Name, clock()), ct);
            return new(UploadOutcome.Failed);
        }
    }

    private static Classification Classify(MailMessage mail) =>
        MessageClassifier.Classify(mail.From, mail.Subject, RecordExtractor.FullText(mail), mail.Labels, mail.Attachments.Select(a => a.FileName).ToList());

    private static readonly System.Text.RegularExpressions.Regex BillWordsRx = new(
        @"(?i)(invoice|lasku|bill|receipt|kuitti|maksu|payment|statement|tiliote|erittely|palkka|payslip|faktura|rechnung)");

    /// <summary>An email whose text says nothing financial but whose document couldn't be read is not
    /// silently dropped when the file may be a bill (named like one, or it may still download): it is
    /// kept as a document to check, so the person sees it and why it wasn't read.</summary>
    private static Classification? UnreadDocument(MailMessage mail)
    {
        var unread = mail.Attachments.Where(a => a.Status is AttachmentStatus.ScannedNotRead or AttachmentStatus.PasswordProtected or AttachmentStatus.Damaged
            or AttachmentStatus.TimedOut or AttachmentStatus.DownloadFailed or AttachmentStatus.TooLarge).ToList();

        if (unread.Count == 0 || !(unread.Any(a => a.Status is AttachmentStatus.TimedOut or AttachmentStatus.DownloadFailed)
            || unread.Any(a => BillWordsRx.IsMatch(a.FileName)) || BillWordsRx.IsMatch(mail.Subject)))
        {
            return null;
        }

        return new Classification(true, FinKind.Invoice, 0.4, ["has an attached document that Omabilo couldn't read", "the file name or subject suggests a bill"], "rules (document not read)");
    }

    /// <summary>One item per email, or one per attached invoice when an email carries several different
    /// bills (different amounts or invoice numbers), each pointing at its own document.</summary>
    private List<FinancialItem> ExtractAll(string userId, MailMessage mail, Classification cls, string? ownerEmail)
    {
        var whole = RecordExtractor.Extract(userId, mail, cls, clock(), ownerEmail);
        var docs = mail.Attachments.Where(a => !string.IsNullOrWhiteSpace(a.Text)).ToList();

        if (cls.Method == "rules (document not read)")
        {
            var name = mail.Attachments.First(a => !string.IsNullOrWhiteSpace(a.Problem)).FileName;
            var placeholder = whole with { Title = $"Document to check: {name}", Missing = [.. whole.Missing.Where(m => m.Contains("couldn't be read"))] };
            return [placeholder with { Advice = InvoiceAnalysis.Advise(placeholder, clock()) }];
        }

        if (!whole.IsBill || docs.Count < 2)
        {
            return [whole];
        }

        var parts = docs.Select(d => RecordExtractor.Extract(userId, OnlyDocument(mail, d.FileName), cls, clock(), ownerEmail)).ToList();
        var separate = parts.Where(p => p.Amount != null && (p.InvoiceNumber != null || p.DueDate != null)).ToList();

        return separate.Count >= 2 && separate.Select(p => (p.Amount, p.InvoiceNumber, p.DueDate)).Distinct().Count() == separate.Count
            ? separate
            : [whole];
    }

    /// <summary>The email with one document: what a split item, or its re-read, is built from.</summary>
    private static MailMessage OnlyDocument(MailMessage mail, string fileName) =>
        mail with { Attachments = mail.Attachments.Where(a => a.FileName == fileName || string.IsNullOrWhiteSpace(a.Text)).ToList() };

    private static bool IsPdf(GmailAttachmentRef a) =>
        a.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) || a.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsImage(GmailAttachmentRef a) =>
        a.MimeType.ToLowerInvariant() is "image/jpeg" or "image/jpg" or "image/png" or "image/tiff" or "image/heic" or "image/webp";

    /// <summary>Downloads and reads each attachment within limits (count, size, time), so a broken,
    /// locked, huge or slow file is recorded with a reason and never stops the message.</summary>
    private async Task<MailMessage> ToMailAsync(string token, GmailFullMessage full, CompanionSettings settings, CancellationToken ct)
    {
        var read = new List<MailAttachment>();
        var ocrAllowed = ocr != null && settings.AiEnabled;
        var considered = 0;

        // Pictures placed in the email's layout (logos, signatures) aren't documents
        foreach (var a in full.Attachments.Where(a => !(a.Inline && a.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))))
        {
            var pdf = IsPdf(a);
            var text = PdfAttachmentReader.IsPlainText(a.FileName, a.MimeType);
            var image = IsImage(a);

            if (!pdf && !text && !image)
            {
                read.Add(new MailAttachment(a.FileName, a.MimeType, a.Size, null, "Only PDF, text and image files are read.", AttachmentStatus.NotSupported));
                continue;
            }

            if (++considered > options.MaxAttachments)
            {
                read.Add(new MailAttachment(a.FileName, a.MimeType, a.Size, null, $"Only the first {options.MaxAttachments} files of an email are read.", AttachmentStatus.NotSupported));
                continue;
            }

            if (a.Size > options.MaxAttachmentBytes)
            {
                read.Add(new MailAttachment(a.FileName, a.MimeType, a.Size, null, $"The file is too large to read (over {options.MaxAttachmentBytes / (1024 * 1024)} MB).", AttachmentStatus.TooLarge));
                continue;
            }

            if (image && !ocrAllowed)
            {
                read.Add(new MailAttachment(a.FileName, a.MimeType, a.Size, null,
                    ocr == null ? "Images aren't read: OCR isn't available on this server." : "Images aren't read unless you turn on AI reading in Settings (it uses OCR).",
                    AttachmentStatus.NotSupported));
                continue;
            }

            read.Add(await ReadAttachmentAsync(token, full.Meta.Id, a, pdf, image, ocrAllowed, ct));
        }

        return new MailMessage(full.Meta.Id, full.Meta.ThreadId, full.Meta.From, full.Meta.Subject, full.Meta.ReceivedUtc, full.Meta.Snippet, full.BodyText, read, full.Meta.Labels);
    }

    private async Task<MailAttachment> ReadAttachmentAsync(string token, string messageId, GmailAttachmentRef a, bool pdf, bool image, bool ocrAllowed, CancellationToken ct)
    {
        byte[] bytes;

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(options.AttachmentTimeout);

            try
            {
                bytes = a.InlineData ?? await Retry.GmailAsync(() => gmail.GetAttachmentAsync(token, messageId, a.AttachmentId, timeout.Token), timeout.Token, delay: delay);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new MailAttachment(a.FileName, a.MimeType, a.Size, null, "Downloading it from Gmail took too long. Omabilo will try again.", AttachmentStatus.TimedOut);
            }
            catch (GmailApiException ex) when (ex.Status != 401)
            {
                return new MailAttachment(a.FileName, a.MimeType, a.Size, null,
                    ex.Retryable ? "Gmail didn't send the file this time. Omabilo will try again." : "Gmail didn't return this attachment.",
                    ex.Retryable ? AttachmentStatus.DownloadFailed : AttachmentStatus.Damaged);
            }
            catch (HttpRequestException)
            {
                return new MailAttachment(a.FileName, a.MimeType, a.Size, null, "A network problem stopped the download. Omabilo will try again.", AttachmentStatus.DownloadFailed);
            }
        }

        var size = Math.Max(a.Size, bytes.Length);

        if (bytes.Length > options.MaxAttachmentBytes)
        {
            return new MailAttachment(a.FileName, a.MimeType, size, null, "The file is too large to read.", AttachmentStatus.TooLarge);
        }

        AttachmentReadResult result;

        if (image)
        {
            result = new AttachmentReadResult(AttachmentStatus.ScannedNotRead, null, 1, "An image of a document.");
        }
        else
        {
            try
            {
                // PDF parsing is CPU work; it runs off the sync's thread and is abandoned when it takes too long
                result = await Task.Run(() => attachments.ReadAsync(a.FileName, a.MimeType, bytes, ct), ct).WaitAsync(options.AttachmentTimeout, ct);
            }
            catch (TimeoutException)
            {
                return new MailAttachment(a.FileName, a.MimeType, size, null, "Reading the PDF took too long. Omabilo will try again.", AttachmentStatus.TimedOut);
            }
        }

        if (result.Status == AttachmentStatus.ScannedNotRead && (pdf || image))
        {
            if (!ocrAllowed)
            {
                var hint = ocr == null ? " OCR isn't available on this server." : " Turn on AI reading in Settings to let Omabilo read scans with OCR.";
                return new MailAttachment(a.FileName, a.MimeType, size, null, (result.Problem ?? "No readable text.") + hint, AttachmentStatus.ScannedNotRead, result.Pages);
            }

            try
            {
                var text = await ocr!.ReadAsync(bytes, pdf ? "application/pdf" : a.MimeType, ct).WaitAsync(options.AttachmentTimeout * 2, ct);
                return text.Count(char.IsLetter) >= PdfAttachmentReader.MinTextLetters
                    ? new MailAttachment(a.FileName, a.MimeType, size, text.Length > PdfAttachmentReader.MaxChars ? text[..PdfAttachmentReader.MaxChars] : text, null, AttachmentStatus.ReadByOcr, result.Pages)
                    : new MailAttachment(a.FileName, a.MimeType, size, null, "OCR found no readable text in it.", AttachmentStatus.ScannedNotRead, result.Pages);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log($"ocr failed: {ex.GetType().Name}");
                return new MailAttachment(a.FileName, a.MimeType, size, null, "OCR couldn't read it this time. Omabilo will try again.", AttachmentStatus.TimedOut, result.Pages);
            }
        }

        return new MailAttachment(a.FileName, a.MimeType, size, result.Text, result.Problem, result.Status, result.Pages);
    }

    /// <summary>Schedules another read when an attachment failed for a passing reason, at most
    /// MaxAttachmentRetries times, 15 minutes doubling; clears the plan once everything was read.</summary>
    private FinancialItem WithRetryPlan(FinancialItem item, FinancialItem? before)
    {
        if (!item.Attachments.Any(a => a.CanRetry))
        {
            return item with { AttachmentRetries = 0, AttachmentRetryAfterUtc = null };
        }

        var tries = (before?.AttachmentRetries ?? -1) + 1;
        return tries >= options.MaxAttachmentRetries
            ? item with { AttachmentRetries = tries, AttachmentRetryAfterUtc = null }
            : item with { AttachmentRetries = tries, AttachmentRetryAfterUtc = clock().AddMinutes(15 * Math.Pow(2, tries)) };
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
            var raw = await ai.AnalyzeAsync(AiGuard.BuildInput(item.Counterparty ?? RecordExtractor.SenderName(mail.From), mail.Subject, AiGuard.Redact(text)), ct);
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
                Evidence = [.. bill.Evidence, $"Payment confirmation from {item.Counterparty ?? "the sender"} on {InvoiceExplainer.Day(source.ReceivedUtc)}."],
                Sources = [.. bill.Sources, source with { Role = "payment confirmation" }],
                UpdatedUtc = now,
            };
            paid = paid with { Advice = InvoiceAnalysis.Advise(paid, now) };
            await store.SaveItemAsync(paid, ct);
            await store.AddAuditAsync(userId, "item_payment_confirmed", bill.Id, ct);
            return (paid, Outcome.Updated);
        }

        if (ItemRules.FindSameObligation(existing, item) is { } same)
        {
            var merged = ItemRules.MergeInto(same, item, now);
            merged = merged with { Advice = InvoiceAnalysis.Advise(merged, now) };
            await store.SaveItemAsync(merged, ct);
            await store.AddAuditAsync(userId, "item_merged", same.Id, ct);
            return (merged, Outcome.Updated);
        }

        if (ItemRules.FindSamePurchase(existing, item) is { } purchase)
        {
            var merged = purchase with
            {
                Sources = purchase.Sources.Any(s => s.GmailId == item.Sources[0].GmailId && s.AccountId == item.Sources[0].AccountId)
                    ? purchase.Sources : [.. purchase.Sources, item.Sources[0] with { Role = "follow-up" }],
                Attachments = ItemRules.MergeAttachments(purchase.Attachments, item.Attachments),
                UpdatedUtc = now,
            };
            await store.SaveItemAsync(merged, ct);
            return (merged, Outcome.Updated);
        }

        if (ItemRules.FindProbableDuplicate(existing, item) is { } dup)
        {
            item = item with { PossibleDuplicateOf = dup.Id, Flags = [.. item.Flags, $"Looks like a duplicate of “{dup.Title}”. It isn't counted twice; keep it only if it's a separate {(item.IsIncome ? "payment" : "charge")}."] };
            item = item with { Advice = InvoiceAnalysis.Advise(item, now) };
        }

        await store.SaveItemAsync(item, ct);
        await store.AddAuditAsync(userId, "item_created", item.Id, ct);
        return (item, Outcome.Created);
    }

    // ── Reading items again ─────────────────────────────────────────────────

    /// <summary>After a sync: retries attachments that failed for a passing reason, and re-reads items
    /// made by older reading rules (once each), a few per run so a sync stays short.</summary>
    private async Task<int> RefreshItemsAsync(string userId, string accountId, string token, string? ownerEmail, CompanionSettings settings, AiBudget budget, CancellationToken ct)
    {
        var now = clock();

        // Only records from this account: another account's messages can't be fetched with this token
        var items = (await store.GetItemsAsync(userId, ct)).Where(i => AccountOf(i) == accountId).ToList();
        var due = items
            .Where(i => i.AttachmentRetryAfterUtc is { } at && at <= now && i.AttachmentRetries < options.MaxAttachmentRetries)
            .Concat(items.Where(i => i.AnalysisVersion < InvoiceAnalysis.CurrentVersion && CanReread(i)))
            .DistinctBy(i => i.Id)
            .Take(options.RefreshBatch)
            .ToList();
        var done = 0;

        foreach (var old in due)
        {
            if (ct.IsCancellationRequested) break;

            if (done > 0)
            {
                await _wait(options.MessagePause, ct);
            }

            try
            {
                await RereadAsync(userId, accountId, token, old, ownerEmail, settings, budget, ct, mayRemove: true);
                done++;
            }
            catch (Exception ex) when (ex is not (GoogleAuthException or OperationCanceledException) && !(ex is GmailApiException { Status: 401 }))
            {
                // Never loop on an item: it keeps its current reading and is marked as tried
                log($"sync: re-reading item {old.Id} for user {userId} failed with {ex.GetType().Name}");
                await store.SaveItemAsync(WithRetryPlan(old, old) with { AnalysisVersion = InvoiceAnalysis.CurrentVersion }, ct);
            }
        }

        return done;
    }

    /// <summary>True when the item came from Gmail; a forwarded email or uploaded file isn't kept, so it can't be read again.</summary>
    /// <summary>Nothing the person did is on it: no decision, edit or note.</summary>
    private static bool Untouched(FinancialItem item) =>
        item.UserState == UserState.None && item.EditedFields.Count == 0 && string.IsNullOrWhiteSpace(item.UserNote);

    public static bool CanReread(FinancialItem item) => item.Sources.Any(s => !BillUpload.NotInGmail(s.GmailId));

    /// <summary>The Gmail account a record's original message is in, or null when it didn't come from Gmail.</summary>
    public static string? AccountOf(FinancialItem item)
    {
        var gmail = item.Sources.Where(s => !BillUpload.NotInGmail(s.GmailId)).ToList();
        return (gmail.FirstOrDefault(s => s.Role == "original") ?? gmail.FirstOrDefault())?.AccountId;
    }

    /// <summary>Reads an item's messages again with the current rules, keeping the person's edits and decisions.</summary>
    /// <summary>Adds an AI explanation to a saved record from what Omabilo kept of it (the lines read from
    /// its document and the saved details), with no mailbox needed: uploads, pasted text and forwarded
    /// mail can be explained after the fact. Null when the record isn't this person's or AI reading is off.</summary>
    public async Task<FinancialItem?> ExplainSavedAsync(string userId, string itemId, CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync(userId, ct);

        if (await store.GetItemAsync(userId, itemId, ct) is not { } item || !settings.AiEnabled || ai == null)
        {
            return null;
        }

        var lines = new List<string>();
        if (item.Counterparty is { } company) lines.Add($"Company: {company}");
        if (item.Amount is { } amount) lines.Add($"Amount: {InvoiceExplainer.Money(amount)}");
        if (item.DueDate is { } due) lines.Add($"Due date: {InvoiceExplainer.Day(due)}");
        if (item.InvoiceNumber is { } number) lines.Add($"Invoice number: {number}");
        if (item.Reference is { } reference) lines.Add($"Reference: {reference}");
        if (item.Period is { } period) lines.Add($"Period: {period}");
        lines.AddRange(item.Sources.Select(s => s.Excerpt).Where(e => e.Length > 0));

        var first = item.Sources.FirstOrDefault();
        var mail = new MailMessage(first?.GmailId ?? item.Id, first?.GmailId ?? item.Id, first?.From ?? "", first?.Subject ?? item.Title,
            first?.ReceivedUtc ?? item.CreatedUtc, "", string.Join("\n", lines), []);
        var explained = await ExplainAsync(item, mail, settings, new AiBudget(1), ct);

        if (explained.Ai == null)
        {
            return explained;
        }

        explained = explained with { UpdatedUtc = clock() };
        await store.SaveItemAsync(explained, ct);
        await store.AddAuditAsync(userId, "item_explained", itemId, ct);
        return explained;
    }

    public async Task<FinancialItem?> ReprocessAsync(string userId, string itemId, CancellationToken ct)
    {
        // Read again through the account the message is in; when that account is disconnected, it can't be
        if (await store.GetItemAsync(userId, itemId, ct) is not { } old || AccountOf(old) is not { } accountId
            || await store.GetConnectionAsync(userId, accountId, ct) is not { Status: not ConnectionStatus.NeedsReconnect } c)
        {
            return null;
        }

        var (token, _) = await AccessTokenAsync(c, ct);
        var settings = await store.GetSettingsAsync(userId, ct);
        var result = await RereadAsync(userId, accountId, token, old, c.GoogleEmail, settings, new AiBudget(1), ct);
        await store.AddAuditAsync(userId, "item_reprocessed", itemId, ct);
        return result;
    }

    /// <param name="mayRemove">On the background re-read only: an item the current rules no longer find
    /// financial (a newsletter an older version filed) is removed, unless the person has touched it.</param>
    private async Task<FinancialItem> RereadAsync(string userId, string accountId, string token, FinancialItem old, string? ownerEmail, CompanionSettings settings, AiBudget budget,
        CancellationToken ct, bool mayRemove = false)
    {
        var notFinancialNow = false;

        // A merged message from another of the person's accounts can't be read with this account's token;
        // what it said earlier stays on the record
        var gmailSources = old.Sources.Where(s => !BillUpload.NotInGmail(s.GmailId) && s.AccountId == accountId).ToList();
        var original = gmailSources.FirstOrDefault(s => s.Role == "original") ?? gmailSources.FirstOrDefault();

        if (original == null)
        {
            return old;
        }

        async Task<(FinancialItem Item, MailMessage Mail)> ReadSourceAsync(SourceRef source)
        {
            var full = await Retry.GmailAsync(() => gmail.GetFullAsync(token, source.GmailId, ct), ct, delay: delay);
            var mail = await ToMailAsync(token, full, settings, ct);

            // An item split from a multi-invoice email is read from its own document
            if (source.AttachmentName != null && mail.Attachments.Count(a => !string.IsNullOrWhiteSpace(a.Text)) > 1)
            {
                mail = OnlyDocument(mail, source.AttachmentName);
            }

            var cls = Classify(mail);

            if (!cls.IsFinancial && source == original && UnreadDocument(mail) == null)
            {
                notFinancialNow = true;
            }

            // A document that still can't be read keeps the item as it was classified before
            if (!cls.IsFinancial)
            {
                cls = new Classification(true, old.Kind, old.Confidence, old.ClassificationReasons, old.ClassifiedBy);
            }

            var fresh = RecordExtractor.Extract(userId, mail, cls, clock(), ownerEmail);
            return (cls.Method == "rules (document not read)" && fresh.Amount == null ? fresh with { Title = old.Title } : fresh, mail);
        }

        var (fresh, firstMail) = await ReadSourceAsync(original);

        if (mayRemove && notFinancialNow && Untouched(old))
        {
            await store.DeleteItemAsync(userId, old.Id, ct);

            foreach (var s in gmailSources)
            {
                await store.SaveMessageAsync(new ProcessedMessage(userId, Mailboxes.MessageKey(s.AccountId, s.GmailId), s.ReceivedUtc, false, FinKind.NotFinancial, null, null, clock()), ct);
            }

            await store.AddAuditAsync(userId, "item_removed_not_financial", old.Id, ct);
            log($"sync: item {old.Id} for user {userId} removed: not financial under the current rules");
            return old;
        }

        // Reminders that were merged in are merged again, so a re-read keeps the latest amount and due date
        foreach (var reminder in gmailSources.Where(s => s.Role == "reminder" && s.GmailId != original.GmailId))
        {
            try
            {
                var (later, _) = await ReadSourceAsync(reminder);
                fresh = ItemRules.MergeInto(fresh, later, clock());
            }
            catch (GmailApiException ex) when (ex.Status == 404)
            {
                // The reminder was deleted from Gmail; what it said earlier stays on the item
            }
        }

        fresh = await ExplainAsync(fresh, firstMail, settings, budget, ct);

        var result = CompanionEdits.KeepEdits(old, fresh with
        {
            Id = old.Id,
            CreatedUtc = old.CreatedUtc,
            UpdatedUtc = clock(),
            UserState = old.UserState is UserState.None && fresh.UserState != UserState.None ? fresh.UserState : old.UserState,
            UserNote = old.UserNote,
            Sources = old.Sources,
            PossibleDuplicateOf = old.PossibleDuplicateOf,
            Evidence = old.Evidence.Concat(fresh.Evidence).Distinct().ToList(),
            Ai = fresh.Ai ?? old.Ai,
        });

        result = WithRetryPlan(result, old);
        result = result with { Advice = InvoiceAnalysis.Advise(result, clock()) };
        await store.SaveItemAsync(result, ct);
        return result;
    }
}
