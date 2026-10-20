using Xunit;

/// <summary>Which emails count as financial: real bills, receipts and salaries in; newsletters, adverts,
/// job alerts and footer words out. Fictional senders and amounts only.</summary>
public class EmailFilterTests
{
    private static readonly string[] Promotions = ["INBOX", "CATEGORY_PROMOTIONS"];
    private static readonly string[] Updates = ["INBOX", "CATEGORY_UPDATES"];

    [Theory]
    // A newsletter's footer names subscriptions and refunds; its prices aren't bills
    [InlineData("Shop <news@shop.example>", "This week's picks", "Our autumn collection is here. Jackets from 89 €.\nManage your subscription · Privacy policy · Refund policy\nUnsubscribe")]
    // A job alert mentions a salary with an amount; it isn't income
    [InlineData("Jobs <alerts@jobs.example>", "New jobs for you", "Developer, Helsinki. Salary 4 200 € / month. Apply now.")]
    // "Bill" the person, and money among friends
    [InlineData("Anna <anna@example.com>", "Bill's birthday", "Let's each bring 10 € for Bill's present.")]
    // A shipping notice names no amount to pay
    [InlineData("Store <orders@store.example>", "Your order has shipped", "Your parcel is on its way and arrives on Thursday.")]
    // A service update mentions renewal without any charge
    [InlineData("Cloud <hello@cloud.example>", "What's new", "We've improved how renewal reminders look in the app.")]
    public void Ordinary_mail_is_not_financial(string from, string subject, string body) =>
        Assert.False(MessageClassifier.Classify(from, subject, body, Updates).IsFinancial);

    [Fact]
    public void Promotions_are_left_out_unless_they_carry_real_invoice_details()
    {
        Assert.False(MessageClassifier.Classify("Telco <offers@telco.example>", "Save on your bill", "Switch now and get 20% off: from 9,90 € a month.", Promotions).IsFinancial);

        var invoice = MessageClassifier.Classify("Telco <billing@telco.example>", "Your invoice", "Amount due: 29,90 €\nDue date: 5.11.2026\nReference: 12345672", Promotions);
        Assert.Equal((true, FinKind.Invoice), (invoice.IsFinancial, invoice.Kind));
    }

    [Fact]
    public void Spam_is_never_financial() =>
        Assert.False(MessageClassifier.Classify("x <x@example.com>", "Invoice overdue", "Pay 499 € now. Reference 12345672", ["SPAM"]).IsFinancial);

    [Theory]
    [InlineData("Elisa <e-lasku@elisa.example>", "Uusi e-lasku verkkopankissasi", "Sinulle on saapunut e-lasku: Elisa Oyj 39,90 €, eräpäivä 5.11.2026.", FinKind.Invoice)]
    [InlineData("Kuura Energia <laskutus@kuura.example>", "Laskusi on saapunut", "Lasku on liitteenä.", FinKind.Invoice)]
    [InlineData("Shop <orders@shop.example>", "Kuitti ostoksestasi", "Kiitos ostoksesta!\nYhteensä 23,45 €", FinKind.Receipt)]
    [InlineData("Shop <orders@shop.example>", "Your refund", "We've refunded 19,90 € to your card.", FinKind.Refund)]
    [InlineData("Bank <noreply@bank.example>", "Palkka on maksettu", "Palkkasi 2 412,40 € on maksettu tilillesi 15.10.2026.", FinKind.Salary)]
    [InlineData("Payroll <payroll@kuura.example>", "Your salary has been paid", "We've paid your October salary of €2,850.00 to your account on 25 October 2026.", FinKind.Salary)]
    public void Real_financial_mail_is_found(string from, string subject, string body, FinKind kind)
    {
        var c = MessageClassifier.Classify(from, subject, body, Updates);
        Assert.Equal((true, kind), (c.IsFinancial, c.Kind));
    }

    [Fact]
    public void A_payslip_sent_as_a_locked_pdf_is_found_from_its_file_name() =>
        Assert.Equal(FinKind.Payslip, MessageClassifier.Classify("HR <hr@kuura.example>", "Lokakuu 2026", "Liitteenä.", Updates, ["palkkalaskelma_10_2026.pdf"]).Kind);

    [Theory]
    [InlineData("Palkka on maksettu", "Palkkasi 2 412,40 € on maksettu tilillesi 15.10.2026.", 2412.40, 15)]
    [InlineData("Your salary has been paid", "We've paid your October salary of €2,850.00 to your account on 25 October 2026.", 2850.00, 25)]
    public void A_salary_note_gives_the_amount_and_the_day_it_was_paid(string subject, string body, decimal net, int day)
    {
        var mail = Mails.Make("Payroll <payroll@kuura.example>", subject, body, new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc));
        var item = RecordExtractor.Extract("u1", mail, MessageClassifier.Classify(mail.From, subject, body), new DateTime(2026, 10, 26));

        Assert.Equal((net, net), (item.Net, item.Amount));
        Assert.Equal(new DateTime(2026, 10, day), item.EventDate);
        Assert.Equal(IncomeEvidence.ReportedReceived, item.IncomeEvidence);
        Assert.True(item.IsIncome);
    }

    [Fact]
    public void A_salary_paid_after_the_balance_was_entered_is_added_once()
    {
        var today = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var settings = new CompanionSettings { Balance = 300m, BalanceAsOfUtc = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), PaydayDay = 15 };
        FinancialItem Pay(string id, FinKind kind) => new()
        {
            Id = id, UserId = "u1", Kind = kind, Title = "Salary from Kuura Software", Net = 2412.40m, Amount = 2412.40m,
            EventDate = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc), IncomeEvidence = IncomeEvidence.ReportedReceived,
        };
        var items = new List<FinancialItem> { Pay("bank-note", FinKind.Salary), Pay("payslip", FinKind.Payslip) };

        var plan = CashFlowPlanner.Plan(items, settings, ItemRules.Income(items, settings, today), today);

        var line = Assert.Single(plan.Income);
        Assert.Equal(2412.40m, line.Amount);
        Assert.Equal(300m + 2412.40m, plan.ProjectedRemainder + plan.ObligationsTotal + plan.RecurringTotal + plan.EverydayTotal);
    }

    [Fact]
    public void Text_with_a_null_character_is_saved_without_it() =>
        Assert.Equal("Lasku 12,00 €", CompanionJson.Clean("Lasku\0 12,00 €"));
}

public class EmailFilterSyncTests
{
    [Fact]
    public async Task A_Gmail_rate_limit_pauses_the_scan_for_a_few_minutes_instead_of_failing_it()
    {
        var rig = new CompanionRig();
        rig.Google.Add("m1", "Kuura Energia <laskut@kuura.example>", "Lasku 2026-77", $"Summa 58,40 EUR\nEräpäivä {rig.Now.AddDays(10):d.M.yyyy}", rig.Now.AddHours(-1));
        await rig.ConnectAsync("alice");
        for (var i = 0; i < 20; i++) rig.Google.GmailFailures.Enqueue(System.Net.HttpStatusCode.TooManyRequests);

        var paused = await rig.Engine.SyncAsync("alice", default);

        Assert.Equal((false, "rate_limited"), (paused.Ok, paused.Code));
        var c = rig.Store.Connections["alice"];
        Assert.Equal(ConnectionStatus.Connected, c.Status);
        Assert.Equal(SyncEngine.RateLimitNote, c.LastError);
        Assert.Equal(rig.Now.AddMinutes(3), c.NextAttemptUtc);

        rig.Google.GmailFailures.Clear();
        Assert.True((await rig.Engine.SyncAsync("alice", default)).Ok);
        Assert.Null(rig.Store.Connections["alice"].LastError);
        Assert.Single(rig.Store.Items.Values);
    }

    [Fact]
    public async Task A_newsletter_filed_by_older_rules_is_removed_but_one_the_person_touched_is_kept()
    {
        var rig = new CompanionRig();
        const string footer = "Our autumn collection is here. Jackets from 89 €.\nManage your subscription · Refund policy\nUnsubscribe";
        rig.Google.Add("news-1", "Shop <news@shop.example>", "This week's picks", footer, rig.Now.AddDays(-2));
        rig.Google.Add("news-2", "Shop <news@shop.example>", "More picks", footer, rig.Now.AddDays(-1));
        await rig.ConnectAsync("alice");
        await rig.Engine.SyncAsync("alice", default);
        Assert.Empty(rig.Store.Items);

        FinancialItem Old(string id, string gmailId, UserState state) => new()
        {
            Id = id, UserId = "alice", Kind = FinKind.Subscription, Title = "Subscription charge from Shop", Amount = 89m, AnalysisVersion = 2, UserState = state,
            Sources = [new SourceRef(gmailId, "Shop <news@shop.example>", "This week's picks", rig.Now.AddDays(-2), "Jackets from 89 €", "original")],
        };
        await rig.Store.SaveItemAsync(Old("it-junk", "news-1", UserState.None), default);
        await rig.Store.SaveItemAsync(Old("it-kept", "news-2", UserState.Reviewed), default);

        await rig.Engine.SyncAsync("alice", default);

        Assert.False(rig.Store.Items.ContainsKey("it-junk"));
        Assert.True(rig.Store.Items.ContainsKey("it-kept"));
        Assert.Contains(rig.Store.Audit, a => a.Entry.Action == "item_removed_not_financial");
    }
}
