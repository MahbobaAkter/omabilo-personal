using Xunit;

// The Financial Inbox keeps what Omabilo collected; My Money shows the same records by meaning.
// Each record lands in at most one My Money tab, so no tab total counts it twice. Fictional only.

public class MyMoneyTests
{
    private static readonly DateTime Today = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private static FinancialItem Item(string id, FinKind kind, string category = Categories.Other, decimal? amount = 50m, DateTime? due = null) =>
        new() { Id = id, UserId = "u1", Kind = kind, Category = category, Title = $"Record {id}", Amount = amount, DueDate = due, Confidence = 0.9 };

    [Theory]
    [InlineData(FinKind.Payslip, "income")]
    [InlineData(FinKind.Salary, "income")]
    [InlineData(FinKind.Invoice, "bills")]
    [InlineData(FinKind.PaymentReminder, "bills")]
    [InlineData(FinKind.TaxNotice, "bills")]
    [InlineData(FinKind.LoanInstalment, "bills")]
    [InlineData(FinKind.Receipt, "receipts")]
    [InlineData(FinKind.Refund, "refunds")]
    [InlineData(FinKind.PaymentConfirmation, null)]
    [InlineData(FinKind.OtherFinancial, null)]
    [InlineData(FinKind.NotFinancial, null)]
    public void Each_kind_lands_in_one_tab_or_stays_in_the_inbox(FinKind kind, string? tab) =>
        Assert.Equal(tab, ItemRules.MoneyTab(Item("x", kind)));

    [Fact]
    public void Tabs_never_share_a_record_so_totals_cannot_double_count()
    {
        var items = Enum.GetValues<FinKind>().Select((k, n) => Item($"i{n}", k, due: Today.AddDays(5))).ToList();
        var tabs = ItemRules.MoneyTabs.Select(t => t.Key).Where(k => k != "overview").ToList();

        foreach (var item in items)
        {
            Assert.True(tabs.Count(t => ItemRules.MoneyTab(item) == t) <= 1);
        }

        Assert.Equal(items.Count(i => ItemRules.MoneyTab(i) != null), tabs.Sum(t => items.Count(i => ItemRules.MoneyTab(i) == t)));
    }

    [Fact]
    public void A_payslip_is_income_and_its_original_stays_in_the_inbox()
    {
        var payslip = Item("p", FinKind.Payslip, Categories.Income, 2412.40m) with { IncomeEvidence = IncomeEvidence.Payslip, EventDate = Today.AddDays(5) };
        var status = ItemRules.StatusOf(payslip, Today);

        Assert.Equal("income", ItemRules.MoneyTab(payslip));
        Assert.True(ItemRules.InView(payslip, status, "all"));
        Assert.NotEqual(DisplayStatus.IncomeReceived, status.Status);
        Assert.Equal(DisplayStatus.IncomeReceived, ItemRules.StatusOf(payslip with { UserState = UserState.ReportedPaid }, Today).Status);
    }

    [Fact]
    public void A_reminder_without_a_due_date_is_a_bill_but_not_assumed_unpaid()
    {
        var reminder = Item("r", FinKind.PaymentReminder, amount: null);
        var status = ItemRules.StatusOf(reminder, Today);

        Assert.Equal("bills", ItemRules.MoneyTab(reminder));
        Assert.False(reminder.IsObligation);
        Assert.DoesNotContain(status.Status, new[] { DisplayStatus.Overdue, DisplayStatus.DueSoon, DisplayStatus.Upcoming });
    }

    [Fact]
    public void Receipts_and_refunds_are_separate()
    {
        Assert.Equal("receipts", ItemRules.MoneyTab(Item("a", FinKind.Receipt)));
        Assert.Equal("refunds", ItemRules.MoneyTab(Item("b", FinKind.Refund)));
    }

    [Fact]
    public void Loan_records_are_bills_and_also_debt()
    {
        var loan = Item("l", FinKind.LoanInstalment, due: Today.AddDays(10));

        Assert.Equal("bills", ItemRules.MoneyTab(loan));
        Assert.True(ItemRules.IsDebt(loan));
    }

    [Fact]
    public void The_inbox_lists_what_is_not_in_my_money_and_never_hides_records()
    {
        var confirmation = Item("c", FinKind.PaymentConfirmation);
        var invoice = Item("i", FinKind.Invoice, due: Today.AddDays(3));

        Assert.True(ItemRules.InView(confirmation, ItemRules.StatusOf(confirmation, Today), "unsorted"));
        Assert.False(ItemRules.InView(invoice, ItemRules.StatusOf(invoice, Today), "unsorted"));
        Assert.Equal("all", ItemRules.InboxViews[^1].Key);
        Assert.Equal(["overview", "income", "bills", "receipts", "refunds"], ItemRules.MoneyTabs.Select(t => t.Key));
    }
}
