// Storage for the connected companion. Every method takes the owner's user id and every query
// filters on it, so a record id from one person can never read or change another person's data.
// PostgresCompanionStore runs on the app's built-in IKON Postgres database through plain ADO.NET.

using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;

public interface ICompanionStore
{
    /// <summary>Every Gmail account the person has connected: the main one first, then oldest first.</summary>
    Task<IReadOnlyList<GmailConnection>> GetConnectionsAsync(string userId, CancellationToken ct);
    Task<GmailConnection?> GetConnectionAsync(string userId, string accountId, CancellationToken ct);
    Task SaveConnectionAsync(GmailConnection connection, CancellationToken ct);

    /// <summary>Deletes one account's tokens and watch. The records it brought in stay.</summary>
    Task DeleteConnectionAsync(string userId, string accountId, CancellationToken ct);
    Task<IReadOnlyList<MailboxRef>> DueConnectionsAsync(DateTime now, int max, CancellationToken ct);

    /// <summary>Takes the per-mailbox sync lock, so two runs (cron and "Sync now") never overlap.</summary>
    Task<bool> TryClaimSyncAsync(MailboxRef mailbox, DateTime now, DateTime until, CancellationToken ct);
    Task ReleaseSyncAsync(MailboxRef mailbox, CancellationToken ct);

    Task SaveStateAsync(OAuthState state, CancellationToken ct);

    /// <summary>Returns and deletes the state in one step, so a callback can't be replayed.</summary>
    Task<OAuthState?> TakeStateAsync(string state, DateTime now, CancellationToken ct);

    Task<ProcessedMessage?> GetMessageAsync(string userId, string gmailId, CancellationToken ct);
    Task SaveMessageAsync(ProcessedMessage message, CancellationToken ct);

    Task<IReadOnlyList<FinancialItem>> GetItemsAsync(string userId, CancellationToken ct);
    Task<FinancialItem?> GetItemAsync(string userId, string itemId, CancellationToken ct);
    Task SaveItemAsync(FinancialItem item, CancellationToken ct);
    Task<bool> DeleteItemAsync(string userId, string itemId, CancellationToken ct);

    Task<CompanionSettings> GetSettingsAsync(string userId, CancellationToken ct);
    Task SaveSettingsAsync(string userId, CompanionSettings settings, CancellationToken ct);

    /// <summary>What happened, never what a message said: no amounts, text or tokens.</summary>
    Task AddAuditAsync(string userId, string action, string? itemId, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string userId, int max, CancellationToken ct);

    Task DeleteAllForUserAsync(string userId, CancellationToken ct);

    // ── Gmail push (users.watch + Pub/Sub) ──

    Task<GmailWatch?> GetWatchAsync(MailboxRef mailbox, CancellationToken ct);
    Task SaveWatchAsync(GmailWatch watch, CancellationToken ct);

    /// <summary>Connected mailboxes with this address (a notification names only the mailbox).</summary>
    Task<IReadOnlyList<MailboxRef>> MailboxesForGmailAsync(string googleEmail, CancellationToken ct);

    /// <summary>Makes this mailbox due now, so the next sweep picks it up if the push-triggered run can't.</summary>
    Task MarkSyncDueAsync(MailboxRef mailbox, DateTime now, CancellationToken ct);

    /// <summary>Totals for the health check: how many watches are live and when the last notice came. No user data.</summary>
    Task<PushOverview> PushOverviewAsync(DateTime now, CancellationToken ct);

    /// <summary>Retention: expired sign-in states, old non-financial message ids and old audit rows.</summary>
    Task PruneAsync(DateTime now, CancellationToken ct);

    /// <summary>Records a message as taken in one step; false when it was already taken, so a trigger
    /// and a sweep reaching the same email never both file it.</summary>
    Task<bool> TryClaimMessageAsync(ProcessedMessage message, CancellationToken ct);

    // ── Forwarding addresses ──

    Task<ForwardAddress?> GetForwardAsync(string userId, CancellationToken ct);

    /// <summary>The person whose address this token is, or null.</summary>
    Task<ForwardAddress?> FindForwardAsync(string token, CancellationToken ct);

    /// <summary>Keeps an existing address (two tabs opening at once get the same one), else stores this one.</summary>
    Task<ForwardAddress> GetOrCreateForwardAsync(ForwardAddress candidate, CancellationToken ct);

    Task SaveForwardAsync(ForwardAddress address, CancellationToken ct);

    // ── Profile picture and cover image (the file itself is in private file storage) ──

    Task<IReadOnlyList<ProfileImage>> GetProfileImagesAsync(string userId, CancellationToken ct);
    Task<ProfileImage?> GetProfileImageAsync(string userId, ProfileImageKind kind, CancellationToken ct);
    Task SaveProfileImageAsync(ProfileImage image, CancellationToken ct);
    Task<bool> DeleteProfileImageAsync(string userId, ProfileImageKind kind, CancellationToken ct);

    // ── The person's own debts and savings goals ──

    Task<IReadOnlyList<DebtRecord>> GetDebtsAsync(string userId, CancellationToken ct);
    Task SaveDebtAsync(DebtRecord debt, CancellationToken ct);
    Task<bool> DeleteDebtAsync(string userId, string debtId, CancellationToken ct);
    Task<IReadOnlyList<GoalRecord>> GetGoalsAsync(string userId, CancellationToken ct);
    Task SaveGoalAsync(GoalRecord goal, CancellationToken ct);

    /// <summary>Deletes the goal and its deposits and withdrawals.</summary>
    Task<bool> DeleteGoalAsync(string userId, string goalId, CancellationToken ct);
    Task<IReadOnlyList<Contribution>> GetContributionsAsync(string userId, CancellationToken ct);
    Task SaveContributionAsync(Contribution contribution, CancellationToken ct);
    Task<bool> DeleteContributionAsync(string userId, string contributionId, CancellationToken ct);
}

public sealed record AuditEntry(DateTime AtUtc, string Action, string? ItemId);

public sealed record PushOverview(int ActiveWatches, int WatchErrors, DateTime? LastPushUtc, long Pushes, long DuplicatePushes);

public static class CompanionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(), new PostgresSafeString() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Postgres refuses a NUL character (and a half surrogate pair) in jsonb, and PDF text can
    /// carry them: they are dropped when saving, so one odd document never fails its email.</summary>
    private sealed class PostgresSafeString : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Clean(value));
    }

    public static string Clean(string value)
    {
        if (!value.Any(c => c == '\0' || char.IsSurrogate(c)))
        {
            return value;
        }

        var sb = new System.Text.StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c == '\0')
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(c).Append(value[++i]);
            }
            else if (!char.IsSurrogate(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

public sealed class PostgresCompanionStore(Func<CancellationToken, Task<DbConnection>> open) : ICompanionStore
{
    public const int NonFinancialRetentionDays = 180;
    public const int AuditRetentionDays = 365;

    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static bool _schemaReady;

    public const string Schema =
        """
        -- One row per person: the only Gmail account before multi-account support. Kept, and kept in
        -- step with each person's main account, so the older build can still be rolled back to.
        create table if not exists om_connections (
            user_id text primary key,
            google_email text,
            granted_scopes text not null default '',
            refresh_token_enc text,
            access_token_enc text,
            access_expires_utc timestamptz,
            history_id text,
            status text not null,
            last_sync_utc timestamptz,
            last_success_utc timestamptz,
            previous_success_utc timestamptz,
            last_error text,
            failure_count int not null default 0,
            next_attempt_utc timestamptz not null,
            sync_lock_until_utc timestamptz,
            last_sync_new_items int not null default 0,
            last_sync_scanned int not null default 0,
            initial_scan_done boolean not null default false,
            connected_utc timestamptz not null
        );
        create index if not exists om_connections_due on om_connections (next_attempt_utc);
        create table if not exists om_oauth_states (
            state text primary key,
            user_id text not null,
            code_verifier text not null,
            redirect_uri text not null,
            expires_utc timestamptz not null
        );
        create table if not exists om_messages (
            user_id text not null,
            gmail_id text not null,
            received_utc timestamptz not null,
            is_financial boolean not null,
            kind text not null,
            item_id text,
            error text,
            processed_utc timestamptz not null,
            primary key (user_id, gmail_id)
        );
        create table if not exists om_items (
            id text primary key,
            user_id text not null,
            kind text not null,
            data jsonb not null,
            created_utc timestamptz not null,
            updated_utc timestamptz not null
        );
        create index if not exists om_items_user on om_items (user_id, updated_utc desc);
        create table if not exists om_debts (
            id text primary key,
            user_id text not null,
            data jsonb not null,
            updated_utc timestamptz not null
        );
        create index if not exists om_debts_user on om_debts (user_id);
        create table if not exists om_goals (
            id text primary key,
            user_id text not null,
            data jsonb not null,
            updated_utc timestamptz not null
        );
        create index if not exists om_goals_user on om_goals (user_id);
        create table if not exists om_contributions (
            id text primary key,
            user_id text not null,
            goal_id text not null,
            data jsonb not null,
            updated_utc timestamptz not null
        );
        create index if not exists om_contributions_user on om_contributions (user_id, goal_id);
        create table if not exists om_settings (
            user_id text primary key,
            data jsonb not null,
            updated_utc timestamptz not null
        );
        create table if not exists om_audit (
            id bigserial primary key,
            user_id text not null,
            at_utc timestamptz not null,
            action text not null,
            item_id text
        );
        create index if not exists om_audit_user on om_audit (user_id, at_utc desc);
        create table if not exists om_gmail_watch (
            user_id text primary key,
            topic text,
            expires_utc timestamptz,
            renewed_utc timestamptz,
            last_error text,
            last_push_utc timestamptz,
            last_push_history_id numeric(20) not null default 0,
            push_count bigint not null default 0,
            duplicate_push_count bigint not null default 0,
            last_push_sync_started_utc timestamptz
        );
        create index if not exists om_connections_email on om_connections (lower(google_email));
        create table if not exists om_gmail_accounts (
            user_id text not null,
            account_id text not null,
            google_email text,
            granted_scopes text not null default '',
            refresh_token_enc text,
            access_token_enc text,
            access_expires_utc timestamptz,
            history_id text,
            status text not null,
            last_sync_utc timestamptz,
            last_success_utc timestamptz,
            previous_success_utc timestamptz,
            last_error text,
            failure_count int not null default 0,
            next_attempt_utc timestamptz not null,
            sync_lock_until_utc timestamptz,
            last_sync_new_items int not null default 0,
            last_sync_scanned int not null default 0,
            initial_scan_done boolean not null default false,
            connected_utc timestamptz not null,
            primary key (user_id, account_id)
        );
        create unique index if not exists om_gmail_accounts_address on om_gmail_accounts (user_id, lower(google_email));
        create index if not exists om_gmail_accounts_due on om_gmail_accounts (next_attempt_utc);
        create index if not exists om_gmail_accounts_email on om_gmail_accounts (lower(google_email));
        insert into om_gmail_accounts (user_id, account_id, google_email, granted_scopes, refresh_token_enc, access_token_enc, access_expires_utc, history_id,
            status, last_sync_utc, last_success_utc, previous_success_utc, last_error, failure_count, next_attempt_utc, last_sync_new_items, last_sync_scanned,
            initial_scan_done, connected_utc)
        select user_id, 'main', google_email, granted_scopes, refresh_token_enc, access_token_enc, access_expires_utc, history_id,
            status, last_sync_utc, last_success_utc, previous_success_utc, last_error, failure_count, next_attempt_utc, last_sync_new_items, last_sync_scanned,
            initial_scan_done, connected_utc
        from om_connections
        on conflict do nothing;
        create table if not exists om_profile_images (
            user_id text not null,
            kind text not null,
            path text not null,
            mime_type text not null,
            size_bytes bigint not null,
            focus_y int not null default 50,
            updated_utc timestamptz not null,
            primary key (user_id, kind)
        );
        create table if not exists om_forward_addresses (
            user_id text primary key,
            token text not null unique,
            data jsonb not null,
            updated_utc timestamptz not null
        );
        """;

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = await open(ct);

        if (!_schemaReady)
        {
            await SchemaLock.WaitAsync(ct);

            try
            {
                if (!_schemaReady)
                {
                    await ExecAsync(conn, Schema, ct);
                    _schemaReady = true;
                }
            }
            finally
            {
                SchemaLock.Release();
            }
        }

        return conn;
    }

    // ── Connections ─────────────────────────────────────────────────────────

    // Gmail accounts live in om_gmail_accounts, one row per account. The schema copies each person's
    // single om_connections row in once as their "main" account; saves and deletes of the main account
    // are mirrored back there, so the build before multi-account support still finds it after a rollback.

    private const string AccountColumns =
        "user_id, account_id, google_email, granted_scopes, refresh_token_enc, access_token_enc, access_expires_utc, history_id, status, last_sync_utc, " +
        "last_success_utc, previous_success_utc, last_error, failure_count, next_attempt_utc, sync_lock_until_utc, last_sync_new_items, last_sync_scanned, " +
        "initial_scan_done, connected_utc";

    public async Task<IReadOnlyList<GmailConnection>> GetConnectionsAsync(string userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, $"select {AccountColumns} from om_gmail_accounts where user_id = @u order by account_id <> 'main', connected_utc, account_id", ("u", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<GmailConnection>();

        while (await r.ReadAsync(ct))
        {
            list.Add(ReadConnection(r));
        }

        return list;
    }

    public async Task<GmailConnection?> GetConnectionAsync(string userId, string accountId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, $"select {AccountColumns} from om_gmail_accounts where user_id = @u and account_id = @a", ("u", userId), ("a", accountId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadConnection(r) : null;
    }

    private static GmailConnection ReadConnection(DbDataReader r) => new()
    {
        UserId = r.GetString(r.GetOrdinal("user_id")),
        AccountId = r.GetString(r.GetOrdinal("account_id")),
        GoogleEmail = Str(r, "google_email"),
        GrantedScopes = Str(r, "granted_scopes") ?? "",
        RefreshTokenEnc = Str(r, "refresh_token_enc"),
        AccessTokenEnc = Str(r, "access_token_enc"),
        AccessExpiresUtc = Time(r, "access_expires_utc"),
        HistoryId = Str(r, "history_id"),
        Status = Enum.TryParse<ConnectionStatus>(Str(r, "status"), out var s) ? s : ConnectionStatus.Error,
        LastSyncUtc = Time(r, "last_sync_utc"),
        LastSuccessUtc = Time(r, "last_success_utc"),
        PreviousSuccessUtc = Time(r, "previous_success_utc"),
        LastError = Str(r, "last_error"),
        FailureCount = r.GetInt32(r.GetOrdinal("failure_count")),
        NextAttemptUtc = Time(r, "next_attempt_utc") ?? DateTime.UtcNow,
        SyncLockUntilUtc = Time(r, "sync_lock_until_utc"),
        LastSyncNewItems = r.GetInt32(r.GetOrdinal("last_sync_new_items")),
        LastSyncScanned = r.GetInt32(r.GetOrdinal("last_sync_scanned")),
        InitialScanDone = r.GetBoolean(r.GetOrdinal("initial_scan_done")),
        ConnectedUtc = Time(r, "connected_utc") ?? DateTime.UtcNow,
    };

    private const string SaveColumns =
        "google_email, granted_scopes, refresh_token_enc, access_token_enc, access_expires_utc, history_id, status, last_sync_utc, last_success_utc, " +
        "previous_success_utc, last_error, failure_count, next_attempt_utc, last_sync_new_items, last_sync_scanned, initial_scan_done, connected_utc";

    private const string SaveValues = "@email, @scopes, @rt, @at, @exp, @hist, @status, @ls, @lsu, @psu, @err, @fails, @next, @newi, @scanned, @init, @connected";

    // The sync lock is never part of a save: only TryClaim and Release move it
    private const string SaveUpdates =
        """
        google_email = excluded.google_email, granted_scopes = excluded.granted_scopes,
        refresh_token_enc = excluded.refresh_token_enc, access_token_enc = excluded.access_token_enc, access_expires_utc = excluded.access_expires_utc,
        history_id = excluded.history_id, status = excluded.status, last_sync_utc = excluded.last_sync_utc, last_success_utc = excluded.last_success_utc,
        previous_success_utc = excluded.previous_success_utc, last_error = excluded.last_error, failure_count = excluded.failure_count,
        next_attempt_utc = excluded.next_attempt_utc, last_sync_new_items = excluded.last_sync_new_items, last_sync_scanned = excluded.last_sync_scanned,
        initial_scan_done = excluded.initial_scan_done, connected_utc = excluded.connected_utc
        """;

    public async Task SaveConnectionAsync(GmailConnection c, CancellationToken ct)
    {
        var sql = $"insert into om_gmail_accounts (user_id, account_id, {SaveColumns}) values (@u, @a, {SaveValues}) on conflict (user_id, account_id) do update set {SaveUpdates};";

        if (Mailboxes.IsMain(c.AccountId))
        {
            sql += $"insert into om_connections (user_id, {SaveColumns}) values (@u, {SaveValues}) on conflict (user_id) do update set {SaveUpdates};";
        }

        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, sql,
            ("u", c.UserId), ("a", c.AccountId), ("email", c.GoogleEmail), ("scopes", c.GrantedScopes), ("rt", c.RefreshTokenEnc), ("at", c.AccessTokenEnc),
            ("exp", Utc(c.AccessExpiresUtc)), ("hist", c.HistoryId), ("status", c.Status.ToString()), ("ls", Utc(c.LastSyncUtc)), ("lsu", Utc(c.LastSuccessUtc)),
            ("psu", Utc(c.PreviousSuccessUtc)), ("err", c.LastError), ("fails", c.FailureCount), ("next", Utc(c.NextAttemptUtc)), ("newi", c.LastSyncNewItems),
            ("scanned", c.LastSyncScanned), ("init", c.InitialScanDone), ("connected", Utc(c.ConnectedUtc)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteConnectionAsync(string userId, string accountId, CancellationToken ct)
    {
        var sql = "delete from om_gmail_watch where user_id = @k; delete from om_gmail_accounts where user_id = @u and account_id = @a;";

        if (Mailboxes.IsMain(accountId))
        {
            sql += "delete from om_connections where user_id = @u;";
        }

        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, sql, ("k", Mailboxes.Key(userId, accountId)), ("u", userId), ("a", accountId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // A further account's watch row is keyed "user#account"; the main account's keeps the bare user id
    public async Task<GmailWatch?> GetWatchAsync(MailboxRef mailbox, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select * from om_gmail_watch where user_id = @u", ("u", mailbox.ToString()));
        await using var r = await cmd.ExecuteReaderAsync(ct);

        if (!await r.ReadAsync(ct))
        {
            return null;
        }

        return new GmailWatch
        {
            UserId = mailbox.UserId,
            AccountId = mailbox.AccountId,
            Topic = Str(r, "topic"),
            ExpiresUtc = Time(r, "expires_utc"),
            RenewedUtc = Time(r, "renewed_utc"),
            LastError = Str(r, "last_error"),
            LastPushUtc = Time(r, "last_push_utc"),
            LastPushHistoryId = Convert.ToUInt64(r.GetValue(r.GetOrdinal("last_push_history_id"))),
            PushCount = r.GetInt64(r.GetOrdinal("push_count")),
            DuplicatePushCount = r.GetInt64(r.GetOrdinal("duplicate_push_count")),
            LastPushSyncStartedUtc = Time(r, "last_push_sync_started_utc"),
        };
    }

    public async Task SaveWatchAsync(GmailWatch w, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            insert into om_gmail_watch (user_id, topic, expires_utc, renewed_utc, last_error, last_push_utc, last_push_history_id, push_count, duplicate_push_count, last_push_sync_started_utc)
            values (@u, @topic, @exp, @ren, @err, @push, @hist, @n, @dup, @started)
            on conflict (user_id) do update set topic = excluded.topic, expires_utc = excluded.expires_utc, renewed_utc = excluded.renewed_utc,
                last_error = excluded.last_error, last_push_utc = excluded.last_push_utc, last_push_history_id = excluded.last_push_history_id,
                push_count = excluded.push_count, duplicate_push_count = excluded.duplicate_push_count, last_push_sync_started_utc = excluded.last_push_sync_started_utc
            """,
            ("u", Mailboxes.Key(w.UserId, w.AccountId)), ("topic", w.Topic), ("exp", Utc(w.ExpiresUtc)), ("ren", Utc(w.RenewedUtc)), ("err", w.LastError), ("push", Utc(w.LastPushUtc)),
            ("hist", (decimal)w.LastPushHistoryId), ("n", w.PushCount), ("dup", w.DuplicatePushCount), ("started", Utc(w.LastPushSyncStartedUtc)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MailboxRef>> MailboxesForGmailAsync(string googleEmail, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            "select user_id, account_id from om_gmail_accounts where lower(google_email) = lower(@e) and refresh_token_enc is not null and status <> 'NeedsReconnect'",
            ("e", googleEmail));
        return await ReadRefsAsync(cmd, ct);
    }

    private static async Task<IReadOnlyList<MailboxRef>> ReadRefsAsync(DbCommand cmd, CancellationToken ct)
    {
        var list = new List<MailboxRef>();
        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            list.Add(new MailboxRef(r.GetString(0), r.GetString(1)));
        }

        return list;
    }

    public async Task MarkSyncDueAsync(MailboxRef m, DateTime now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "update om_gmail_accounts set next_attempt_utc = least(next_attempt_utc, @now) where user_id = @u and account_id = @a",
            ("u", m.UserId), ("a", m.AccountId), ("now", Utc(now)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PushOverview> PushOverviewAsync(DateTime now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            select count(*) filter (where expires_utc > @now and last_error is null), count(*) filter (where last_error is not null),
                   max(last_push_utc), coalesce(sum(push_count), 0), coalesce(sum(duplicate_push_count), 0)
            from om_gmail_watch
            """,
            ("now", Utc(now)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new PushOverview(Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(1)),
            r.IsDBNull(2) ? null : DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), Convert.ToInt64(r.GetValue(3)), Convert.ToInt64(r.GetValue(4)));
    }

    public async Task<IReadOnlyList<MailboxRef>> DueConnectionsAsync(DateTime now, int max, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            select user_id, account_id from om_gmail_accounts
            where status in ('Connected', 'Error', 'Syncing') and refresh_token_enc is not null and next_attempt_utc <= @now
              and (sync_lock_until_utc is null or sync_lock_until_utc < @now)
            order by next_attempt_utc limit @max
            """,
            ("now", Utc(now)), ("max", max));
        return await ReadRefsAsync(cmd, ct);
    }

    public async Task<bool> TryClaimSyncAsync(MailboxRef m, DateTime now, DateTime until, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            "update om_gmail_accounts set sync_lock_until_utc = @until where user_id = @u and account_id = @a and (sync_lock_until_utc is null or sync_lock_until_utc < @now)",
            ("until", Utc(until)), ("u", m.UserId), ("a", m.AccountId), ("now", Utc(now)));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task ReleaseSyncAsync(MailboxRef m, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "update om_gmail_accounts set sync_lock_until_utc = null where user_id = @u and account_id = @a", ("u", m.UserId), ("a", m.AccountId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── OAuth state ─────────────────────────────────────────────────────────

    public async Task SaveStateAsync(OAuthState s, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            "insert into om_oauth_states (state, user_id, code_verifier, redirect_uri, expires_utc) values (@s, @u, @v, @r, @e)",
            ("s", s.State), ("u", s.UserId), ("v", s.CodeVerifier), ("r", s.RedirectUri), ("e", Utc(s.ExpiresUtc)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<OAuthState?> TakeStateAsync(string state, DateTime now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            "delete from om_oauth_states where state = @s returning user_id, code_verifier, redirect_uri, expires_utc",
            ("s", state));
        await using var r = await cmd.ExecuteReaderAsync(ct);

        if (!await r.ReadAsync(ct))
        {
            return null;
        }

        var expires = DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc);
        return expires < now ? null : new OAuthState(state, r.GetString(0), r.GetString(1), r.GetString(2), expires);
    }

    // ── Messages ────────────────────────────────────────────────────────────

    public async Task<ProcessedMessage?> GetMessageAsync(string userId, string gmailId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select received_utc, is_financial, kind, item_id, error, processed_utc from om_messages where user_id = @u and gmail_id = @g",
            ("u", userId), ("g", gmailId));
        await using var r = await cmd.ExecuteReaderAsync(ct);

        if (!await r.ReadAsync(ct))
        {
            return null;
        }

        return new ProcessedMessage(userId, gmailId, DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc), r.GetBoolean(1),
            Enum.TryParse<FinKind>(r.GetString(2), out var k) ? k : FinKind.NotFinancial,
            r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc));
    }

    public async Task SaveMessageAsync(ProcessedMessage m, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            insert into om_messages (user_id, gmail_id, received_utc, is_financial, kind, item_id, error, processed_utc)
            values (@u, @g, @rec, @fin, @kind, @item, @err, @proc)
            on conflict (user_id, gmail_id) do update set is_financial = excluded.is_financial, kind = excluded.kind,
                item_id = excluded.item_id, error = excluded.error, processed_utc = excluded.processed_utc
            """,
            ("u", m.UserId), ("g", m.GmailId), ("rec", Utc(m.ReceivedUtc)), ("fin", m.IsFinancial), ("kind", m.Kind.ToString()),
            ("item", m.ItemId), ("err", m.Error), ("proc", Utc(m.ProcessedUtc)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Items ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FinancialItem>> GetItemsAsync(string userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select data::text from om_items where user_id = @u order by updated_utc desc", ("u", userId));
        var list = new List<FinancialItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            if (JsonSerializer.Deserialize<FinancialItem>(r.GetString(0), CompanionJson.Options) is { } item && item.UserId == userId)
            {
                list.Add(item);
            }
        }

        return list;
    }

    public async Task<FinancialItem?> GetItemAsync(string userId, string itemId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select data::text from om_items where user_id = @u and id = @id", ("u", userId), ("id", itemId));
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json == null ? null : JsonSerializer.Deserialize<FinancialItem>(json, CompanionJson.Options) is { } item && item.UserId == userId ? item : null;
    }

    public async Task SaveItemAsync(FinancialItem item, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        // The where clause keeps an id clash from ever overwriting another person's row
        await using var cmd = Cmd(conn,
            """
            insert into om_items (id, user_id, kind, data, created_utc, updated_utc)
            values (@id, @u, @kind, cast(@data as jsonb), @created, @updated)
            on conflict (id) do update set kind = excluded.kind, data = excluded.data, updated_utc = excluded.updated_utc
            where om_items.user_id = excluded.user_id
            """,
            ("id", item.Id), ("u", item.UserId), ("kind", item.Kind.ToString()), ("data", JsonSerializer.Serialize(item, CompanionJson.Options)),
            ("created", Utc(item.CreatedUtc)), ("updated", Utc(item.UpdatedUtc)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteItemAsync(string userId, string itemId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "delete from om_items where user_id = @u and id = @id", ("u", userId), ("id", itemId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ── Settings and audit ──────────────────────────────────────────────────

    public async Task<CompanionSettings> GetSettingsAsync(string userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select data::text from om_settings where user_id = @u", ("u", userId));
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json == null ? new CompanionSettings() : JsonSerializer.Deserialize<CompanionSettings>(json, CompanionJson.Options) ?? new CompanionSettings();
    }

    public async Task SaveSettingsAsync(string userId, CompanionSettings settings, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            insert into om_settings (user_id, data, updated_utc) values (@u, cast(@data as jsonb), @now)
            on conflict (user_id) do update set data = excluded.data, updated_utc = excluded.updated_utc
            """,
            ("u", userId), ("data", JsonSerializer.Serialize(settings, CompanionJson.Options)), ("now", DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAuditAsync(string userId, string action, string? itemId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "insert into om_audit (user_id, at_utc, action, item_id) values (@u, @at, @a, @i)",
            ("u", userId), ("at", DateTime.UtcNow), ("a", action), ("i", itemId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string userId, int max, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select at_utc, action, item_id from om_audit where user_id = @u order by at_utc desc limit @max", ("u", userId), ("max", max));
        var list = new List<AuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            list.Add(new AuditEntry(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }

        return list;
    }

    public async Task DeleteAllForUserAsync(string userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        foreach (var table in new[] { "om_items", "om_debts", "om_goals", "om_contributions", "om_messages", "om_settings", "om_audit", "om_oauth_states", "om_gmail_watch",
                     "om_gmail_accounts", "om_connections", "om_forward_addresses", "om_profile_images" })
        {
            await using var cmd = Cmd(conn, $"delete from {table} where user_id = @u", ("u", userId));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Further accounts' watch rows are keyed "user#account"
        await using (var watches = Cmd(conn, "delete from om_gmail_watch where left(user_id, length(@u) + 1) = @u || '#'", ("u", userId)))
        {
            await watches.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task PruneAsync(DateTime now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            delete from om_oauth_states where expires_utc < @now;
            delete from om_messages where is_financial = false and received_utc < @nf;
            delete from om_audit where at_utc < @audit;
            """,
            ("now", Utc(now)), ("nf", Utc(now.AddDays(-NonFinancialRetentionDays))), ("audit", Utc(now.AddDays(-AuditRetentionDays))));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryClaimMessageAsync(ProcessedMessage m, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            insert into om_messages (user_id, gmail_id, received_utc, is_financial, kind, item_id, error, processed_utc)
            values (@u, @g, @rec, @fin, @kind, @item, @err, @proc)
            on conflict (user_id, gmail_id) do nothing
            """,
            ("u", m.UserId), ("g", m.GmailId), ("rec", Utc(m.ReceivedUtc)), ("fin", m.IsFinancial), ("kind", m.Kind.ToString()),
            ("item", m.ItemId), ("err", m.Error), ("proc", Utc(m.ProcessedUtc)));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ── Forwarding addresses ────────────────────────────────────────────────

    public async Task<ForwardAddress?> GetForwardAsync(string userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select data::text from om_forward_addresses where user_id = @u", ("u", userId));
        return ReadForward(await cmd.ExecuteScalarAsync(ct) as string, userId);
    }

    public async Task<ForwardAddress?> FindForwardAsync(string token, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select user_id, data::text from om_forward_addresses where token = @t", ("t", token));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadForward(r.GetString(1), r.GetString(0)) : null;
    }

    public async Task<ForwardAddress> GetOrCreateForwardAsync(ForwardAddress candidate, CancellationToken ct)
    {
        await using (var conn = await OpenAsync(ct))
        {
            await using var cmd = Cmd(conn,
                "insert into om_forward_addresses (user_id, token, data, updated_utc) values (@u, @t, cast(@data as jsonb), @now) on conflict (user_id) do nothing",
                ("u", candidate.UserId), ("t", candidate.Token), ("data", JsonSerializer.Serialize(candidate, CompanionJson.Options)), ("now", DateTime.UtcNow));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return await GetForwardAsync(candidate.UserId, ct) ?? candidate;
    }

    public async Task SaveForwardAsync(ForwardAddress a, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            insert into om_forward_addresses (user_id, token, data, updated_utc) values (@u, @t, cast(@data as jsonb), @now)
            on conflict (user_id) do update set token = excluded.token, data = excluded.data, updated_utc = excluded.updated_utc
            """,
            ("u", a.UserId), ("t", a.Token), ("data", JsonSerializer.Serialize(a, CompanionJson.Options)), ("now", DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The row's user id wins over anything in the JSON
    private static ForwardAddress? ReadForward(string? json, string userId) =>
        json == null ? null : JsonSerializer.Deserialize<ForwardAddress>(json, CompanionJson.Options) is { } a ? a with { UserId = userId } : null;

    // ── Profile pictures ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProfileImage>> GetProfileImagesAsync(string userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select kind, path, mime_type, size_bytes, focus_y, updated_utc from om_profile_images where user_id = @u", ("u", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ProfileImage>();

        while (await r.ReadAsync(ct))
        {
            if (Enum.TryParse<ProfileImageKind>(r.GetString(0), out var kind))
            {
                list.Add(new ProfileImage(userId, kind, r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc)));
            }
        }

        return list;
    }

    public async Task<ProfileImage?> GetProfileImageAsync(string userId, ProfileImageKind kind, CancellationToken ct) =>
        (await GetProfileImagesAsync(userId, ct)).FirstOrDefault(i => i.Kind == kind);

    public async Task SaveProfileImageAsync(ProfileImage i, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            insert into om_profile_images (user_id, kind, path, mime_type, size_bytes, focus_y, updated_utc) values (@u, @k, @p, @m, @s, @f, @t)
            on conflict (user_id, kind) do update set path = excluded.path, mime_type = excluded.mime_type, size_bytes = excluded.size_bytes,
                focus_y = excluded.focus_y, updated_utc = excluded.updated_utc
            """,
            ("u", i.UserId), ("k", i.Kind.ToString()), ("p", i.Path), ("m", i.MimeType), ("s", i.SizeBytes), ("f", i.FocusY), ("t", Utc(i.UpdatedUtc)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteProfileImageAsync(string userId, ProfileImageKind kind, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "delete from om_profile_images where user_id = @u and kind = @k", ("u", userId), ("k", kind.ToString()));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ── ADO helpers ─────────────────────────────────────────────────────────

    private static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Debts and savings goals ─────────────────────────────────────────────

    public Task<IReadOnlyList<DebtRecord>> GetDebtsAsync(string userId, CancellationToken ct) => GetOwnedAsync<DebtRecord>("om_debts", userId, d => d.UserId, ct);

    public Task SaveDebtAsync(DebtRecord debt, CancellationToken ct) => SaveOwnedAsync("om_debts", debt.Id, debt.UserId, null, debt, debt.UpdatedUtc, ct);

    public Task<bool> DeleteDebtAsync(string userId, string debtId, CancellationToken ct) => DeleteOwnedAsync("om_debts", userId, debtId, ct);

    public Task<IReadOnlyList<GoalRecord>> GetGoalsAsync(string userId, CancellationToken ct) => GetOwnedAsync<GoalRecord>("om_goals", userId, g => g.UserId, ct);

    public Task SaveGoalAsync(GoalRecord goal, CancellationToken ct) => SaveOwnedAsync("om_goals", goal.Id, goal.UserId, null, goal, goal.UpdatedUtc, ct);

    public async Task<bool> DeleteGoalAsync(string userId, string goalId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var entries = Cmd(conn, "delete from om_contributions where user_id = @u and goal_id = @g", ("u", userId), ("g", goalId));
        await entries.ExecuteNonQueryAsync(ct);
        await using var cmd = Cmd(conn, "delete from om_goals where user_id = @u and id = @id", ("u", userId), ("id", goalId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<Contribution>> GetContributionsAsync(string userId, CancellationToken ct) =>
        GetOwnedAsync<Contribution>("om_contributions", userId, c => c.UserId, ct);

    public Task SaveContributionAsync(Contribution c, CancellationToken ct) => SaveOwnedAsync("om_contributions", c.Id, c.UserId, c.GoalId, c, c.CreatedUtc, ct);

    public Task<bool> DeleteContributionAsync(string userId, string contributionId, CancellationToken ct) => DeleteOwnedAsync("om_contributions", userId, contributionId, ct);

    // The table names below are constants from this file, never input
    private async Task<IReadOnlyList<T>> GetOwnedAsync<T>(string table, string userId, Func<T, string> owner, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, $"select data::text from {table} where user_id = @u", ("u", userId));
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            if (JsonSerializer.Deserialize<T>(r.GetString(0), CompanionJson.Options) is { } row && owner(row) == userId)
            {
                list.Add(row);
            }
        }

        return list;
    }

    private async Task SaveOwnedAsync<T>(string table, string id, string userId, string? goalId, T row, DateTime updatedUtc, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var goalColumn = goalId == null ? "" : ", goal_id";
        var goalValue = goalId == null ? "" : ", @g";

        // The where clause keeps an id clash from ever overwriting another person's row
        await using var cmd = Cmd(conn,
            $"""
            insert into {table} (id, user_id{goalColumn}, data, updated_utc) values (@id, @u{goalValue}, cast(@data as jsonb), @updated)
            on conflict (id) do update set data = excluded.data, updated_utc = excluded.updated_utc
            where {table}.user_id = excluded.user_id
            """,
            ("id", id), ("u", userId), ("g", goalId), ("data", JsonSerializer.Serialize(row, CompanionJson.Options)), ("updated", Utc(updatedUtc)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> DeleteOwnedAsync(string table, string userId, string id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, $"delete from {table} where user_id = @u and id = @id", ("u", userId), ("id", id));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static DbCommand Cmd(DbConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private static object? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v, DateTimeKind.Utc) : null;

    private static string? Str(DbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static DateTime? Time(DbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
    }
}
