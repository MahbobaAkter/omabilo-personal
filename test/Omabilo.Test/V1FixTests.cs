using System.Globalization;
using Xunit;

/// <summary>Fixes from the staging check: the accountant draft's month, a document heading in the
/// biller's name, how the VAT answer labels dates, and the Gmail bar's states. Fictional data only.</summary>
public class V1FixTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static string D(DateTime d) => d.ToString("d.M.yyyy", CultureInfo.InvariantCulture);

    private static FinancialItem Bill(string id, DateTime? invoiceDate, DateTime due, decimal amount = 50.20m, decimal? vat = 10.20m) => new()
    {
        Id = id, UserId = "u1", Kind = FinKind.Invoice, Title = "Bill", Counterparty = $"Yritys {id} Oy", Amount = amount, Vat = vat,
        InvoiceDate = invoiceDate, DueDate = due, Confidence = 0.95, AnalysisVersion = 99,
    };

    // ── Accountant draft ────────────────────────────────────────────────────

    [Fact]
    public void The_accountant_draft_names_the_same_month_as_the_pack()
    {
        var pack = FinanceAssistant.ForAccountant([Bill("sep", new(2026, 9, 1), new(2026, 9, 15))], 2026, 9);
        var draft = FinanceAssistant.AccountantDraft(pack);

        Assert.Equal("September 2026", pack.Period);
        Assert.Contains("documents for September 2026", draft);
        Assert.DoesNotContain("October", draft);
        Assert.Contains("Yritys sep Oy", draft);
    }

    [Fact]
    public void An_empty_month_gives_a_draft_that_says_so_instead_of_an_empty_list()
    {
        var pack = FinanceAssistant.ForAccountant([Bill("oct", new(2026, 10, 12), new(2026, 10, 22))], 2026, 9);
        var draft = FinanceAssistant.AccountantDraft(pack);

        Assert.Empty(pack.Sources);
        Assert.Contains("September 2026", pack.Summary);
        Assert.Contains("nothing to list", draft);
        Assert.DoesNotContain("These are the ones I have", draft);

        // This month holds the October bill
        Assert.Equal("oct", Assert.Single(FinanceAssistant.ForAccountant([Bill("oct", new(2026, 10, 12), new(2026, 10, 22))], 2026, 10).Sources).ItemId);
    }

    // ── VAT wording ─────────────────────────────────────────────────────────

    [Fact]
    public void The_VAT_answer_says_whether_a_date_is_the_invoice_date_or_the_due_date()
    {
        var items = new[] { Bill("dated", new(2026, 10, 12), new(2026, 10, 22)), Bill("undated", null, new(2026, 10, 25)) };
        var answer = FinanceAssistant.Answer(AssistantQuestion.VatThisMonth, items, CashFlowPlanner.Plan(items, new CompanionSettings(), ItemRules.Income(items, new CompanionSettings(), Now.Date), Now.Date), Now.Date);

        Assert.StartsWith("Invoice dated 12 Oct 2026", answer.Sources.Single(s => s.ItemId == "dated").Detail);
        Assert.StartsWith("Due 25 Oct 2026", answer.Sources.Single(s => s.ItemId == "undated").Detail);
        Assert.DoesNotContain(answer.Sources, s => s.Detail.StartsWith("Invoice of", StringComparison.Ordinal));
    }

    // ── Biller name ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_heading_on_the_company_line_is_not_part_of_the_billers_name()
    {
        var rig = new CompanionRig();
        var today = rig.Now.Date;
        var text = $"LASKU Testisähkö Oy (fictional test company). Laskun päivämäärä {D(today.AddDays(-7))}. Eräpäivä {D(today.AddDays(3))}. " +
                   "Viitenumero 12344. Veroton 40,00 €. ALV 25,5 % 10,20 €. Yhteensä 50,20 €. Saaja Testisähkö Oy, IBAN FI00 0000 0000 0000 00.";

        var result = await rig.Engine.ImportPastedTextAsync("alice", text, default);

        Assert.Equal(UploadOutcome.Filed, result.Outcome);
        var item = rig.Store.Items[result.ItemId!];
        Assert.Equal("Testisähkö Oy", item.Counterparty);
        Assert.DoesNotContain("LASKU", item.Title);
        Assert.Equal(today.AddDays(-7), item.InvoiceDate);
        Assert.Equal(today.AddDays(3), item.DueDate);
    }

    // ── Gmail bar ───────────────────────────────────────────────────────────

    private static GmailConnection Conn(ConnectionStatus status = ConnectionStatus.Connected, DateTime? lastSuccess = null, string? error = null,
        DateTime? syncLock = null, bool initialScanDone = true, int scanned = 0) => new()
    {
        UserId = "u1", GoogleEmail = "test.user@example.com", Status = status, LastSuccessUtc = lastSuccess, LastError = error,
        SyncLockUntilUtc = syncLock, InitialScanDone = initialScanDone, LastSyncScanned = scanned, ConnectedUtc = Now.AddDays(-2),
    };

    [Fact]
    public void Connected_shows_when_it_last_synced_with_sync_now_available()
    {
        var s = GmailBar.Describe(Conn(lastSuccess: Now.AddMinutes(-12), scanned: 3), null, false, false, Now);

        Assert.Equal((GmailBarKind.Connected, "Gmail connected", "success", GmailBarAction.SyncNow), (s.Kind, s.Title, s.Dot, s.Action));
        Assert.Equal("test.user@example.com · Synced 12 min ago · 3 new messages checked", s.Summary);
        Assert.Null(s.SyncDisabledReason);
        Assert.Null(s.AlertTitle);
    }

    [Fact]
    public void My_sync_and_the_first_scan_show_a_scanning_caption_with_sync_now_disabled()
    {
        var mine = GmailBar.Describe(Conn(lastSuccess: Now.AddHours(-1)), null, true, false, Now);
        var first = GmailBar.Describe(Conn(initialScanDone: false), null, false, true, Now);

        foreach (var s in new[] { mine, first })
        {
            Assert.Equal(GmailBarKind.MySync, s.Kind);
            Assert.Contains("Looking for financial messages", s.Summary);
            Assert.NotNull(s.SyncDisabledReason);
        }
    }

    [Fact]
    public void A_background_sync_disables_sync_now_and_says_why()
    {
        var s = GmailBar.Describe(Conn(lastSuccess: Now.AddHours(-1), syncLock: Now.AddMinutes(2)), null, false, false, Now);

        Assert.Equal(GmailBarKind.BackgroundSync, s.Kind);
        Assert.Contains("Syncing in the background", s.Summary);
        Assert.Contains("already checking", s.SyncDisabledReason);

        // An expired lock is not a sync in progress
        Assert.Equal(GmailBarKind.Connected, GmailBar.Describe(Conn(lastSuccess: Now.AddHours(-1), syncLock: Now.AddMinutes(-1)), null, false, false, Now).Kind);
    }

    [Fact]
    public void First_scan_pending_when_nothing_has_synced_yet()
    {
        var s = GmailBar.Describe(Conn(), null, false, false, Now);
        Assert.Equal(GmailBarKind.FirstScanPending, s.Kind);
        Assert.EndsWith("First scan pending", s.Summary);
    }

    [Fact]
    public void Error_and_reconnect_say_when_the_last_successful_sync_was()
    {
        var error = GmailBar.Describe(Conn(ConnectionStatus.Error, Now.AddHours(-3), "Gmail didn't answer"), null, false, false, Now);
        Assert.Equal((GmailBarKind.Error, "Gmail sync problem", "warning", GmailBarAction.SyncNow), (error.Kind, error.Title, error.Dot, error.Action));
        Assert.Contains("Last successful sync 3 h ago", error.Summary);
        Assert.Equal("Gmail didn't answer. Retrying automatically.", error.AlertText);

        var reconnect = GmailBar.Describe(Conn(ConnectionStatus.NeedsReconnect, null), null, false, false, Now);
        Assert.Equal((GmailBarKind.NeedsReconnect, "error", GmailBarAction.Reconnect), (reconnect.Kind, reconnect.Dot, reconnect.Action));
        Assert.Contains("No successful sync yet", reconnect.Summary);
        Assert.Contains("Reconnect Gmail", reconnect.AlertText);
        Assert.DoesNotContain("Synced", reconnect.Summary);
    }

    [Fact]
    public void Rate_limited_keeps_sync_now_and_explains_the_pause()
    {
        var s = GmailBar.Describe(Conn(lastSuccess: Now.AddMinutes(-5), error: SyncEngine.RateLimitNote), null, false, false, Now);
        Assert.Equal((GmailBarKind.RateLimited, "warning", GmailBarAction.SyncNow), (s.Kind, s.Dot, s.Action));
        Assert.Equal("Gmail asked Omabilo to slow down", s.AlertTitle);
    }

    [Fact]
    public void An_active_trial_is_a_badge_until_its_last_week_then_a_notice()
    {
        var c = Conn(lastSuccess: Now.AddMinutes(-5));

        var early = GmailBar.Describe(c, Now.AddDays(20), false, false, Now);
        Assert.Equal("Free until 8 Nov 2026", early.TrialBadge);
        Assert.False(early.ShowTrialNotice);

        var late = GmailBar.Describe(c, Now.AddDays(5), false, false, Now);
        Assert.Null(late.TrialBadge);
        Assert.True(late.ShowTrialNotice);
        Assert.Equal(GmailBarAction.SyncNow, late.Action);

        var premium = GmailBar.Describe(c, null, false, false, Now);
        Assert.Null(premium.TrialBadge);
        Assert.False(premium.ShowTrialNotice);
    }

    [Fact]
    public void An_ended_trial_shows_gmail_paused_with_premium_instead_of_sync_now()
    {
        var s = GmailBar.Describe(Conn(lastSuccess: Now.AddDays(-5), syncLock: Now.AddMinutes(2)), Now.AddDays(-5), false, false, Now);

        Assert.Equal(GmailBarKind.TrialEnded, s.Kind);
        Assert.Equal("Gmail paused: free trial ended", s.Title);
        Assert.Equal("neutral", s.Dot);
        Assert.Equal(GmailBarAction.GetPremium, s.Action);
        Assert.True(s.ShowTrialNotice);
        Assert.Contains("Last successful sync 5 days ago", s.Summary);
        Assert.DoesNotContain("Synced", s.Summary);
    }
}
