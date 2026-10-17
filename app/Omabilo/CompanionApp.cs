// IKON wiring for the connected companion: secrets, the database, the Google redirect endpoint, the
// scheduled Gmail sweep, account erasure and the EU-pinned AI explainer. The logic lives in the
// plain-C# files beside this one; this file only connects it to the platform.

using Ikon.App.Triggers;
using Ikon.Common.Core.Protocol;

public partial class OmabiloApp
{
    private static readonly HttpClient GoogleHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private SyncEngine? _engine;
    private CompanionService? _companion;
    private PostgresCompanionStore? _store;

    private const string CallbackPath = "/oauth/google/callback";

    private PostgresCompanionStore Store => _store ??= new PostgresCompanionStore(async ct =>
    {
        var conn = await app.DatabaseAsync();
        await conn.OpenAsync(ct);
        return conn;
    });

    private SyncEngine Engine => _engine ??= CreateEngine();

    private CompanionService Companion => _companion ??= new CompanionService(Store, Engine, () => DateTime.UtcNow);

    private SyncEngine CreateEngine()
    {
        var client = new GoogleClient(GoogleHttp);

        // Names only, never values: says at a glance which settings this instance can see
        Log.Instance.Info($"companion: secrets present: {string.Join(", ", app.Secrets.Keys.Order())}");
        return new SyncEngine(Store, client, client, GoogleConfig, () => TokenProtector.FromBase64(SecretOrNull("OMABILO_TOKEN_KEY")),
            new PdfAttachmentReader(), EuAiAnalyzer.CreateOrNull(), new SyncOptions(), () => DateTime.UtcNow,
            message => Log.Instance.Info(message), pushTopic: () => PushSettings()?.Topic);
    }

    // ── Gmail push settings ─────────────────────────────────────────────────

    private const string PushPath = "/gmail/push";

    private PushAuthenticator? _pushAuth;

    private PushAuthenticator PushAuth => _pushAuth ??= new PushAuthenticator(new GoogleSigningKeys(GoogleHttp), () => DateTime.UtcNow);

    /// <summary>The address to give the Pub/Sub push subscription (and the OIDC audience).</summary>
    private string PushEndpointUrl => $"{app.PublicUrl.TrimEnd('/')}/api{PushPath}";

    /// <summary>Null until the topic and at least one way to authenticate Pub/Sub are set; until then
    /// Omabilo polls every 5 minutes and asks Gmail for no notices.</summary>
    private PushConfig? PushSettings()
    {
        if (SecretOrNull("GMAIL_PUBSUB_TOPIC") is not { } topic)
        {
            return null;
        }

        var config = new PushConfig(topic, PushEndpointUrl, SecretOrNull("GMAIL_PUSH_SERVICE_ACCOUNT"), SecretOrNull("GMAIL_PUSH_TOKEN"));
        return config.CanAuthenticate ? config : null;
    }

    // Trimmed: a pasted value can carry a space, a line break or (from PowerShell) a byte-order mark,
    // and Google rejects a client id or secret with any of them
    private string? SecretOrNull(string key) =>
        app.Secrets.TryGet(key, out var value) && (value ?? "").Trim().Trim((char)0xFEFF).Trim() is { Length: > 0 } clean ? clean : null;

    /// <summary>Null until both Google secrets are set; the UI then says exactly what is missing.</summary>
    private GoogleOAuthConfig? GoogleConfig() =>
        SecretOrNull("GOOGLE_OAUTH_CLIENT_ID") is { } id && SecretOrNull("GOOGLE_OAUTH_CLIENT_SECRET") is { } secret
            ? new GoogleOAuthConfig(id, secret, GoogleRedirectUri)
            : null;

    /// <summary>The address to register in Google Cloud as an authorized redirect URI.</summary>
    private string GoogleRedirectUri => $"{app.PublicUrl.TrimEnd('/')}/api{CallbackPath}";

    // ── Google's redirect after consent ─────────────────────────────────────

    // Public because Google calls a fixed URL that can carry no grant. The one-time state, checked
    // against the database, is what ties the callback to the signed-in person who started it.
    [HttpGet(CallbackPath, Auth = EndpointAuth.Public)]
    public async Task<HttpResult> GoogleOAuthCallback(Ikon.App.HttpRequest request, CancellationToken ct)
    {
        request.Query.TryGetValue("state", out var state);
        request.Query.TryGetValue("code", out var code);
        request.Query.TryGetValue("error", out var error);

        ConnectResult result;

        try
        {
            result = await Engine.CompleteConnectAsync(state, code, error, ct);
        }
        catch (Exception ex)
        {
            // The type, plus Google's status and reason code when there is one; never tokens or codes
            Log.Instance.Warning($"gmail callback failed: {ex.GetType().Name}{(ex is GmailApiException g ? $" ({g.Status} {g.Reason})" : "")}");
            result = new ConnectResult(false, null, "Something went wrong while connecting Gmail. Please try again from Omabilo.", "error");
        }

        if (result is { Ok: true, UserId: { } userId })
        {
            // The first scan starts at once, server-side; the person doesn't need to keep any tab open
            _ = Task.Run(async () =>
            {
                await using var hold = await app.BackgroundWork.StartAsync(TimeSpan.FromMinutes(15), "first Gmail scan");

                try
                {
                    await Engine.SyncAsync(userId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Instance.Warning($"first gmail scan failed: {ex.GetType().Name}");
                }
            });
        }

        return HttpResult.Html(CallbackPage(result.Ok, result.Message));
    }

    private string CallbackPage(bool ok, string message)
    {
        var back = WebUtility.HtmlEncode(app.PublicUrl);
        var text = WebUtility.HtmlEncode(message);
        var title = ok ? "Gmail connected" : "Gmail not connected";
        var color = ok ? "#0f766e" : "#b42318";

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}} · Omabilo</title>
            <style>
              body { margin: 0; font-family: system-ui, sans-serif; background: #faf7f2; color: #14213d; display: grid; place-items: center; min-height: 100vh; padding: 16px; }
              main { background: #fff; border: 1px solid #e7e0d5; border-radius: 16px; max-width: 440px; padding: 28px; }
              h1 { font-size: 22px; margin: 0 0 8px; color: {{color}}; }
              p { line-height: 1.5; margin: 0 0 20px; }
              a { display: inline-block; background: #0f766e; color: #fff; text-decoration: none; padding: 12px 18px; border-radius: 12px; font-weight: 600; }
            </style></head>
            <body><main><h1>{{title}}</h1><p>{{text}}</p><p>You can close this tab and go back to Omabilo.</p><a href="{{back}}">Back to Omabilo</a></main></body></html>
            """;
    }

    // ── Scheduled sync: runs in the cloud without any browser open ──────────

    // The fallback: every 5 minutes, but each mailbox is only due when its own interval has passed
    // (5 minutes without push, hourly reconciliation with an active Gmail watch)
    [Cron("*/5 * * * *")]
    public async Task SyncConnectedGmailAccounts(CancellationToken ct)
    {
        // Recorded before anything else, so /api/health can prove the scheduler really fires
        await RecordJobRunAsync(SyncJobKey, "started", ct);

        var missing = Engine.MissingConfiguration();

        if (missing.Count > 0)
        {
            await RecordJobRunAsync(SyncJobKey, "skipped: Gmail is not configured on this server", ct);
            return;
        }

        await using var hold = await app.BackgroundWork.StartAsync(TimeSpan.FromMinutes(14), "scheduled Gmail sync");

        try
        {
            var count = await Engine.SyncDueAsync(25, ct);
            await RecordJobRunAsync(SyncJobKey, $"ok: {count} connection(s) synced", ct);

            if (count > 0)
            {
                Log.Instance.Info($"scheduled gmail sync: {count} connection(s) synced");
            }
        }
        catch (Exception ex)
        {
            await RecordJobRunAsync(SyncJobKey, $"failed: {ex.GetType().Name}", ct);
            throw;
        }
    }

    // ── Gmail push: Pub/Sub calls this when a watched inbox changes ──────────

    // Public because Pub/Sub calls it from Google's servers. The request must carry Pub/Sub's signed
    // token (or the push URL token); the notice only names a mailbox and a history id, and the sync
    // it triggers reads that mailbox with the owner's own stored grant, never with anything sent here.
    [HttpPost(PushPath, Auth = EndpointAuth.Public)]
    public async Task<HttpResult> GmailPush(Ikon.App.HttpRequest request, CancellationToken ct)
    {
        request.Headers.TryGetValue("Authorization", out var authorization);

        if (PushSettings() is not { } settings)
        {
            // Says only whether an Authorization header reached the app (never its value), which shows
            // whether Pub/Sub's signed token can be checked behind this host's gateway
            return new HttpResult(404, new { error = "Gmail push is not configured on this server", authorizationHeaderReceived = authorization != null });
        }

        request.Query.TryGetValue("token", out var urlToken);
        var auth = await PushAuth.CheckAsync(settings, authorization, urlToken, ct);

        if (!auth.Ok)
        {
            Log.Instance.Warning($"gmail push rejected: {auth.Method} {auth.Problem} (authorization header present: {authorization != null})");
            await RecordJobRunAsync(PushRejectedKey, $"{auth.Method} {auth.Problem}", ct);
            return new HttpResult(401, new { error = "not authorized" });
        }

        if (PushNotices.Parse(request.Body) is not { } notice)
        {
            // Acknowledged so Pub/Sub doesn't retry something that can never be read
            Log.Instance.Warning("gmail push: unreadable notice acknowledged");
            return HttpResult.NoContent();
        }

        PushOutcome outcome;

        try
        {
            outcome = await Engine.HandlePushAsync(notice, ct);
        }
        catch (Exception ex)
        {
            // Not acknowledged: Pub/Sub delivers it again later
            Log.Instance.Warning($"gmail push failed before syncing: {ex.GetType().Name}");
            return new HttpResult(503, new { error = "try again" });
        }

        await RecordJobRunAsync(PushAcceptedKey, $"{auth.Method}: {outcome.UsersToSync.Count} to sync, {outcome.Duplicates} duplicate, {outcome.UnknownMailbox} unknown", ct);
        Log.Instance.Info($"gmail push ({auth.Method}): {outcome.UsersToSync.Count} mailbox sync(s) started, {outcome.Duplicates} duplicate(s), {outcome.UnknownMailbox} unknown");

        if (outcome.UsersToSync.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                await using var hold = await app.BackgroundWork.StartAsync(TimeSpan.FromMinutes(10), "Gmail push sync");

                foreach (var userId in outcome.UsersToSync)
                {
                    try
                    {
                        var result = await Engine.SyncAfterPushAsync(userId, CancellationToken.None);
                        Log.Instance.Info($"gmail push sync for user {userId}: {result.Code ?? "ok"}, {result.Scanned} checked, {result.NewItems} new");
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Warning($"gmail push sync failed for user {userId}: {ex.GetType().Name}");
                    }
                }
            });
        }

        return HttpResult.NoContent();
    }

    private string PushAcceptedKey => $"gmail-push@{new Uri(app.PublicUrl).Authority}";

    private string PushRejectedKey => $"gmail-push-rejected@{new Uri(app.PublicUrl).Authority}";

    // ── Health: what an operator needs to know, and nothing about any person ──

    [HttpGet("/health", Auth = EndpointAuth.Public)]
    public async Task<HttpResult> Health(CancellationToken ct)
    {
        var database = "ok";
        var jobs = new Dictionary<string, (DateTime At, string Result)>();
        PushOverview? overview = null;

        try
        {
            await using var conn = await app.DatabaseAsync();
            await conn.OpenAsync(ct);
            await EnsureJobTableAsync(conn, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "select job, last_run_utc, last_result from om_job_runs where job in (@sync, @push, @rejected)";
            AddParam(cmd, "sync", SyncJobKey);
            AddParam(cmd, "push", PushAcceptedKey);
            AddParam(cmd, "rejected", PushRejectedKey);

            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    jobs[r.GetString(0)] = (DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc), r.GetString(2));
                }
            }

            overview = await Store.PushOverviewAsync(DateTime.UtcNow, ct);
        }
        catch (Exception ex)
        {
            database = $"error: {ex.GetType().Name}";
        }

        var missing = Engine.MissingConfiguration();
        var body = new
        {
            status = database == "ok" ? "ok" : "degraded",
            database,
            tokenKeyConfigured = TokenProtector.FromBase64(SecretOrNull("OMABILO_TOKEN_KEY")) != null,
            googleOAuthConfigured = GoogleConfig() != null,
            missingConfiguration = missing,
            googleRedirectUri = GoogleRedirectUri,
            gmailScope = GoogleScopes.GmailReadOnly,
            scheduledSync = new
            {
                schedule = "sweep every 5 minutes (UTC); each mailbox every 5 minutes without push, hourly with an active Gmail watch",
                lastRunUtc = jobs.TryGetValue(SyncJobKey, out var s) ? s.At : (DateTime?)null,
                lastResult = jobs.TryGetValue(SyncJobKey, out var s2) ? s2.Result : null,
            },
            gmailPush = new
            {
                configured = PushSettings() != null,
                topicConfigured = SecretOrNull("GMAIL_PUBSUB_TOPIC") != null,
                oidcServiceAccountConfigured = SecretOrNull("GMAIL_PUSH_SERVICE_ACCOUNT") != null,
                urlTokenConfigured = SecretOrNull("GMAIL_PUSH_TOKEN") != null,
                pushEndpoint = PushEndpointUrl,
                activeWatches = overview?.ActiveWatches,
                watchErrors = overview?.WatchErrors,
                notificationsAccepted = overview?.Pushes,
                duplicatesIgnored = overview?.DuplicatePushes,
                lastNotificationUtc = overview?.LastPushUtc,
                lastAccepted = jobs.TryGetValue(PushAcceptedKey, out var a) ? new { at = a.At, result = a.Result } : null,
                lastRejected = jobs.TryGetValue(PushRejectedKey, out var rj) ? new { at = rj.At, result = rj.Result } : null,
            },
            checkedUtc = DateTime.UtcNow,
        };

        return HttpResult.Json(body, database == "ok" ? 200 : 503);
    }

    // Keyed by server address: a local run shares the live database, and its runs must not pass for the live scheduler's
    private string SyncJobKey => $"gmail-sync@{new Uri(app.PublicUrl).Authority}";

    private static async Task EnsureJobTableAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "create table if not exists om_job_runs (job text primary key, last_run_utc timestamptz not null, last_result text not null)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task RecordJobRunAsync(string job, string result, CancellationToken ct)
    {
        try
        {
            await using var conn = await app.DatabaseAsync();
            await conn.OpenAsync(ct);
            await EnsureJobTableAsync(conn, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                insert into om_job_runs (job, last_run_utc, last_result) values (@job, @at, @result)
                on conflict (job) do update set last_run_utc = excluded.last_run_utc, last_result = excluded.last_result
                """;
            AddParam(cmd, "job", job);
            AddParam(cmd, "at", DateTime.UtcNow);
            AddParam(cmd, "result", result);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"job heartbeat failed: {ex.GetType().Name}");
        }
    }


    // ── Account erasure: the platform deletes the account, this deletes what Omabilo kept ──

    [Trigger(TriggerEventType.UserErased)]
    internal async Task EraseCompanionDataAsync(UserDataErasureEventArgs args)
    {
        await Companion.DeleteAllDataAsync(args.UserId, CancellationToken.None);
        await DeleteFeedbackForAsync(args.UserId);
        Log.Instance.Info($"erased companion data for user {args.UserId}");
    }

    private async Task DeleteFeedbackForAsync(string userId)
    {
        try
        {
            await using var conn = await app.DatabaseAsync();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"delete from {FeedbackTable} where tester_tag = @tag";
            var p = cmd.CreateParameter();
            p.ParameterName = "tag";
            p.Value = TesterTagFor(userId);
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // No feedback table yet means there is nothing to delete
            Log.Instance.Info($"feedback delete skipped: {ex.GetType().Name}");
        }
    }
}

/// <summary>The AI explainer on the IKON platform: a small Claude model pinned to EU inference,
/// with the email fenced off as untrusted data and the answer typed and validated.</summary>
public sealed class EuAiAnalyzer : IAiAnalyzer
{
    private const LLMModel Model = LLMModel.Claude55Haiku;

    public string ModelName => $"{Model} (EU)";

    /// <summary>Null when the model isn't served in the EU, so AI stays off rather than leaving the region.</summary>
    public static EuAiAnalyzer? CreateOrNull()
    {
        try
        {
            var regions = Emerge.GetSupportedRegions(Model);
            return regions.Any(r => r is ModelRegion.Eu or ModelRegion.EuNorth or ModelRegion.EuWest or ModelRegion.EuCentral or ModelRegion.EuSouth)
                ? new EuAiAnalyzer()
                : null;
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"AI region check failed: {ex.GetType().Name}");
            return null;
        }
    }

    public async Task<AiRawAnalysis> AnalyzeAsync(string redactedInput, CancellationToken ct) =>
        await Emerge.Run<AiRawAnalysis>(Model, pass =>
        {
            pass.SystemPrompt = AiGuard.SystemPrompt;
            pass.Command = "Explain this financial email for the person who received it.\n\n" + redactedInput;
            pass.Regions = [ModelRegion.Eu];
            pass.Temperature = 0.2;
        }, ct);
}
