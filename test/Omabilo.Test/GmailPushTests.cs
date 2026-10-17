using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

/// <summary>Gmail push (users.watch + Pub/Sub): verifying notices, deduplicating them, syncing only the
/// mailbox they name, renewing and stopping watches. All against the fake Google; no real Pub/Sub.</summary>
public class GmailPushTests
{
    private const string Topic = "projects/omabilo-test/topics/gmail-push";
    private const string Audience = "https://omabilo.example/api/gmail/push";
    private const string ServiceAccount = "gmail-push@omabilo-test.iam.gserviceaccount.com";

    // ── Reading Pub/Sub's envelope ──

    private static string Envelope(string email, ulong historyId, string messageId = "pubsub-1")
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { emailAddress = email, historyId })));
        return JsonSerializer.Serialize(new { message = new { data, messageId, publishTime = "2026-10-17T16:00:00Z" }, subscription = "projects/omabilo-test/subscriptions/gmail-push" });
    }

    [Fact]
    public void A_pubsub_envelope_gives_the_mailbox_and_history_id()
    {
        var notice = PushNotices.Parse(Envelope("tester@example.com", 98765));

        Assert.Equal(new GmailPushNotice("tester@example.com", 98765, "pubsub-1"), notice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"message":{}}""")]
    [InlineData("""{"message":{"data":"bm90IGpzb24="}}""")]
    public void Anything_else_is_not_a_notice(string body) => Assert.Null(PushNotices.Parse(body));

    // ── Authenticating the push request ──

    private sealed class TestKeys(RSA rsa) : IGoogleSigningKeys
    {
        public Task<RSA?> FindAsync(string keyId, CancellationToken ct)
        {
            if (keyId != "key-1") return Task.FromResult<RSA?>(null);
            var copy = RSA.Create();
            copy.ImportParameters(rsa.ExportParameters(false));
            return Task.FromResult<RSA?>(copy);
        }
    }

    private static readonly DateTime Now = new(2026, 10, 17, 16, 0, 0, DateTimeKind.Utc);

    private static string Jwt(RSA rsa, object claims, string kid = "key-1")
    {
        var head = Pkce.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid, typ = "JWT" }));
        var body = Pkce.Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(head + "." + body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{head}.{body}.{Pkce.Base64Url(sig)}";
    }

    private static object Claims(string aud = Audience, string email = ServiceAccount, long? exp = null, bool verified = true) => new
    {
        iss = "https://accounts.google.com",
        aud,
        email,
        email_verified = verified,
        iat = new DateTimeOffset(Now).ToUnixTimeSeconds() - 10,
        exp = exp ?? new DateTimeOffset(Now).ToUnixTimeSeconds() + 3600,
    };

    private static readonly PushConfig Oidc = new(Topic, Audience, ServiceAccount, null);

    [Fact]
    public async Task A_token_signed_by_google_for_our_audience_and_service_account_is_accepted()
    {
        using var rsa = RSA.Create(2048);
        var auth = new PushAuthenticator(new TestKeys(rsa), () => Now);

        var result = await auth.CheckAsync(Oidc, "Bearer " + Jwt(rsa, Claims()), null, default);

        Assert.True(result.Ok);
        Assert.Equal("oidc", result.Method);
    }

    [Fact]
    public async Task Forged_misdirected_or_stale_tokens_are_refused()
    {
        using var rsa = RSA.Create(2048);
        using var attacker = RSA.Create(2048);
        var auth = new PushAuthenticator(new TestKeys(rsa), () => Now);

        async Task<string?> Problem(string header) => (await auth.CheckAsync(Oidc, header, null, default)).Problem;

        Assert.Equal("bad_signature", await Problem("Bearer " + Jwt(attacker, Claims())));
        Assert.Equal("unknown_signing_key", await Problem("Bearer " + Jwt(rsa, Claims(), kid: "other")));
        Assert.Equal("wrong_audience", await Problem("Bearer " + Jwt(rsa, Claims(aud: "https://evil.example/push"))));
        Assert.Equal("wrong_service_account", await Problem("Bearer " + Jwt(rsa, Claims(email: "someone@evil.example"))));
        Assert.Equal("email_not_verified", await Problem("Bearer " + Jwt(rsa, Claims(verified: false))));
        Assert.Equal("expired", await Problem("Bearer " + Jwt(rsa, Claims(exp: new DateTimeOffset(Now).ToUnixTimeSeconds() - 3600))));
        Assert.Equal("malformed_token", await Problem("Bearer not-a-jwt"));
        Assert.False((await auth.CheckAsync(Oidc, null, null, default)).Ok);
    }

    [Fact]
    public async Task The_url_token_must_match_exactly()
    {
        using var rsa = RSA.Create(2048);
        var auth = new PushAuthenticator(new TestKeys(rsa), () => Now);
        var config = new PushConfig(Topic, Audience, null, "a-long-random-push-token-0123456789");

        Assert.True((await auth.CheckAsync(config, null, "a-long-random-push-token-0123456789", default)).Ok);
        Assert.False((await auth.CheckAsync(config, null, "a-long-random-push-token-0123456788", default)).Ok);
        Assert.False((await auth.CheckAsync(config, null, null, default)).Ok);
    }

    // ── Watches ──

    [Fact]
    public async Task With_push_configured_the_first_sync_starts_a_watch_and_polls_only_hourly()
    {
        var rig = new CompanionRig { PushTopic = Topic };
        await rig.ConnectAsync("alice");

        await rig.Engine.SyncAsync("alice", default);

        Assert.Equal([Topic], rig.Google.WatchTopics);
        var watch = rig.Store.Watches["alice"];
        Assert.Null(watch.LastError);
        Assert.True(watch.ExpiresUtc > rig.Now.AddDays(6));
        Assert.Equal(rig.Now.AddMinutes(60), rig.Store.Connections["alice"].NextAttemptUtc);
    }

    [Fact]
    public async Task Without_push_nothing_is_watched_and_the_mailbox_is_polled_every_5_minutes()
    {
        var rig = new CompanionRig();
        await rig.ConnectAsync("alice");

        await rig.Engine.SyncAsync("alice", default);

        Assert.Empty(rig.Google.WatchTopics);
        Assert.Equal(rig.Now.AddMinutes(5), rig.Store.Connections["alice"].NextAttemptUtc);
    }

    [Fact]
    public async Task A_failed_watch_never_fails_the_sync_and_falls_back_to_polling()
    {
        var rig = new CompanionRig { PushTopic = Topic };
        rig.Google.WatchError = (403, "forbidden");
        await rig.ConnectAsync("alice");

        var result = await rig.Engine.SyncAsync("alice", default);

        Assert.True(result.Ok);
        Assert.Contains("403", rig.Store.Watches["alice"].LastError);
        Assert.Equal(rig.Now.AddMinutes(5), rig.Store.Connections["alice"].NextAttemptUtc);
        Assert.Contains(rig.Store.Audit, a => a.Entry.Action == "gmail_watch_failed");
    }

    [Fact]
    public async Task The_watch_is_renewed_daily_not_on_every_sync()
    {
        var rig = new CompanionRig { PushTopic = Topic };
        await rig.ConnectAsync("alice");
        await rig.Engine.SyncAsync("alice", default);

        rig.Now = rig.Now.AddHours(2);
        await rig.Engine.SyncAsync("alice", default);
        Assert.Single(rig.Google.WatchTopics);

        rig.Now = rig.Now.AddHours(23);
        await rig.Engine.SyncAsync("alice", default);
        Assert.Equal(2, rig.Google.WatchTopics.Count);
    }

    [Fact]
    public async Task Disconnecting_stops_the_watch_then_revokes()
    {
        var rig = new CompanionRig { PushTopic = Topic };
        await rig.ConnectAsync("alice");
        await rig.Engine.SyncAsync("alice", default);

        await rig.Engine.DisconnectAsync("alice", default);

        Assert.Equal(1, rig.Google.StopCalls);
        Assert.NotEmpty(rig.Google.Revoked);
        Assert.False(rig.Store.Watches.ContainsKey("alice"));
    }

    // ── A notice arrives ──

    [Fact]
    public async Task A_new_fictional_invoice_is_processed_from_the_notice_and_a_newsletter_is_not_kept()
    {
        var rig = new CompanionRig { PushTopic = Topic };
        await rig.ConnectAsync("alice");
        await rig.Engine.SyncAsync("alice", default);
        Assert.Empty(rig.Store.Items);

        var t = rig.Now;
        rig.Google.Add("m-invoice", "Revontuli Energy <billing@revontuli.example>", "Your electricity invoice", Mails.Invoice(t), t);
        rig.Google.Add("m-news", "Shop <news@shop.example>", "Autumn sale ends Sunday", Mails.Newsletter, t);

        var outcome = await rig.Engine.HandlePushAsync(new GmailPushNotice("Test.User@example.com", (ulong)rig.Google.HistoryCounter, "p1"), default);
        Assert.Equal(["alice"], outcome.UsersToSync);

        var result = await rig.Engine.SyncAfterPushAsync("alice", default);

        Assert.True(result.Ok);
        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Equal(FinKind.Invoice, item.Kind);
        Assert.Equal(64.20m, item.Amount);
        Assert.Equal("m-invoice", item.Sources.Single().GmailId);
        Assert.False(rig.Store.Messages[("alice", "m-news")].IsFinancial);
        Assert.Equal(1, rig.Store.Watches["alice"].PushCount);
    }

    [Fact]
    public async Task Redelivered_or_old_notices_are_counted_and_ignored()
    {
        var rig = new CompanionRig { PushTopic = Topic };
        await rig.ConnectAsync("alice");
        await rig.Engine.SyncAsync("alice", default);
        rig.Google.Add("m-invoice", "Revontuli Energy <billing@revontuli.example>", "Your electricity invoice", Mails.Invoice(rig.Now), rig.Now);
        var notice = new GmailPushNotice("test.user@example.com", (ulong)rig.Google.HistoryCounter, "p1");

        Assert.Single((await rig.Engine.HandlePushAsync(notice, default)).UsersToSync);
        await rig.Engine.SyncAfterPushAsync("alice", default);

        // Pub/Sub delivers at least once: the same notice again, and one older than the last sync
        var again = await rig.Engine.HandlePushAsync(notice, default);
        var older = await rig.Engine.HandlePushAsync(notice with { HistoryId = notice.HistoryId - 1 }, default);

        Assert.Empty(again.UsersToSync);
        Assert.Empty(older.UsersToSync);
        Assert.Equal(2, rig.Store.Watches["alice"].DuplicatePushCount);
        Assert.Single(rig.Store.Items);
    }

    [Fact]
    public async Task A_notice_syncs_only_the_mailbox_it_names()
    {
        var rig = new CompanionRig { PushTopic = Topic };
        rig.Google.EmailAddress = "alice@example.com";
        await rig.ConnectAsync("alice");
        rig.Google.EmailAddress = "bob@example.com";
        await rig.ConnectAsync("bob");
        await rig.Engine.SyncAsync("alice", default);
        await rig.Engine.SyncAsync("bob", default);
        rig.Google.Add("m-1", "Revontuli Energy <billing@revontuli.example>", "Your electricity invoice", Mails.Invoice(rig.Now), rig.Now);

        var outcome = await rig.Engine.HandlePushAsync(new GmailPushNotice("bob@example.com", (ulong)rig.Google.HistoryCounter, "p1"), default);

        Assert.Equal(["bob"], outcome.UsersToSync);
        Assert.Equal(0, rig.Store.Watches["alice"].PushCount);
    }

    [Fact]
    public async Task A_notice_for_an_unknown_mailbox_does_nothing()
    {
        var rig = new CompanionRig { PushTopic = Topic };
        await rig.ConnectAsync("alice");

        var outcome = await rig.Engine.HandlePushAsync(new GmailPushNotice("stranger@example.com", 999999, "p1"), default);

        Assert.Empty(outcome.UsersToSync);
        Assert.Equal(1, outcome.UnknownMailbox);
        Assert.False(rig.Store.Watches.ContainsKey("alice"));
    }

    [Fact]
    public async Task A_mailbox_backing_off_after_errors_keeps_its_backoff()
    {
        var rig = new CompanionRig { PushTopic = Topic };
        await rig.ConnectAsync("alice");
        rig.Store.Connections["alice"] = rig.Store.Connections["alice"] with { Status = ConnectionStatus.Error, NextAttemptUtc = rig.Now.AddMinutes(30) };

        var outcome = await rig.Engine.HandlePushAsync(new GmailPushNotice("test.user@example.com", 999999, "p1"), default);

        Assert.Empty(outcome.UsersToSync);
        Assert.Equal(rig.Now.AddMinutes(30), rig.Store.Connections["alice"].NextAttemptUtc);
        Assert.Equal(1, rig.Store.Watches["alice"].PushCount);
    }

    [Fact]
    public async Task A_push_waits_for_a_running_sync_instead_of_dropping_the_mail()
    {
        var store = new InMemoryCompanionStore();
        var google = new FakeGoogle();
        var client = new GoogleClient(new HttpClient(google));
        var keys = new TokenProtector(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var waits = 0;

        // The "running sync" finishes while the push waits
        var engine = new SyncEngine(store, client, client, () => CompanionRig.Config, () => keys, new PdfAttachmentReader(), null, new SyncOptions(),
            () => DateTime.UtcNow, _ => { }, async (_, ct) => { waits++; await store.ReleaseSyncAsync("alice", ct); }, () => Topic);

        var url = await engine.BeginConnectAsync("alice", default);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"];
        await engine.CompleteConnectAsync(state, "good-code", null, default);
        await engine.SyncAsync("alice", default);
        google.Add("m-invoice", "Revontuli Energy <billing@revontuli.example>", "Your electricity invoice", Mails.Invoice(DateTime.UtcNow), DateTime.UtcNow);
        await store.TryClaimSyncAsync("alice", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10), default);

        var result = await engine.SyncAfterPushAsync("alice", default);

        Assert.True(result.Ok);
        Assert.Equal(1, waits);
        Assert.Single(store.Items);
    }
}
