namespace Omabilo.Api.Finance;

// All money maths for the finance screens, in one place, so the dashboard, payday planner,
// debts and reports can never disagree. Pure functions: data in, numbers out.

public record PlanLine(string Label, decimal Amount, string Group, Certainty Certainty, DateTime? Date, string Source, string? Note = null);

public record PlanExclusion(string Label, decimal Amount, DateTime? Date, string Reason);

public record PaydayPlan(
    DateTime Today,
    DateTime PlanUntil,
    DateTime NextPayday,
    int Days,
    decimal StartingBalance,
    decimal ConfirmedIncome,
    decimal Bills,
    decimal LoanPayments,
    decimal Savings,
    decimal Discretionary,
    decimal ProjectedBalance,
    decimal SafeToSpendPerDay,
    IReadOnlyList<PlanLine> Lines,
    IReadOnlyList<PlanExclusion> Excluded,
    IReadOnlyList<string> Warnings);

public record UpcomingItem(string Label, decimal Amount, DateTime Date, string Type, Certainty Certainty, string? InvoiceId, bool Overdue);

public record MonthTotals(string Month, decimal Income, decimal Spending, decimal Saved);

public record CategoryTotal(string Category, decimal Amount);

public record MonthReport(
    string Month,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    bool IsPartialMonth,
    decimal Income,
    decimal Spending,
    decimal Saved,
    decimal KeptPercent,
    decimal PreviousSpending,
    IReadOnlyList<CategoryTotal> ByCategory,
    IReadOnlyList<CategoryTotal> TopPayees,
    int TransactionCount);

public static class FinanceCalculator
{
    public static decimal Balance(FinanceData d, DateTime today) =>
        d.OpeningBalance + d.Transactions.Where(t => t.Date.Date <= today.Date).Sum(t => t.Amount);

    /// <summary>The next payday after today (today counts only if the salary hasn't arrived yet).</summary>
    public static DateTime NextPayday(FinanceData d, DateTime today)
    {
        var candidate = Fin.NextPayday(today.Date, d.Settings.PayDay);

        if (candidate == today.Date && d.Transactions.Any(t => t.RecurringId == "rec-salary" && t.Date.Date == today.Date))
        {
            candidate = Fin.NextPayday(today.Date.AddDays(1), d.Settings.PayDay);
        }

        return candidate;
    }

    private static bool PaidInMonth(FinanceData d, RecurringItem r, DateTime month) =>
        d.Transactions.Any(t => t.RecurringId == r.Id && t.Date.Year == month.Year && t.Date.Month == month.Month);

    /// <summary>A recurring cost is already covered when an unpaid confirmed invoice from the same
    /// provider is due within a week of it, so the invoice is counted and the estimate is not.</summary>
    private static CanonicalInvoice? CoveringInvoice(RecurringItem r, DateTime occurrence, IEnumerable<CanonicalInvoice> invoices)
    {
        var name = InvoiceKeys.NormalizeProvider(r.Name);
        return invoices.FirstOrDefault(i =>
        {
            var provider = InvoiceKeys.NormalizeProvider(i.Provider);
            return provider.Length >= 4 && name.Contains(provider, StringComparison.Ordinal) && Math.Abs((i.DueDate.Date - occurrence).TotalDays) <= 7;
        });
    }

    public static PaydayPlan PlanUntilPayday(FinanceData d, IReadOnlyList<CanonicalInvoice> invoices, DateTime today, DateTime? until = null)
    {
        today = today.Date;
        var payday = NextPayday(d, today);
        var lastDay = payday.AddDays(-1);

        if (until is { } u)
        {
            if (u.Date < today || u.Date > lastDay)
            {
                throw new ArgumentOutOfRangeException(nameof(until), $"Pick a date from {today:yyyy-MM-dd} to {lastDay:yyyy-MM-dd} (the day before payday).");
            }

            lastDay = u.Date;
        }

        if (lastDay < today)
        {
            lastDay = today;
        }

        var days = (lastDay - today).Days + 1;
        var lines = new List<PlanLine>();
        var excluded = new List<PlanExclusion>();
        var warnings = new List<string>();
        var open = invoices.Where(i => i.CountsInTotals && i.PaymentStatus == InvoicePaymentStatus.Unpaid).ToList();

        // Income: only confirmed one-off payments count; expected-but-unconfirmed money is shown, not used
        foreach (var inc in d.ExpectedIncome.Where(i => i.Date.Date >= today && i.Date.Date <= lastDay))
        {
            if (inc.Confirmed)
            {
                lines.Add(new PlanLine(inc.Description, inc.Amount, "income", Certainty.Confirmed, inc.Date, "expected income"));
            }
            else
            {
                excluded.Add(new PlanExclusion(inc.Description, inc.Amount, inc.Date, "Not confirmed yet, so it isn't counted"));
            }
        }

        // Confirmed invoices due by the end of the period (overdue ones included)
        foreach (var inv in open.Where(i => i.DueDate.Date <= lastDay))
        {
            var overdue = inv.DueDate.Date < today;
            lines.Add(new PlanLine($"{inv.Provider} invoice", -inv.Amount, "bills", Certainty.Confirmed, inv.DueDate, "invoice",
                overdue ? "Overdue" : null));

            if (overdue)
            {
                warnings.Add($"{inv.Provider} invoice was due {inv.DueDate:d.M.} and is still unpaid.");
            }
        }

        // Recurring items falling in the period
        for (var month = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc); month <= lastDay; month = month.AddMonths(1))
        {
            foreach (var r in d.Recurring.Where(r => r.Kind != RecurringKind.Income))
            {
                var date = FinnishDemoSeed.OnDay(month, r.DayOfMonth);

                if (date < today || date > lastDay)
                {
                    continue;
                }

                if (PaidInMonth(d, r, month))
                {
                    excluded.Add(new PlanExclusion(r.Name, -r.Amount, date, "Already paid this month"));
                    continue;
                }

                if (r.Kind is RecurringKind.Essential or RecurringKind.Subscription && CoveringInvoice(r, date, open) is { } inv)
                {
                    excluded.Add(new PlanExclusion(r.Name, -r.Amount, date, $"Covered by the {inv.Provider} invoice, which is counted instead"));
                    continue;
                }

                var (group, certainty) = r.Kind switch
                {
                    RecurringKind.LoanPayment => ("loans", Certainty.Scheduled),
                    RecurringKind.Savings => ("savings", Certainty.Planned),
                    _ => ("bills", r.AmountVaries ? Certainty.Estimate : Certainty.Scheduled),
                };
                lines.Add(new PlanLine(r.Name, -r.Amount, group, certainty, date, "recurring", r.AmountVaries ? "Typical amount; the real bill may differ" : null));
            }
        }

        var discretionary = Math.Round(d.Settings.DiscretionaryPerDay * days, 2);

        if (discretionary > 0)
        {
            // Finnish number style (18,00 €) to match the website
            var perDay = d.Settings.DiscretionaryPerDay.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
            lines.Add(new PlanLine($"Everyday spending ({perDay} € a day × {days} days)", -discretionary,
                "discretionary", Certainty.Planned, null, "your setting"));
        }

        var balance = Balance(d, today);
        decimal Sum(string group) => -lines.Where(l => l.Group == group).Sum(l => l.Amount);
        var income = lines.Where(l => l.Group == "income").Sum(l => l.Amount);
        var committed = balance + income - Sum("bills") - Sum("loans") - Sum("savings");
        var projected = committed - discretionary;

        if (projected < 0)
        {
            warnings.Add("At this pace the account would go below zero before payday.");
        }

        return new PaydayPlan(today, lastDay, payday, days, balance, income, Sum("bills"), Sum("loans"), Sum("savings"), discretionary,
            projected, Math.Round(Math.Max(0, committed) / days, 2),
            lines.OrderBy(l => l.Date ?? DateTime.MaxValue).ToList(), excluded.OrderBy(e => e.Date).ToList(), warnings);
    }

    /// <summary>Bills and payments in the next N days: confirmed invoices plus recurring items not yet paid or invoiced.</summary>
    public static IReadOnlyList<UpcomingItem> Upcoming(FinanceData d, IReadOnlyList<CanonicalInvoice> invoices, DateTime today, int days)
    {
        today = today.Date;
        var until = today.AddDays(days);
        var open = invoices.Where(i => i.CountsInTotals && i.PaymentStatus == InvoicePaymentStatus.Unpaid).ToList();
        var items = open.Where(i => i.DueDate.Date <= until)
            .Select(i => new UpcomingItem($"{i.Provider} invoice", i.Amount, i.DueDate, "invoice", Certainty.Confirmed, i.Id, i.DueDate.Date < today))
            .ToList();

        for (var month = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc); month <= until; month = month.AddMonths(1))
        {
            foreach (var r in d.Recurring.Where(r => r.Kind is RecurringKind.Essential or RecurringKind.Subscription or RecurringKind.LoanPayment))
            {
                var date = FinnishDemoSeed.OnDay(month, r.DayOfMonth);

                if (date >= today && date <= until && !PaidInMonth(d, r, month) && CoveringInvoice(r, date, open) == null)
                {
                    items.Add(new UpcomingItem(r.Name, r.Amount, date, r.Kind == RecurringKind.LoanPayment ? "loan" : "recurring",
                        r.AmountVaries ? Certainty.Estimate : Certainty.Scheduled, null, false));
                }
            }
        }

        return items.OrderBy(i => i.Date).ToList();
    }

    public static MonthTotals Totals(FinanceData d, DateTime monthStart, DateTime? upTo = null)
    {
        var end = upTo?.Date.AddDays(1) ?? monthStart.AddMonths(1);
        var txns = d.Transactions.Where(t => t.Date >= monthStart && t.Date < end).ToList();
        return new MonthTotals(
            monthStart.ToString("yyyy-MM", Fin.Inv),
            txns.Where(t => t.Amount > 0 && t.Category != "Savings").Sum(t => t.Amount),
            -txns.Where(t => t.Amount < 0 && t.Category != "Savings").Sum(t => t.Amount),
            -txns.Where(t => t.Category == "Savings").Sum(t => t.Amount));
    }

    public static MonthReport Report(FinanceData d, DateTime monthStart, DateTime today)
    {
        var isCurrent = monthStart.Year == today.Year && monthStart.Month == today.Month;
        var end = isCurrent ? today.Date : monthStart.AddMonths(1).AddDays(-1);
        var txns = d.Transactions.Where(t => t.Date >= monthStart && t.Date <= end).ToList();
        var totals = Totals(d, monthStart, end);
        // A month in progress is compared with the same days of the previous month
        var prev = Totals(d, monthStart.AddMonths(-1), isCurrent ? monthStart.AddMonths(-1).AddDays(today.Day - 1) : null);
        var spend = txns.Where(t => t.Amount < 0 && t.Category != "Savings").ToList();

        return new MonthReport(
            totals.Month, monthStart, end, isCurrent,
            totals.Income, totals.Spending, totals.Saved,
            totals.Income > 0 ? Math.Round((totals.Income - totals.Spending) / totals.Income * 100, 1) : 0,
            prev.Spending,
            spend.GroupBy(t => t.Category).Select(g => new CategoryTotal(g.Key, -g.Sum(t => t.Amount))).OrderByDescending(c => c.Amount).ToList(),
            spend.GroupBy(t => t.Description).Select(g => new CategoryTotal(g.Key, -g.Sum(t => t.Amount))).OrderByDescending(c => c.Amount).Take(5).ToList(),
            txns.Count);
    }

    public static DebtAccount ToDebtAccount(Loan l) => new(l.Id, l.Name, l.Lender, l.Kind switch
    {
        "Credit card" => DebtKind.CreditCard,
        "Car loan" => DebtKind.CarLoan,
        "Student loan" => DebtKind.StudentLoan,
        "Buy now, pay later" => DebtKind.PayLater,
        _ => DebtKind.Personal,
    }, l.Balance, l.StartingBalance, l.Apr, l.MinPayment, l.DueDay);
}
