using System.Web;
using Xunit;

// Several Gmail accounts for one Omabilo user: each account is its own connection (tokens, status,
// lock, watch) and all of them file into the same person's records. Run against the in-memory store
// and a fake Google answering like the real endpoints. Fictional people, addresses and amounts only.

public class MultiGmailTests
{
    private const string Work = "work.mailbox@example.com";
    private const string Biller = "Revontuli Energy <billing@revontuli.example>";

    /// <summary>Alice with her first Gmail (the main account) and a second, work account.</summary>
    private static async Task<(CompanionRig Rig, GmailConnection Main, GmailConnection Second)> TwoAccountsAsync()
    {
        var rig = new CompanionRig();
        var main = await rig.ConnectAsync("alice");
        rig.Google.OtherMailboxes[Work] = [];
        rig.Google.EmailAddress = Work;
        var second = await rig.ConnectAsync("alice");
        rig.Google.EmailAddress = "test.user@example.com";
        return (rig, main, second);
    }

    private static async Task<ConnectResult> ConnectAsAsync(CompanionRig rig, string userId, string email)
    {
        rig.Google.OtherMailboxes.TryAdd(email, []);
        rig.Google.EmailAddress = email;
        var url = await rig.Engine.BeginConnectAsync(userId, default);
        var state = HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;
        return await rig.Engine.CompleteConnectAsync(state, "good-code", null, default);
    }

    [Fact]
    public async Task Connecting_a_second_account_keeps_the_first()
    {
        var (rig, main, second) = await TwoAccountsAsync();

        var accounts = await rig.Store.GetConnectionsAsync("alice", default);
        Assert.Equal(2, accounts.Count);
        Assert.Equal(Mailboxes.Main, main.AccountId);
        Assert.NotEqual(main.AccountId, second.AccountId);
        Assert.Equal(["test.user@example.com", Work], accounts.Select(a => a.GoogleEmail));

        // The first account's tokens are untouched; each account's token is encrypted for that account only
        Assert.Equal("refresh-1", rig.Keys.Unprotect(accounts[0].RefreshTokenEnc, "alice"));
        Assert.Equal($"refresh-{Work}", rig.Keys.Unprotect(accounts[1].RefreshTokenEnc, Mailboxes.Key("alice", second.AccountId)));
        Assert.Null(rig.Keys.Unprotect(accounts[1].RefreshTokenEnc, "alice"));
        Assert.Contains(rig.Store.Audit, a => a.UserId == "alice" && a.Entry.Action == "gmail_account_added");
        Assert.Empty(rig.Google.Revoked);
    }

    [Fact]
    public async Task Bills_from_every_account_arrive_in_one_inbox_with_their_source_account()
    {
        var (rig, _, second) = await TwoAccountsAsync();
        var t = DateTime.UtcNow.Date;
        rig.Google.Add("m-home", Biller, "Your electricity invoice", Mails.Invoice(t), t.AddDays(-2));
        rig.Google.AddTo(Work, "m-work", "Kuura Telecom <billing@kuura-telecom.example>", "Your phone invoice",
            Mails.Invoice(t, number: "KT-778120", amount: 29.90m, reference: "55001230"), t.AddDays(-1));

        var result = await rig.Engine.SyncAsync("alice", default);

        Assert.True(result.Ok, result.Message);
        var items = rig.Store.Items.Values.Where(i => i.UserId == "alice").ToList();
        Assert.Equal(2, items.Count);
        var home = Assert.Single(items, i => i.Amount == 64.20m);
        var work = Assert.Single(items, i => i.Amount == 29.90m);
        Assert.Equal(Mailboxes.Main, home.Sources[0].AccountId);
        Assert.Null(home.Sources[0].Account);
        Assert.Equal(second.AccountId, work.Sources[0].AccountId);
        Assert.Equal("m-work", work.Sources[0].GmailId);
        Assert.Equal(Mailboxes.Main, SyncEngine.AccountOf(home));
        Assert.Equal(second.AccountId, SyncEngine.AccountOf(work));

        // Each account remembers its own messages under its own key
        Assert.True(rig.Store.Messages.ContainsKey(("alice", "m-home")));
        Assert.True(rig.Store.Messages.ContainsKey(("alice", Mailboxes.MessageKey(second.AccountId, "m-work"))));
        Assert.All(await rig.Store.GetConnectionsAsync("alice", default), c => Assert.True(c.InitialScanDone));
    }

    [Fact]
    public async Task The_same_bill_in_two_accounts_is_kept_once_with_both_sources()
    {
        var (rig, _, second) = await TwoAccountsAsync();
        var t = DateTime.UtcNow.Date;
        rig.Google.Add("m-a", Biller, "Your electricity invoice", Mails.Invoice(t), t.AddDays(-2));
        rig.Google.AddTo(Work, "m-b", Biller, "Your electricity invoice", Mails.Invoice(t), t.AddDays(-2));

        await rig.Engine.SyncAsync("alice", default);

        var bill = Assert.Single(rig.Store.Items.Values, i => i.UserId == "alice" && i.PossibleDuplicateOf == null);
        Assert.Equal(64.20m, bill.Amount);
        Assert.Contains(bill.Sources, s => s.GmailId == "m-a" && s.AccountId == Mailboxes.Main);
        Assert.Contains(bill.Sources, s => s.GmailId == "m-b" && s.AccountId == second.AccountId);

        // Counted once in what is due
        var snap = await rig.Service.LoadAsync("alice", default);
        Assert.Equal(64.20m, snap.Plan.ObligationsTotal);
    }

    [Fact]
    public async Task Different_bills_with_the_same_amount_in_two_accounts_stay_apart()
    {
        var (rig, _, _) = await TwoAccountsAsync();
        var t = DateTime.UtcNow.Date;
        rig.Google.Add("m-a", Biller, "Your electricity invoice", Mails.Invoice(t, number: "RE-2026-48213", reference: "20241182"), t.AddDays(-2));
        rig.Google.AddTo(Work, "m-b", "Kuura Telecom <billing@kuura-telecom.example>", "Your phone invoice",
            Mails.Invoice(t, number: "KT-778120", reference: "55001230"), t.AddDays(-2));

        await rig.Engine.SyncAsync("alice", default);

        var items = rig.Store.Items.Values.Where(i => i.UserId == "alice").ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items.Select(i => i.InvoiceNumber).Distinct().Count());
    }

    [Fact]
    public async Task The_same_message_id_in_two_accounts_is_two_messages()
    {
        var (rig, _, _) = await TwoAccountsAsync();
        var t = DateTime.UtcNow.Date;

        // Gmail ids are only unique inside one mailbox
        rig.Google.Add("m-same", Biller, "Your electricity invoice", Mails.Invoice(t), t.AddDays(-2));
        rig.Google.AddTo(Work, "m-same", "Kuura Telecom <billing@kuura-telecom.example>", "Your phone invoice",
            Mails.Invoice(t, number: "KT-778120", amount: 29.90m, reference: "55001230"), t.AddDays(-1));

        await rig.Engine.SyncAsync("alice", default);

        Assert.Equal(2, rig.Store.Items.Values.Count(i => i.UserId == "alice"));
    }

    [Fact]
    public async Task Pay_reported_in_two_accounts_counts_once()
    {
        var (rig, _, _) = await TwoAccountsAsync();
        var t = DateTime.UtcNow.Date;
        rig.Google.Add("m-pay", "Kuura Software <payroll@kuura.example>", "Palkkalaskelma lokakuu", Mails.Payslip(t.AddDays(-2)), t.AddDays(-3));
        rig.Google.AddTo(Work, "m-pay2", "Kuura Software <payroll@kuura.example>", "Palkkalaskelma lokakuu", Mails.Payslip(t.AddDays(-2)), t.AddDays(-3));
        await rig.Engine.SyncAsync("alice", default);

        // The second copy is flagged as a duplicate, not filed as a second pay
        var slips = rig.Store.Items.Values.Where(i => i.UserId == "alice" && i.IsIncome).ToList();
        Assert.Equal(2, slips.Count);
        var slip = Assert.Single(slips, i => i.PossibleDuplicateOf == null);
        Assert.Equal(slip.Id, Assert.Single(slips, i => i.PossibleDuplicateOf != null).PossibleDuplicateOf);

        // Even with both marked received, the pay counts once in the plan, the report and month by month
        foreach (var s in slips) await rig.Service.SetStateAsync("alice", s.Id, UserState.ReportedPaid, default);
        await rig.Service.SaveSettingsAsync("alice", new CompanionSettings { Balance = 300m, BalanceAsOfUtc = t.AddDays(-10), PaydayDay = t.AddDays(-12).Day }, default);

        var snap = await rig.Service.LoadAsync("alice", default);
        var payDate = slip.EventDate!.Value;
        Assert.Equal(2412.40m, snap.Plan.IncomeTotal);
        Assert.Equal(2412.40m, MonthlyReports.For(snap.Items, payDate.Year, payDate.Month, t, snap.Income).IncomeTotal);
        Assert.Equal(2412.40m, MonthlySummary.Build(snap.Items, t).Single(m => m.Year == payDate.Year && m.Month == payDate.Month).IncomeReportedReceived);
    }

    [Fact]
    public async Task Connecting_an_address_again_reconnects_that_account()
    {
        var (rig, _, second) = await TwoAccountsAsync();
        var before = rig.Store.Connections[Mailboxes.Key("alice", second.AccountId)];
        rig.Now = rig.Now.AddDays(3);

        var result = await ConnectAsAsync(rig, "alice", Work.ToUpperInvariant());

        Assert.True(result.Ok, result.Message);
        Assert.Equal(second.AccountId, result.AccountId);
        Assert.Equal(2, (await rig.Store.GetConnectionsAsync("alice", default)).Count);
        Assert.Equal(before.ConnectedUtc, rig.Store.Connections[Mailboxes.Key("alice", second.AccountId)].ConnectedUtc);
        Assert.Contains(rig.Store.Audit, a => a.Entry.Action == "gmail_reconnected");
    }

    [Fact]
    public async Task Disconnecting_one_account_keeps_the_other_and_every_record()
    {
        var (rig, _, second) = await TwoAccountsAsync();
        var t = DateTime.UtcNow.Date;
        rig.Google.AddTo(Work, "m-work", Biller, "Your electricity invoice", Mails.Invoice(t), t.AddDays(-2));
        await rig.Engine.SyncAsync("alice", default);

        await rig.Engine.DisconnectAsync("alice", second.AccountId, default);

        var left = Assert.Single(await rig.Store.GetConnectionsAsync("alice", default));
        Assert.Equal(Mailboxes.Main, left.AccountId);
        Assert.Equal([$"refresh-{Work}"], rig.Google.Revoked);
        Assert.Single(rig.Store.Items.Values, i => i.UserId == "alice");

        // The main account still syncs
        Assert.True((await rig.Engine.SyncAsync("alice", default)).Ok);
    }

    [Fact]
    public async Task A_revoked_account_needs_reconnecting_while_the_other_keeps_syncing()
    {
        var (rig, _, second) = await TwoAccountsAsync();
        await rig.Engine.SyncAsync("alice", default);
        rig.Google.RevokedAccounts.Add(Work);
        rig.Google.InvalidateAccessTokens();
        var t = DateTime.UtcNow.Date;
        rig.Google.Add("m-new", Biller, "Your electricity invoice", Mails.Invoice(t), t.AddDays(-1));

        var result = await rig.Engine.SyncAsync("alice", default);

        Assert.False(result.Ok);
        Assert.Equal("needs_reconnect", result.Code);
        Assert.Equal(ConnectionStatus.NeedsReconnect, rig.Store.Connections[Mailboxes.Key("alice", second.AccountId)].Status);
        Assert.Null(rig.Store.Connections[Mailboxes.Key("alice", second.AccountId)].RefreshTokenEnc);
        Assert.Equal(ConnectionStatus.Connected, rig.Store.Connections["alice"].Status);
        Assert.Single(rig.Store.Items.Values, i => i.UserId == "alice");
        Assert.DoesNotContain(rig.Logs, l => l.Contains("refresh-") || l.Contains("access-") || l.Contains("64,20"));
    }

    [Fact]
    public async Task A_rate_limit_on_one_account_pauses_only_that_account()
    {
        var (rig, _, second) = await TwoAccountsAsync();
        await rig.Engine.SyncAsync("alice", default);
        rig.Google.GmailFailures.Enqueue(System.Net.HttpStatusCode.TooManyRequests);
        rig.Google.GmailFailures.Enqueue(System.Net.HttpStatusCode.TooManyRequests);
        rig.Google.GmailFailures.Enqueue(System.Net.HttpStatusCode.TooManyRequests);
        rig.Google.GmailFailures.Enqueue(System.Net.HttpStatusCode.TooManyRequests);

        var paused = await rig.Engine.SyncMailboxAsync(new MailboxRef("alice", second.AccountId), default);
        var other = await rig.Engine.SyncMailboxAsync(new MailboxRef("alice", Mailboxes.Main), default);

        Assert.Equal("rate_limited", paused.Code);
        Assert.Equal(SyncEngine.RateLimitNote, rig.Store.Connections[Mailboxes.Key("alice", second.AccountId)].LastError);
        Assert.True(other.Ok);
        Assert.Null(rig.Store.Connections["alice"].LastError);
    }

    [Fact]
    public async Task Each_account_has_its_own_sync_lock()
    {
        var (rig, _, second) = await TwoAccountsAsync();
        Assert.True(await rig.Store.TryClaimSyncAsync(new MailboxRef("alice", Mailboxes.Main), rig.Now, rig.Now.AddMinutes(10), default));

        var busy = await rig.Engine.SyncMailboxAsync(new MailboxRef("alice", Mailboxes.Main), default);
        var free = await rig.Engine.SyncMailboxAsync(new MailboxRef("alice", second.AccountId), default);

        Assert.Equal("busy", busy.Code);
        Assert.True(free.Ok);
    }

    [Fact]
    public async Task One_person_cannot_reach_another_persons_account()
    {
        var (rig, _, second) = await TwoAccountsAsync();
        var t = DateTime.UtcNow.Date;
        rig.Google.AddTo(Work, "m-work", Biller, "Your electricity invoice", Mails.Invoice(t), t.AddDays(-2));
        await rig.Engine.SyncAsync("alice", default);

        // Bob names Alice's account id: nothing of hers is read, synced or removed
        Assert.Null(await rig.Store.GetConnectionAsync("bob", second.AccountId, default));
        Assert.Equal("not_connected", (await rig.Engine.SyncMailboxAsync(new MailboxRef("bob", second.AccountId), default)).Code);
        await rig.Engine.DisconnectAsync("bob", second.AccountId, default);
        Assert.Equal(2, (await rig.Store.GetConnectionsAsync("alice", default)).Count);
        Assert.Empty(rig.Google.Revoked);
        Assert.Empty((await rig.Service.LoadAsync("bob", default)).Items);
        Assert.Empty((await rig.Service.LoadAsync("bob", default)).Connections);

        var item = rig.Store.Items.Values.Single(i => i.UserId == "alice");
        Assert.Null(await rig.Engine.ReprocessAsync("bob", item.Id, default));
    }

    [Fact]
    public async Task Two_people_can_connect_the_same_address_and_keep_separate_records()
    {
        var rig = new CompanionRig();
        Assert.True((await ConnectAsAsync(rig, "alice", "shared.family@example.com")).Ok);
        Assert.True((await ConnectAsAsync(rig, "bob", "shared.family@example.com")).Ok);
        var t = DateTime.UtcNow.Date;
        rig.Google.AddTo("shared.family@example.com", "m-1", Biller, "Your electricity invoice", Mails.Invoice(t), t.AddDays(-2));

        await rig.Engine.SyncAsync("alice", default);
        await rig.Engine.SyncAsync("bob", default);

        Assert.Single(rig.Store.Items.Values, i => i.UserId == "alice");
        Assert.Single(rig.Store.Items.Values, i => i.UserId == "bob");
    }

    [Fact]
    public async Task Accounts_beyond_the_limit_are_refused_and_their_grant_revoked()
    {
        var rig = new CompanionRig();

        for (var i = 1; i <= Mailboxes.MaxPerUser; i++)
        {
            Assert.True((await ConnectAsAsync(rig, "alice", $"box{i}@example.com")).Ok);
        }

        var refused = await ConnectAsAsync(rig, "alice", "one.too.many@example.com");

        Assert.False(refused.Ok);
        Assert.Equal("account_limit", refused.Code);
        Assert.Equal(Mailboxes.MaxPerUser, (await rig.Store.GetConnectionsAsync("alice", default)).Count);
        Assert.Contains("refresh-one.too.many@example.com", rig.Google.Revoked);
    }

    [Fact]
    public async Task A_push_for_the_second_account_syncs_only_that_mailbox()
    {
        var (rig, _, second) = await TwoAccountsAsync();
        rig.PushTopic = "projects/omabilo-test/topics/gmail";
        await rig.Engine.SyncAsync("alice", default);
        var mainSynced = rig.Store.Connections["alice"].LastSuccessUtc;
        var t = DateTime.UtcNow.Date;
        rig.Google.AddTo(Work, "m-push", Biller, "Your electricity invoice", Mails.Invoice(t), t);
        rig.Now = rig.Now.AddMinutes(2);

        var outcome = await rig.Engine.HandlePushAsync(new GmailPushNotice(Work, (ulong)rig.Google.HistoryCounter, "p-1"), default);

        Assert.Equal([new MailboxRef("alice", second.AccountId)], outcome.ToSync);
        Assert.True((await rig.Engine.SyncAfterPushAsync(outcome.ToSync[0], default)).Ok);
        Assert.Single(rig.Store.Items.Values, i => i.UserId == "alice");
        Assert.Equal(mainSynced, rig.Store.Connections["alice"].LastSuccessUtc);
        Assert.Equal(1, rig.Store.Watches[Mailboxes.Key("alice", second.AccountId)].PushCount);
    }

    [Fact]
    public async Task A_record_is_read_again_through_the_account_it_came_from()
    {
        var (rig, _, _) = await TwoAccountsAsync();
        var t = DateTime.UtcNow.Date;
        rig.Google.AddTo(Work, "m-work", Biller, "Your electricity invoice", Mails.Invoice(t), t.AddDays(-2));
        await rig.Engine.SyncAsync("alice", default);
        var item = rig.Store.Items.Values.Single(i => i.UserId == "alice");

        var again = await rig.Engine.ReprocessAsync("alice", item.Id, default);

        Assert.NotNull(again);
        Assert.Equal(64.20m, again!.Amount);
    }

    [Fact]
    public async Task Deleting_all_data_disconnects_every_account()
    {
        var (rig, _, _) = await TwoAccountsAsync();

        await rig.Service.DeleteAllDataAsync("alice", default);

        Assert.Empty(await rig.Store.GetConnectionsAsync("alice", default));
        Assert.Equal(2, rig.Google.Revoked.Count);
    }

    [Fact]
    public void The_main_account_keeps_the_keys_it_had_before_multi_account_support()
    {
        // Existing rows (watch, processed messages, encrypted tokens) stay valid for the migrated account
        Assert.Equal("alice", Mailboxes.Key("alice", Mailboxes.Main));
        Assert.Equal("18c2f1", Mailboxes.MessageKey(Mailboxes.Main, "18c2f1"));
        Assert.Equal("gabc:18c2f1", Mailboxes.MessageKey("gabc", "18c2f1"));
        Assert.Equal("alice#gabc", Mailboxes.Key("alice", "gabc"));
        Assert.Equal(Mailboxes.Main, new SourceRef("x", "", "", DateTime.UtcNow, "", "original").AccountId);
    }

    [Fact]
    public void The_consent_screen_lets_the_person_choose_another_google_account()
    {
        var url = new GoogleClient(new HttpClient()).BuildAuthorizationUrl(CompanionRig.Config, "s", "c");

        Assert.Equal("select_account consent", HttpUtility.ParseQueryString(new Uri(url).Query)["prompt"]);
    }
}
