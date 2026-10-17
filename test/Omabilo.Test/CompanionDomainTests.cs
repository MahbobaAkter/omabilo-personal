using System.Globalization;
using Xunit;

/// <summary>Fictional emails used across the companion tests. No real people, accounts or companies.</summary>
public static class Mails
{
    public static string D(DateTime d) => d.ToString("d.M.yyyy", CultureInfo.InvariantCulture);

    public static MailMessage Make(string from, string subject, string body, DateTime? received = null, string id = "m1") =>
        new(id, "t" + id, from, subject, received ?? DateTime.UtcNow, body.Length > 100 ? body[..100] : body, body, []);

    public static string Invoice(DateTime today, string number = "RE-2026-48213", decimal amount = 64.20m, int dueIn = 12, string reference = "20241182") =>
        $"""
        Hello,

        Here is your invoice.

        Biller: Revontuli Energy Oy
        For: Electricity, September (182 kWh)
        Billing period: 1.9.2026 - 30.9.2026
        Invoice number: {number}
        Invoice date: {D(today.AddDays(-3))}
        Amount due: {amount.ToString("0.00", new CultureInfo("fi-FI"))} €
        Due date: {D(today.AddDays(dueIn))}
        Reference: {reference}
        IBAN: FI21 1234 5600 0007 85
        """;

    public static string Payslip(DateTime payDate, decimal gross = 3200m, decimal net = 2412.40m, decimal tax = 547.20m) =>
        $"""
        Palkkalaskelma

        Työnantaja: Kuura Software Oy
        Palkkakausi: 1.9.2026 - 30.9.2026
        Bruttopalkka {gross.ToString("0.00", new CultureInfo("fi-FI"))} €
        Veroprosentti 17,1 %
        Ennakonpidätys {tax.ToString("0.00", new CultureInfo("fi-FI"))} €
        Työeläkemaksu 226,40 €
        Työttömyysvakuutusmaksu 14,00 €
        Nettopalkka {net.ToString("0.00", new CultureInfo("fi-FI"))} €
        Palkkapäivä {D(payDate)}
        """;

    public const string Newsletter =
        """
        Autumn sale ends Sunday! Shop now and get 30% off all jackets, from 49 €.
        Free shipping on orders over 60 €. Limited time only.
        Unsubscribe from this newsletter.
        """;

    public const string Personal = "Hi! Are we still on for coffee on Friday? I found that book you asked about.";

    public static string Receipt(DateTime date, decimal total = 42.80m, string order = "K-558210") =>
        $"""
        Thank you for your order!

        Merchant: Nordic Gear Shop
        Order number: {order}
        Purchase date: {D(date)}
        Total: {total.ToString("0.00", new CultureInfo("fi-FI"))} €
        VAT 25,5 %: 8,70 €
        """;
}

public class CompanionClassificationTests
{
    [Fact]
    public void Invoice_is_financial_with_reasons()
    {
        var c = MessageClassifier.Classify("Revontuli Energy <billing@revontuli.example>", "Your electricity invoice", Mails.Invoice(DateTime.UtcNow));
        Assert.True(c.IsFinancial);
        Assert.Equal(FinKind.Invoice, c.Kind);
        Assert.True(c.Confidence >= 0.8);
        Assert.Contains(c.Reasons, r => r.Contains("invoice"));
    }

    [Fact]
    public void Marketing_with_prices_and_personal_mail_are_not_financial()
    {
        Assert.False(MessageClassifier.Classify("Shop <news@shop.example>", "Autumn sale!", Mails.Newsletter).IsFinancial);
        Assert.False(MessageClassifier.Classify("Friend <friend@example.com>", "Friday", Mails.Personal).IsFinancial);
    }

    [Theory]
    [InlineData("Maksumuistutus", "Emme ole saaneet maksuasi. Maksettava 44,90 €. Eräpäivä 20.10.2026", FinKind.PaymentReminder)]
    [InlineData("Perintäilmoitus", "Perintätoimisto Kivi Oy: saatava 120,00 €", FinKind.DebtCollection)]
    [InlineData("Palkkalaskelma lokakuu", "Nettopalkka 2 412,40 €", FinKind.Payslip)]
    [InlineData("Verotuspäätös 2025", "Verohallinto: jäännösvero 312,00 €", FinKind.TaxNotice)]
    [InlineData("Refund processed", "Your refund of 19,90 € has been issued.", FinKind.Refund)]
    [InlineData("Payment received", "Thank you for your payment of 64,20 € for invoice RE-1.", FinKind.PaymentConfirmation)]
    [InlineData("Your order", "Thank you for your order! Total 42,80 €", FinKind.Receipt)]
    [InlineData("Your plan renews soon", "Your subscription will renew on 1.11.2026 for 12,99 €.", FinKind.Subscription)]
    public void Kinds_are_recognised_in_finnish_and_english(string subject, string body, FinKind expected) =>
        Assert.Equal(expected, MessageClassifier.Classify("Sender <x@example.com>", subject, body).Kind);
}

public class CompanionExtractionTests
{
    private static readonly DateTime Today = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static FinancialItem Extract(string subject, string body, string from = "Billing <billing@biller.example>") =>
        RecordExtractor.Extract("u1", Mails.Make(from, subject, body), MessageClassifier.Classify(from, subject, body), Today);

    [Fact]
    public void Invoice_fields_are_read_with_payment_instructions()
    {
        var i = Extract("Your invoice", Mails.Invoice(Today));
        Assert.Equal("Revontuli Energy Oy", i.Counterparty);
        Assert.Equal("RE-2026-48213", i.InvoiceNumber);
        Assert.Equal(64.20m, i.Amount);
        Assert.Equal(Today.AddDays(12), i.DueDate);
        Assert.Equal(Today.AddDays(-3), i.InvoiceDate);
        Assert.Contains("1.9.2026", i.Period);
        Assert.Contains("FI21", i.PaymentInstructions);
        Assert.Equal(Categories.Utilities, i.Category);
        Assert.Equal("EUR", i.Currency);
        Assert.False(i.Suspicious);
        Assert.Single(i.Sources);
        Assert.True(i.Sources[0].Excerpt.Length <= RecordExtractor.ExcerptLength);
    }

    [Fact]
    public void Conflicting_totals_and_missing_fields_are_flagged_not_guessed()
    {
        var conflict = Extract("Invoice", "Biller: Kuusi Water Oy\nAmount due: 30,00 €\nTotal: 45,00 €\nDue date: 30.10.2026\nReference: 12345672");
        Assert.Contains(conflict.Flags, f => f.Contains("different totals"));

        var missing = Extract("Invoice", "Biller: Kuusi Water Oy\nPlease pay your water invoice soon.");
        Assert.Null(missing.Amount);
        Assert.Null(missing.DueDate);
        Assert.Contains(missing.Missing, m => m.Contains("due date"));
        Assert.Equal(DisplayStatus.NeedsReview, ItemRules.StatusOf(missing, Today).Status);
    }

    [Fact]
    public void Payslip_fields_are_read_and_the_tax_percentage_is_not_taken_as_tax()
    {
        var i = Extract("Palkkalaskelma", Mails.Payslip(Today.AddDays(8)));
        Assert.Equal(FinKind.Payslip, i.Kind);
        Assert.Equal("Kuura Software Oy", i.Counterparty);
        Assert.Equal(3200m, i.Gross);
        Assert.Equal(2412.40m, i.Net);
        Assert.Equal(547.20m, i.Tax);
        Assert.Equal(240.40m, i.OtherDeductions);
        Assert.Equal(Today.AddDays(8), i.EventDate);
        Assert.Equal(IncomeEvidence.Payslip, i.IncomeEvidence);
        Assert.Empty(i.Flags);
        Assert.Contains(i.Evidence, e => e.Contains("doesn't prove"));
        Assert.Equal(DisplayStatus.ExpectedIncome, ItemRules.StatusOf(i, Today).Status);
    }

    [Fact]
    public void Payslip_whose_deductions_do_not_add_up_is_flagged()
    {
        var i = Extract("Palkkalaskelma", Mails.Payslip(Today, net: 2600m));
        Assert.Contains(i.Flags, f => f.Contains("doesn't match net salary"));
    }

    [Fact]
    public void Payslip_without_net_or_date_lists_what_is_unknown()
    {
        var i = Extract("Palkkalaskelma", "Palkkalaskelma\nBruttopalkka 3 200,00 €");
        Assert.Null(i.Net);
        Assert.Contains(i.Missing, m => m.Contains("Net salary"));
        Assert.Contains(i.Missing, m => m.Contains("Payment date"));
    }

    [Fact]
    public void Receipt_is_spending_with_vat_order_and_category()
    {
        var i = Extract("Thank you for your order", Mails.Receipt(Today.AddDays(-1)), "Nordic Gear <orders@nordicgear.example>");
        Assert.Equal(FinKind.Receipt, i.Kind);
        Assert.Equal(42.80m, i.Amount);
        Assert.Equal(8.70m, i.Vat);
        Assert.Equal("K-558210", i.OrderRef);
        Assert.Equal("Nordic Gear Shop", i.Counterparty);
        Assert.Equal(Categories.Shopping, i.Category);
        Assert.Equal(DisplayStatus.Recorded, ItemRules.StatusOf(i, Today).Status);
    }

    [Fact]
    public void Collection_notice_is_never_treated_as_proof_of_debt()
    {
        var i = Extract("Perintäilmoitus", "Perintätoimisto Kivi Oy\nSaatava: lasku 4471\nMaksettava 120,00 €\nEräpäivä 30.10.2026\nViitenumero 12345672");
        Assert.Equal(FinKind.DebtCollection, i.Kind);
        Assert.Contains(i.Flags, f => f.Contains("doesn't prove the debt is valid"));
    }

    [Fact]
    public void Foreign_currency_is_kept_and_not_counted()
    {
        var i = Extract("Invoice", "Biller: Globex Inc\nAmount due: 120.00 USD\nDue date: 2026-10-30\nInvoice number: GX-1");
        Assert.Equal("USD", i.Currency);
        Assert.False(ItemRules.CountsAsOpenObligation(i));
    }

    [Fact]
    public void Excerpt_hides_card_numbers_and_identity_codes()
    {
        var e = RecordExtractor.Excerpt("Card 4111 1111 1111 1111 charged 10,00 €\nCustomer 131052-308T total 10,00 €");
        Assert.DoesNotContain("4111", e);
        Assert.DoesNotContain("131052", e);
    }

    [Fact]
    public void Html_email_becomes_readable_lines() =>
        Assert.Equal("Amount due: 30,00 €\nDue date: 1.11.2026", GoogleClient.HtmlToText("<style>p{}</style><p>Amount due: 30,00&nbsp;€</p><div>Due date: 1.11.2026</div><script>x()</script>"));
}

public class CompanionRulesTests
{
    private static readonly DateTime Today = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static FinancialItem Bill(string id, string party, decimal amount, DateTime due, string? number = null, string? reference = null, FinKind kind = FinKind.Invoice) => new()
    {
        Id = id, UserId = "u1", Kind = kind, Title = $"Invoice from {party}", Counterparty = party, Amount = amount, DueDate = due,
        InvoiceNumber = number, Reference = reference, Confidence = 0.9, CreatedUtc = Today, UpdatedUtc = Today,
        Sources = [new SourceRef("g-" + id, party, "s", Today, "x", "original")],
    };

    [Fact]
    public void Statuses_follow_dates_and_never_claim_unpaid()
    {
        Assert.Equal(DisplayStatus.Overdue, ItemRules.StatusOf(Bill("a", "Kuusi", 10, Today.AddDays(-2)), Today).Status);
        Assert.Contains("doesn't prove", ItemRules.StatusOf(Bill("a", "Kuusi", 10, Today.AddDays(-2)), Today).Why);
        Assert.Equal(DisplayStatus.DueSoon, ItemRules.StatusOf(Bill("b", "Kuusi", 10, Today.AddDays(5)), Today).Status);
        Assert.Equal(DisplayStatus.Upcoming, ItemRules.StatusOf(Bill("c", "Kuusi", 10, Today.AddDays(20)), Today).Status);
        Assert.Equal(DisplayStatus.ReportedPaid, ItemRules.StatusOf(Bill("d", "Kuusi", 10, Today.AddDays(-2)) with { UserState = UserState.ReportedPaid }, Today).Status);
        Assert.Equal(DisplayStatus.Disputed, ItemRules.StatusOf(Bill("e", "Kuusi", 10, Today) with { UserState = UserState.Disputed }, Today).Status);
    }

    [Fact]
    public void A_reminder_for_the_same_invoice_number_joins_it_and_keeps_corrections()
    {
        var original = CompanionEdits.Apply(Bill("a", "Tähtiverkko Oy", 39.90m, Today.AddDays(-12), "TV-77120"), new ItemEdit(Category: Categories.PhoneInternet), Today);
        var reminder = Bill("b", "Tähtiverkko Oy", 44.90m, Today.AddDays(2), "TV-77120", kind: FinKind.PaymentReminder) with { OriginalDueDate = Today.AddDays(-12) };

        var same = ItemRules.FindSameObligation([original], reminder);
        Assert.Equal("a", same?.Id);

        var merged = ItemRules.MergeInto(original, reminder, Today);
        Assert.Equal(FinKind.PaymentReminder, merged.Kind);
        Assert.Equal(44.90m, merged.Amount);
        Assert.Equal(Today.AddDays(2), merged.DueDate);
        Assert.Equal(Categories.PhoneInternet, merged.Category);
        Assert.Equal(2, merged.Sources.Count);
        Assert.Equal("reminder", merged.Sources[1].Role);
    }

    [Fact]
    public void Monthly_bills_sharing_one_reference_are_not_merged()
    {
        var october = Bill("a", "Kotikatu Housing Oy", 820m, Today.AddDays(-16), reference: "12345672");
        var november = Bill("b", "Kotikatu Housing Oy", 820m, Today.AddDays(15), reference: "12345672");

        Assert.Null(ItemRules.FindSameObligation([october], november));
        Assert.Null(ItemRules.FindProbableDuplicate([october], november));
    }

    [Fact]
    public void Same_biller_amount_and_due_date_without_ids_is_a_possible_duplicate()
    {
        var a = Bill("a", "Kuusi Water Oy", 30m, Today.AddDays(10));
        var b = Bill("b", "Kuusi Water Oy", 30m, Today.AddDays(11));
        Assert.Equal("a", ItemRules.FindProbableDuplicate([a], b)?.Id);
    }

    [Fact]
    public void A_payment_confirmation_finds_its_bill()
    {
        var bill = Bill("a", "Revontuli Energy Oy", 64.20m, Today.AddDays(5), "RE-1");
        var confirmation = Bill("c", "Revontuli Energy", 64.20m, Today, "RE-1", kind: FinKind.PaymentConfirmation) with { DueDate = null, EventDate = Today };
        Assert.Equal("a", ItemRules.MatchPaymentConfirmation([bill], confirmation)?.Id);
    }

    [Fact]
    public void Recurring_costs_need_two_monthly_occurrences()
    {
        var items = new[]
        {
            Bill("a", "Nimbus Mobile Oy", 19.90m, Today.AddMonths(-2)),
            Bill("b", "Nimbus Mobile Oy", 19.90m, Today.AddMonths(-1)),
            Bill("c", "Kuusi Water Oy", 30m, Today.AddMonths(-1)),
        };

        var r = Assert.Single(ItemRules.Recurring(items, Today));
        Assert.Equal("Nimbus Mobile Oy", r.Counterparty);
        Assert.Equal(Today, r.NextExpected);
    }

    [Fact]
    public void Payday_comes_from_the_person_then_a_payslip_then_a_pattern()
    {
        FinancialItem Pay(string id, DateTime d) => new() { Id = id, UserId = "u1", Kind = FinKind.Payslip, Net = 2400m, EventDate = d, IncomeEvidence = IncomeEvidence.Payslip, Counterparty = "Kuura", CreatedUtc = d };

        var pattern = ItemRules.Income([Pay("a", new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc)), Pay("b", new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc))], new CompanionSettings(), Today);
        // 25 Oct 2026 is a Sunday, so the salary comes on Friday the 23rd
        Assert.Equal(new DateTime(2026, 10, 23), pattern.NextPayday);
        Assert.Contains("weekend", pattern.PaydayBasis);
        Assert.Equal(IncomeEvidence.Estimated, pattern.PaydayEvidence);

        var one = ItemRules.Income([Pay("a", new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc))], new CompanionSettings(), Today);
        Assert.Null(one.NextPayday);
        Assert.Contains("isn't enough", one.PaydayBasis);

        var scheduled = ItemRules.Income([Pay("a", Today.AddDays(6))], new CompanionSettings(), Today);
        Assert.Equal(IncomeEvidence.Scheduled, scheduled.PaydayEvidence);

        var user = ItemRules.Income([Pay("a", Today.AddDays(6))], new CompanionSettings { PaydayDay = 15, NetSalary = 2500m }, Today);
        Assert.Equal(new DateTime(2026, 11, 13), user.NextPayday);
        Assert.Equal(2500m, user.ExpectedNet);
        Assert.Equal(IncomeEvidence.UserEntered, user.NetEvidence);
    }
}

public class CashFlowTests
{
    private static readonly DateTime Today = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static FinancialItem Bill(string id, string party, decimal amount, int dueIn, UserState state = UserState.None, bool suspicious = false) => new()
    {
        Id = id, UserId = "u1", Kind = FinKind.Invoice, Title = $"Invoice from {party}", Counterparty = party, Amount = amount,
        DueDate = Today.AddDays(dueIn), Confidence = 0.9, UserState = state, Suspicious = suspicious, CreatedUtc = Today, UpdatedUtc = Today,
    };

    [Fact]
    public void Projected_remainder_is_balance_plus_income_minus_bills_and_spending()
    {
        var items = new List<FinancialItem>
        {
            Bill("a", "Revontuli Energy Oy", 64.20m, 5),
            Bill("b", "Kuusi Water Oy", 30m, -2),
            Bill("paid", "Nimbus", 19.90m, 3, UserState.ReportedPaid),
            Bill("odd", "Unknown", 89m, 1, suspicious: true),
            Bill("later", "Kotikatu", 820m, 40),
        };
        var settings = new CompanionSettings { Balance = 900m, BalanceAsOfUtc = Today, PaydayDay = 25, EverydaySpendPerDay = 20m };
        var income = ItemRules.Income(items, settings, Today);
        var plan = CashFlowPlanner.Plan(items, settings, income, Today);

        Assert.Equal(new DateTime(2026, 10, 22), plan.End);
        Assert.Equal(94.20m, plan.ObligationsTotal);
        Assert.Equal(120m, plan.EverydayTotal);
        Assert.Equal(900m - 94.20m - 120m, plan.ProjectedRemainder);
        Assert.Contains(plan.NotCounted, n => n.ItemId == "odd" && n.Reason.Contains("warning signs"));
        Assert.DoesNotContain(plan.Obligations, o => o.ItemId is "paid" or "later");
    }

    [Fact]
    public void A_payslip_paid_around_payday_is_the_next_salary_not_extra_income()
    {
        var slip = new FinancialItem { Id = "p", UserId = "u1", Kind = FinKind.Payslip, Title = "Payslip", Net = 2412.40m, Amount = 2412.40m, EventDate = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc), IncomeEvidence = IncomeEvidence.Payslip };
        var settings = new CompanionSettings { Balance = 500m, BalanceAsOfUtc = Today, PaydayDay = 25 };
        var plan = CashFlowPlanner.Plan([slip], settings, ItemRules.Income([slip], settings, Today), Today);

        Assert.Equal(0m, plan.IncomeTotal);
        Assert.Equal(500m, plan.ProjectedRemainder);
        Assert.Single(plan.NotCounted, n => n.Label.StartsWith("Next salary"));
    }

    [Fact]
    public void Without_a_balance_no_remainder_is_invented()
    {
        var plan = CashFlowPlanner.Plan([Bill("a", "Kuusi", 30m, 3)], new CompanionSettings(), ItemRules.Income([], new CompanionSettings(), Today), Today);
        Assert.Null(plan.ProjectedRemainder);
        Assert.Contains("Enter your current balance", plan.RemainderExplanation);
        Assert.Equal("Next 30 days (payday not known)", plan.PeriodLabel);
    }

    [Fact]
    public void A_monthly_cost_is_counted_once_whether_or_not_its_bill_has_arrived()
    {
        var settings = new CompanionSettings { Balance = 100m, BalanceAsOfUtc = Today };
        var paid = new List<FinancialItem>
        {
            Bill("m1", "Nimbus Mobile Oy", 19.90m, -45, UserState.ReportedPaid),
            Bill("m2", "Nimbus Mobile Oy", 19.90m, -15, UserState.ReportedPaid),
        };

        // Before the bill arrives: one estimate
        var before = CashFlowPlanner.Plan(paid, settings, ItemRules.Income([], settings, Today), Today);
        Assert.Equal(19.90m, before.RecurringTotal);
        Assert.Equal(0m, before.ObligationsTotal);
        Assert.Equal(AmountBasis.Estimate, Assert.Single(before.Recurring).Basis);

        // Once it arrives: the real bill, and no estimate on top
        var after = CashFlowPlanner.Plan([.. paid, Bill("m3", "Nimbus Mobile Oy", 19.90m, 16)], settings, ItemRules.Income([], settings, Today), Today);
        Assert.Equal(0m, after.RecurringTotal);
        Assert.Equal(19.90m, after.ObligationsTotal);
        Assert.Equal(80.10m, after.ProjectedRemainder);
    }

    [Fact]
    public void Income_already_in_the_balance_is_not_added_again()
    {
        var pay = new FinancialItem { Id = "p", UserId = "u1", Kind = FinKind.Salary, Title = "Salary", Net = 2400m, Amount = 2400m, EventDate = Today, IncomeEvidence = IncomeEvidence.ReportedReceived };
        var settings = new CompanionSettings { Balance = 2500m, BalanceAsOfUtc = Today };
        var plan = CashFlowPlanner.Plan([pay], settings, ItemRules.Income([pay], settings, Today), Today);

        Assert.Equal(0m, plan.IncomeTotal);
        Assert.Contains(plan.NotCounted, n => n.Reason.Contains("Already part of the balance"));
    }

    [Fact]
    public void A_shortfall_produces_a_practical_suggestion()
    {
        var items = new List<FinancialItem> { Bill("rent", "Kotikatu Housing Oy", 820m, 2) };
        var settings = new CompanionSettings { Balance = 300m, BalanceAsOfUtc = Today };
        var income = ItemRules.Income(items, settings, Today);
        var plan = CashFlowPlanner.Plan(items, settings, income, Today);
        var tips = CashFlowPlanner.Suggest(items, plan, income, settings, Today);

        Assert.True(plan.Shortfall);
        Assert.Contains(tips, t => t.Title.Contains("more than your money") && t.Why.Contains("payment plan"));
        Assert.Contains(tips, t => t.Title.Contains("due"));
    }

    [Fact]
    public void Suspicious_bills_get_a_verify_suggestion_and_no_pay_advice()
    {
        var items = new List<FinancialItem> { Bill("odd", "Unknown", 89m, 1, suspicious: true) with { Flags = ["It asks you to use a link."] } };
        var settings = new CompanionSettings();
        var income = ItemRules.Income(items, settings, Today);
        var tips = CashFlowPlanner.Suggest(items, CashFlowPlanner.Plan(items, settings, income, Today), income, settings, Today);

        Assert.Contains(tips, t => t.Title.StartsWith("Verify"));
        Assert.DoesNotContain(tips, t => t.ItemIds.Contains("odd") && t.Why.Contains("pay it in your bank"));
    }
}
