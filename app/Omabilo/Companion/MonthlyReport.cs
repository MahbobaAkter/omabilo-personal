// Monthly income and expense reports from the records a person keeps in Omabilo. What happened
// (money received, bills paid, purchases) is kept apart from what is still expected (bills not
// yet paid, salary not yet arrived), and every total lists the records behind it. A bill only
// counts as spent once the person marks it paid or a payment message confirms it; a payslip is
// a calculation, so its net pay is shown but not counted as money received. Plain C#, no IKON types.

/// <summary>One record behind a total. Amount is negative for a refund.</summary>
public sealed record ReportLine(string ItemId, string Title, string Category, DateTime Date, decimal Amount, string Basis);

public sealed record CategoryTotal(string Category, decimal Amount, int Count);

public sealed record MonthReport(
    int Year,
    int Month,
    IReadOnlyList<ReportLine> Income,
    IReadOnlyList<ReportLine> Expenses,
    IReadOnlyList<ReportLine> PayslipsNotConfirmed,
    IReadOnlyList<ReportLine> ExpectedIncome,
    IReadOnlyList<ReportLine> BillsStillOpen,
    IReadOnlyList<string> LeftOut)
{
    public decimal IncomeTotal => Income.Sum(l => l.Amount);
    public decimal ExpenseTotal => Expenses.Sum(l => l.Amount);
    public decimal Net => IncomeTotal - ExpenseTotal;
    public decimal ExpectedIncomeTotal => ExpectedIncome.Sum(l => l.Amount);
    public decimal BillsStillOpenTotal => BillsStillOpen.Sum(l => l.Amount);
    public bool HasActuals => Income.Count > 0 || Expenses.Count > 0;

    public IReadOnlyList<CategoryTotal> ExpenseCategories => Expenses.GroupBy(l => l.Category)
        .Select(g => new CategoryTotal(g.Key, g.Sum(l => l.Amount), g.Count()))
        .Where(c => c.Amount != 0).OrderByDescending(c => c.Amount).ToList();
}

public static class MonthlyReports
{
    public const string HowCounted =
        "Income is money a message or you confirmed as received. Expenses are bills you marked paid (or a payment message confirmed) in the month they were due, " +
        "plus receipts, minus refunds. Bills not yet paid and salary not yet arrived are shown separately and never counted. Possible duplicates and records marked \"not for me\" are left out.";

    /// <summary>The last <paramref name="months"/> calendar months up to today's, newest first.</summary>
    public static IReadOnlyList<MonthReport> Build(IReadOnlyList<FinancialItem> items, DateTime today, int months = 6, IncomeOutlook? outlook = null)
    {
        today = today.Date;
        var first = new DateTime(today.Year, today.Month, 1);
        return Enumerable.Range(0, months).Select(i => first.AddMonths(-i)).Select(m => For(items, m.Year, m.Month, today, outlook)).ToList();
    }

    public static MonthReport For(IReadOnlyList<FinancialItem> items, int year, int month, DateTime today, IncomeOutlook? outlook = null)
    {
        today = today.Date;
        var leftOut = new List<string>();
        var counted = items.Where(Counts).ToList();
        bool InMonth(DateTime? d) => d is { } x && x.Year == year && x.Month == month;

        var foreign = counted.Where(x => x.Currency != "EUR" && InMonth(MonthlySummary.DateOf(x))).ToList();
        if (foreign.Count > 0)
        {
            leftOut.Add($"{foreign.Count} record{(foreign.Count == 1 ? "" : "s")} in another currency than euros.");
        }

        counted = counted.Where(x => x.Currency == "EUR").ToList();

        // Income: money confirmed received, once per pay even when a payslip and a "salary paid" email both exist
        var income = new List<ReportLine>();
        var payslipsOnly = new List<ReportLine>();
        var expected = new List<ReportLine>();

        foreach (var x in counted.Where(x => x.IsIncome && InMonth(x.EventDate) && (x.Net ?? x.Amount) is > 0)
                     .OrderByDescending(x => Received(x)).ThenBy(x => x.Kind == FinKind.Payslip).ThenBy(x => x.EventDate))
        {
            var amount = (x.Net ?? x.Amount)!.Value;
            var date = x.EventDate!.Value.Date;
            var line = new ReportLine(x.Id, x.Title, Categories.Income, date, amount, "");

            if (income.Concat(payslipsOnly).Concat(expected).Any(l => l.Amount == amount && Math.Abs((l.Date - date).TotalDays) <= 3))
            {
                continue;
            }

            // Received wins over the date, as in the payday plan: pay can arrive before its stated day
            if (Received(x))
            {
                income.Add(line with { Basis = x.IncomeEvidence == IncomeEvidence.ReportedReceived ? "The message says it was paid." : "You marked it received." });
            }
            else if (date > today)
            {
                expected.Add(line with { Basis = "Expected on this date according to the message; not arrived yet." });
            }
            else
            {
                payslipsOnly.Add(line with { Basis = "Net pay stated on a payslip. Mark it received once it's on your account." });
            }
        }

        // The next salary, when the payday falls later this month and no payslip or salary message
        // covers this month's pay yet: a payslip dated a few days from the entered payday is the same pay
        var monthPay = counted.Any(x => x.IsIncome && InMonth(x.EventDate) && (x.Net ?? x.Amount) is > 0);
        if (outlook is { NextPayday: { } pay, ExpectedNet: { } net } && InMonth(pay) && pay.Date > today && !monthPay
            && !expected.Any(l => Math.Abs((l.Date - pay.Date).TotalDays) <= 3))
        {
            expected.Add(new ReportLine("", "Next salary (estimate)", Categories.Income, pay.Date, net, outlook.PaydayBasis));
        }

        // Expenses: bills paid, receipts and refunds
        var expenses = new List<ReportLine>();
        var open = new List<ReportLine>();

        foreach (var x in counted.Where(x => x.IsBill && InMonth(MonthlySummary.DateOf(x))).OrderBy(x => MonthlySummary.DateOf(x)))
        {
            var date = MonthlySummary.DateOf(x)!.Value.Date;

            if (x.Amount is not > 0)
            {
                leftOut.Add($"{x.Title}: the amount isn't known.");
                continue;
            }

            if (x.UserState is UserState.ReportedPaid or UserState.ConfirmedPaid)
            {
                expenses.Add(new ReportLine(x.Id, x.Title, x.Category, date, x.Amount.Value,
                    x.UserState == UserState.ConfirmedPaid ? "Paid: confirmed by a payment message." : "Paid: you marked it paid."));
            }
            else if (x.UserState == UserState.Disputed)
            {
                leftOut.Add($"{x.Title}: you disputed it.");
            }
            else
            {
                open.Add(new ReportLine(x.Id, x.Title, x.Category, date, x.Amount.Value, date < today ? "Due date passed; no payment recorded." : "Not paid yet."));
            }
        }

        foreach (var x in counted.Where(x => x.Kind is FinKind.Receipt or FinKind.Refund && InMonth(x.EventDate ?? MonthlySummary.DateOf(x))))
        {
            var date = (x.EventDate ?? MonthlySummary.DateOf(x))!.Value.Date;

            if (x.Amount is not > 0)
            {
                leftOut.Add($"{x.Title}: the amount isn't known.");
                continue;
            }

            // A receipt for a bill already counted as paid is the same money
            if (x.Kind == FinKind.Receipt && expenses.Any(e => e.Amount == x.Amount && Math.Abs((e.Date - date).TotalDays) <= 7
                    && items.FirstOrDefault(i => i.Id == e.ItemId) is { } bill
                    && InvoiceKeys.NormalizeProvider(bill.Counterparty) == InvoiceKeys.NormalizeProvider(x.Counterparty)))
            {
                leftOut.Add($"{x.Title}: the same payment as a bill already counted.");
                continue;
            }

            expenses.Add(x.Kind == FinKind.Refund
                ? new ReportLine(x.Id, x.Title, x.Category, date, -x.Amount.Value, "Refund: money back.")
                : new ReportLine(x.Id, x.Title, x.Category, date, x.Amount.Value, "Receipt for a purchase."));
        }

        return new MonthReport(year, month, income, expenses.OrderBy(l => l.Date).ToList(), payslipsOnly, expected.OrderBy(l => l.Date).ToList(), open, leftOut);
    }

    private static bool Received(FinancialItem x) => ItemRules.IncomeReceived(x);

    private static bool Counts(FinancialItem x) =>
        x.PossibleDuplicateOf == null && x.UserState != UserState.NotApplicable && x.Kind != FinKind.NotFinancial;
}
