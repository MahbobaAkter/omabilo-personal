// Storage for the connected companion. Every method takes the owner's user id and every query
// filters on it, so a record id from one person can never read or change another person's data.
// PostgresCompanionStore runs on the app's built-in IKON Postgres database through plain ADO.NET.

using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;

public interface ICompanionStore
{
    Task<GmailConnection?> GetConnectionAsync(string userId, CancellationToken ct);
    Task SaveConnectionAsync(GmailConnection connection, CancellationToken ct);
    Task DeleteConnectionAsync(string userId, CancellationToken ct);
    Task<IReadOnlyList<string>> DueConnectionsAsync(DateTime now, int max, CancellationToken ct);

    /// <summary>Takes the per-user sync lock, so two runs (cron and "Sync now") never overlap.</summary>
    Task<bool> TryClaimSyncAsync(string userId, DateTime now, DateTime until, CancellationToken ct);
    Task ReleaseSyncAsync(string userId, CancellationToken ct);

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

    /// <summary>Retention: expired sign-in states, old non-financial message ids and old audit rows.</summary>
    Task PruneAsync(DateTime now, CancellationToken ct);
}

public sealed record AuditEntry(DateTime AtUtc, string Action, string? ItemId);

public static class CompanionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class PostgresCompanionStore(Func<CancellationToken, Task<DbConnection>> open) : ICompanionStore
{
    public const int NonFinancialRetentionDays = 180;
    public const int AuditRetentionDays = 365;

    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static bool _schemaReady;

    public const string Schema =
        """
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

    public async Task<GmailConnection?> GetConnectionAsync(string userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "select * from om_connections where user_id = @u", ("u", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);

        if (!await r.ReadAsync(ct))
        {
            return null;
        }

        return new GmailConnection
        {
            UserId = userId,
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
    }

    public async Task SaveConnectionAsync(GmailConnection c, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            insert into om_connections (user_id, google_email, granted_scopes, refresh_token_enc, access_token_enc, access_expires_utc, history_id, status,
                last_sync_utc, last_success_utc, previous_success_utc, last_error, failure_count, next_attempt_utc, last_sync_new_items, last_sync_scanned, initial_scan_done, connected_utc)
            values (@u, @email, @scopes, @rt, @at, @exp, @hist, @status, @ls, @lsu, @psu, @err, @fails, @next, @newi, @scanned, @init, @connected)
            on conflict (user_id) do update set google_email = excluded.google_email, granted_scopes = excluded.granted_scopes,
                refresh_token_enc = excluded.refresh_token_enc, access_token_enc = excluded.access_token_enc, access_expires_utc = excluded.access_expires_utc,
                history_id = excluded.history_id, status = excluded.status, last_sync_utc = excluded.last_sync_utc, last_success_utc = excluded.last_success_utc,
                previous_success_utc = excluded.previous_success_utc, last_error = excluded.last_error, failure_count = excluded.failure_count,
                next_attempt_utc = excluded.next_attempt_utc, last_sync_new_items = excluded.last_sync_new_items, last_sync_scanned = excluded.last_sync_scanned,
                initial_scan_done = excluded.initial_scan_done, connected_utc = excluded.connected_utc
            """,
            ("u", c.UserId), ("email", c.GoogleEmail), ("scopes", c.GrantedScopes), ("rt", c.RefreshTokenEnc), ("at", c.AccessTokenEnc),
            ("exp", Utc(c.AccessExpiresUtc)), ("hist", c.HistoryId), ("status", c.Status.ToString()), ("ls", Utc(c.LastSyncUtc)), ("lsu", Utc(c.LastSuccessUtc)),
            ("psu", Utc(c.PreviousSuccessUtc)), ("err", c.LastError), ("fails", c.FailureCount), ("next", Utc(c.NextAttemptUtc)), ("newi", c.LastSyncNewItems),
            ("scanned", c.LastSyncScanned), ("init", c.InitialScanDone), ("connected", Utc(c.ConnectedUtc)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteConnectionAsync(string userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "delete from om_connections where user_id = @u", ("u", userId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> DueConnectionsAsync(DateTime now, int max, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            """
            select user_id from om_connections
            where status in ('Connected', 'Error', 'Syncing') and refresh_token_enc is not null and next_attempt_utc <= @now
              and (sync_lock_until_utc is null or sync_lock_until_utc < @now)
            order by next_attempt_utc limit @max
            """,
            ("now", Utc(now)), ("max", max));
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    public async Task<bool> TryClaimSyncAsync(string userId, DateTime now, DateTime until, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn,
            "update om_connections set sync_lock_until_utc = @until where user_id = @u and (sync_lock_until_utc is null or sync_lock_until_utc < @now)",
            ("until", Utc(until)), ("u", userId), ("now", Utc(now)));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task ReleaseSyncAsync(string userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "update om_connections set sync_lock_until_utc = null where user_id = @u", ("u", userId));
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

        foreach (var table in new[] { "om_items", "om_messages", "om_settings", "om_audit", "om_oauth_states", "om_connections" })
        {
            await using var cmd = Cmd(conn, $"delete from {table} where user_id = @u", ("u", userId));
            await cmd.ExecuteNonQueryAsync(ct);
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

    // ── ADO helpers ─────────────────────────────────────────────────────────

    private static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
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
