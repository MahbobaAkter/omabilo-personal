using Omabilo.Api.Finance;
using Xunit;

/// <summary>Money maths on a fixed date (Sat 17 Oct 2026, payday the 25th) so every figure can be checked by hand.</summary>
public class FinanceCalculatorTests
{
    private static readonly DateTime Today = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static FinanceData Seed() => FinnishDemoSeed.Create("demo-alice", Today);

    private static CanonicalInvoice Invoice(string provider, decimal amount, int dueDay, bool verified = true, bool paid = false) =>
        new($"inv_{provider.GetHashCode():x}", "demo-alice", provider, "N-1", null, null, new DateTime(2026, 10, dueDay, 0, 0, 0, DateTimeKind.Utc), amount, "EUR",
            paid ? InvoicePaymentStatus.Paid : InvoicePaymentStatus.Unpaid,
            verified ? InvoiceVerification.Verified : InvoiceVerification.Unverified,
            verified ? InvoiceProcessingState.Ready : InvoiceProcessingState.NeedsReview,
            1, InvoiceSourceType.Manual, ["src"], Today, Today);

    [Fact]
    public void Demo_seed_has_four_months_of_coherent_history_and_the_stated_balance()
    {
        var d = Seed();
        Assert.True(d.Settings.IsDemoData);
        Assert.Equal(FinnishDemoSeed.BalanceToday, FinanceCalculator.Balance(d, Today));
        Assert.Equal(5, d.Transactions.Select(t => t.Date.Month).Distinct().Count());
        Assert.All(d.Transactions, t => Assert.True(t.Date <= Today));
        Assert.Equal(4, d.Transactions.Count(t => t.RecurringId == "rec-salary"));
        Assert.All(d.Recurring.Where(r => r.Kind != RecurringKind.Savings && r.LoanId == null), r => Assert.Contains("fictional", r.Name));
    }

    [Fact]
    public void Same_owner_gets_the_same_demo_data()
    {
        Assert.Equal(
            Seed().Transactions.Select(t => t.Amount),
            FinnishDemoSeed.Create("demo-alice", Today).Transactions.Select(t => t.Amount));
    }

    [Fact]
    public void Payday_plan_counts_each_item_once_with_its_certainty()
    {
        var plan = FinanceCalculator.PlanUntilPayday(Seed(), [], Today);

        Assert.Equal(new DateTime(2026, 10, 25), plan.NextPayday);
        Assert.Equal(new DateTime(2026, 10, 24), plan.PlanUntil);
        Assert.Equal(8, plan.Days);
        Assert.Equal(812.40m, plan.StartingBalance);
        Assert.Equal(184m, plan.ConfirmedIncome);       // confirmed tax refund on the 23rd
        Assert.Equal(62m, plan.Bills);                  // electricity on the 20th, an estimate
        Assert.Equal(45m, plan.LoanPayments);           // card minimum on the 22nd
        Assert.Equal(0m, plan.Savings);                 // the savings transfer is on the 26th, after payday
        Assert.Equal(144m, plan.Discretionary);         // 18 € a day for 8 days
        Assert.Equal(745.40m, plan.ProjectedBalance);
        Assert.Equal(Math.Round((812.40m + 184m - 62m - 45m) / 8, 2), plan.SafeToSpendPerDay);

        Assert.Contains(plan.Lines, l => l.Label.StartsWith("Electricity") && l.Certainty == Certainty.Estimate);
        Assert.Contains(plan.Lines, l => l.Group == "loans" && l.Certainty == Certainty.Scheduled);
        Assert.Contains(plan.Excluded, e => e.Label.StartsWith("Selling an old bike") && e.Reason.Contains("Not confirmed"));
    }

    [Fact]
    public void An_invoice_replaces_the_matching_recurring_estimate()
    {
        var plan = FinanceCalculator.PlanUntilPayday(Seed(), [Invoice("Pohjoisvirta Energia", 58.20m, 21)], Today);

        Assert.Equal(58.20m, plan.Bills);
        Assert.DoesNotContain(plan.Lines, l => l.Label.StartsWith("Electricity"));
        Assert.Contains(plan.Excluded, e => e.Label.StartsWith("Electricity") && e.Reason.Contains("Covered by the Pohjoisvirta Energia invoice"));
        Assert.Equal(812.40m + 184m - 58.20m - 45m - 144m, plan.ProjectedBalance);
    }

    [Fact]
    public void Unconfirmed_and_paid_invoices_are_not_counted_but_overdue_ones_are_flagged()
    {
        var plan = FinanceCalculator.PlanUntilPayday(Seed(),
        [
            Invoice("Review Me Oy", 99m, 20, verified: false),
            Invoice("Already Paid Oy", 50m, 20, paid: true),
            Invoice("Late Clinic Oy", 30m, 10),
        ], Today);

        Assert.Equal(62m + 30m, plan.Bills);
        Assert.Contains(plan.Warnings, w => w.Contains("Late Clinic Oy"));
    }

    [Fact]
    public void Expected_income_counts_only_after_confirmation()
    {
        var d = Seed();
        var confirmed = d with { ExpectedIncome = d.ExpectedIncome.Select(i => i with { Confirmed = true }).ToList() };

        Assert.Equal(184m, FinanceCalculator.PlanUntilPayday(d, [], Today).ConfirmedIncome);
        Assert.Equal(304m, FinanceCalculator.PlanUntilPayday(confirmed, [], Today).ConfirmedIncome);
    }

    [Fact]
    public void Plan_date_must_fall_before_payday()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FinanceCalculator.PlanUntilPayday(Seed(), [], Today, Today.AddDays(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FinanceCalculator.PlanUntilPayday(Seed(), [], Today, Today.AddDays(-1)));
        Assert.Equal(3, FinanceCalculator.PlanUntilPayday(Seed(), [], Today, Today.AddDays(2)).Days);
    }

    [Fact]
    public void Upcoming_lists_unpaid_items_without_duplicates()
    {
        var upcoming = FinanceCalculator.Upcoming(Seed(), [Invoice("Pohjoisvirta Energia", 58.20m, 21)], Today, 14);

        Assert.Contains(upcoming, u => u.Type == "invoice" && u.Amount == 58.20m);
        Assert.DoesNotContain(upcoming, u => u.Label.StartsWith("Electricity"));
        Assert.Contains(upcoming, u => u.Label.StartsWith("Aurora credit card"));
        Assert.DoesNotContain(upcoming, u => u.Label.StartsWith("Rent"));   // paid on the 1st, next one is beyond 14 days
        Assert.True(upcoming.SequenceEqual(upcoming.OrderBy(u => u.Date)));
    }

    [Fact]
    public void Reports_label_the_period_and_compare_like_with_like()
    {
        var d = Seed();
        var september = FinanceCalculator.Report(d, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), Today);
        var october = FinanceCalculator.Report(d, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Today);

        Assert.False(september.IsPartialMonth);
        Assert.Equal(new DateTime(2026, 9, 30), september.PeriodEnd);
        Assert.Equal(2780m, september.Income);
        Assert.Equal(150m, september.Saved);
        Assert.True(september.Spending > 1500m);

        Assert.True(october.IsPartialMonth);
        Assert.Equal(Today, october.PeriodEnd);
        Assert.Equal(0m, october.Income);   // salary arrives on the 25th
        Assert.True(october.PreviousSpending < september.Spending);   // compared with 1-17 Sep only
    }
}
