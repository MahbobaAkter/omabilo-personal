// The "money until payday" calculation every screen shares (Dashboard, Payday Planner, Smart
// Spend Guard), so they always show the same numbers. Each line says how sure it is, and
// anything left out is listed with the reason, so nothing is counted twice or hidden.
public partial class OmabiloApp
{
    private enum Sureness { Confirmed, Scheduled, Estimate }

    private record PlanLine(string Label, decimal Amount, Sureness Sureness, DateTime? Date, string Note);

    private record PaydayPlan(
        decimal Balance,
        DateTime Payday,
        int Days,
        List<PlanLine> Bills,
        List<PlanLine> DebtPayments,
        List<PlanLine> Savings,
        List<(string Label, string Reason)> NotCounted,
        decimal SafeToSpend,
        decimal EverydayPerDay,
        decimal EverydayEstimate,
        decimal LeftAfterEveryday);

    private static readonly string[] EverydayCategories = ["Groceries", "Dining out", "Shopping"];

    private PaydayPlan ComputePaydayPlan()
    {
        var payday = NextPayday();
        var lastDay = payday.AddDays(-1);
        var days = DaysToPayday();
        var bills = BillsBeforePayday();
        var openBills = _inbox.Where(IsOpenBill).ToList();
        var notCounted = new List<(string, string)>();

        // 1. Bills you've received that are due before payday (overdue ones included)
        var billLines = bills.Select(b => new PlanLine(b.From, b.Amount ?? 0, Sureness.Confirmed, b.DueDate,
            b.DueDate < Today ? "Overdue" : "Bill received")).ToList();

        // 2. Loan minimums due before payday, unless already paid or already in your bills
        var debtLines = new List<PlanLine>();

        foreach (var d in _debts.Where(d => d.Balance > 0))
        {
            var due = OnDueDay(Today, d.DueDay);

            if (due < Today)
            {
                due = OnDueDay(Today.AddMonths(1), d.DueDay);
            }

            if (due > lastDay)
            {
                continue;
            }

            if (openBills.FirstOrDefault(b => b.From == d.Name) is { } bill)
            {
                notCounted.Add(($"{d.Name} minimum", $"Already counted as the {bill.From} bill"));
            }
            else if (PaidThisMonth(d, due))
            {
                notCounted.Add(($"{d.Name} minimum", "Already paid this month"));
            }
            else
            {
                debtLines.Add(new PlanLine($"{d.Name} minimum", Math.Min(d.MinPayment, d.Balance), Sureness.Scheduled, due, "Scheduled loan payment"));
            }
        }

        // 3. Savings: transfers come out of the next pay (Plan your next pay), so none before payday
        var savingsLines = new List<PlanLine>();
        notCounted.Add(("Savings transfers", "Planned from your next pay, after payday"));

        // 4. Everyday spending, estimated from last month (an estimate, so shown apart)
        var lastMonth = MonthStart(Today).AddMonths(-1);
        var everydayLastMonth = -TxnsIn(lastMonth).Where(t => t.Amount < 0 && EverydayCategories.Contains(t.Category)).Sum(t => t.Amount);
        var perDay = Math.Round(everydayLastMonth / DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month), 2);

        var balance = CurrentBalance();
        var safe = balance - billLines.Sum(l => l.Amount) - debtLines.Sum(l => l.Amount) - savingsLines.Sum(l => l.Amount);
        var estimate = perDay * days;

        return new PaydayPlan(balance, payday, days, billLines, debtLines, savingsLines, notCounted, safe, perDay, estimate, safe - estimate);
    }

    private static DateTime OnDueDay(DateTime month, int day) =>
        new(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)), 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A loan payment recorded in the due month: logged here (linked), or a past
    /// payment whose description names the loan or its lender.</summary>
    private bool PaidThisMonth(DebtAccount d, DateTime due)
    {
        var lenderWord = d.Lender.Split(' ')[0];
        return _txns.Any(t => t.Date.Year == due.Year && t.Date.Month == due.Month && t.Category == "Debt payments"
            && (t.LinkId == d.Id || t.Merchant.Contains(d.Name, StringComparison.OrdinalIgnoreCase)
                || (lenderWord.Length > 3 && t.Merchant.Contains(lenderWord, StringComparison.OrdinalIgnoreCase))));
    }

    // ── One clear next step, most urgent first ──────────────────────────────

    private record NextStep(string Title, string Detail, string ButtonText, string Section, string Tone);

    private NextStep ComputeNextStep(PaydayPlan plan)
    {
        var overdue = _inbox.Where(i => IsOpenBill(i) && i.DueDate < Today).OrderBy(i => i.DueDate).FirstOrDefault();

        if (overdue != null)
        {
            return new($"Pay the overdue {overdue.From} bill", $"{Fin.Eur(overdue.Amount ?? 0, 2)} was due {Fin.Day(overdue.DueDate!.Value)}. Paying it first avoids late fees.", "Open inbox", "inbox", "error");
        }

        if (plan.SafeToSpend < 0)
        {
            return new("Bills before payday are more than your balance", $"You're {Fin.Eur(-plan.SafeToSpend)} short until {Fin.Day(plan.Payday)}. See which bills could move.", "Open Payday Planner", "payday", "error");
        }

        var toReview = InvoicesToReview();

        if (toReview > 0)
        {
            return new($"Check {toReview} invoice{(toReview == 1 ? "" : "s")} waiting for review", "Confirm what was read so it counts in your bills, or reject it.", "Review invoices", "invoices", "warning");
        }

        var soon = _inbox.Where(i => IsOpenBill(i) && i.DueDate is { } d && d <= Today.AddDays(3)).OrderBy(i => i.DueDate).FirstOrDefault();

        if (soon != null)
        {
            return new($"Pay {soon.From} soon", $"{Fin.Eur(soon.Amount ?? 0, 2)}, {DueText(soon.DueDate!.Value)}.", "Open inbox", "inbox", "warning");
        }

        var buffer = _goals.FirstOrDefault(g => g.IsEmergencyFund);

        if (buffer != null && buffer.Saved < buffer.Target && plan.LeftAfterEveryday > 100)
        {
            var suggest = Math.Min(Math.Floor(plan.LeftAfterEveryday / 4 / 5) * 5, buffer.Target - buffer.Saved);

            if (suggest >= 10)
            {
                return new($"Move {Fin.Eur(suggest)} to your emergency fund", $"You're on track until payday with about {Fin.Eur(plan.LeftAfterEveryday)} to spare at your usual pace.", "Open Savings & Goals", "goals", "success");
            }
        }

        return new("You're on track", "Nothing needs you right now. Your monthly report shows where your money went.", "See Monthly Reports", "reports", "success");
    }

    // ── Smart Spend Guard: what a planned purchase does to the plan ─────────

    private record SpendCheck(decimal Amount, decimal SafeAfter, decimal PerDayBefore, decimal PerDayAfter, decimal LeftAfterEveryday, decimal FromSavings, SavingsGoal? AffectedGoal, string Verdict, string Tone);

    private SpendCheck CheckPurchase(PaydayPlan plan, decimal amount)
    {
        var safeAfter = plan.SafeToSpend - amount;
        var leftAfter = plan.LeftAfterEveryday - amount;
        var fromSavings = Math.Max(0, -safeAfter);
        var goal = _goals.Where(g => g.Saved > 0).OrderByDescending(g => g.IsEmergencyFund).ThenByDescending(g => g.Saved).FirstOrDefault();

        var (verdict, tone) = safeAfter < 0
            ? ($"This is {Fin.Eur(-safeAfter)} more than you have until payday. It would need money from savings.", "error")
            : leftAfter < 0
                ? ("You can pay for it, but at your usual pace you'd run short before payday. Spending a little less each day would cover it.", "warning")
                : ("This fits. You'd still have room for your usual everyday spending.", "success");

        return new SpendCheck(amount, safeAfter, plan.SafeToSpend / plan.Days, Math.Max(0, safeAfter) / plan.Days, leftAfter, fromSavings, fromSavings > 0 ? goal : null, verdict, tone);
    }
}
