// Month-by-month totals from the records Omabilo holds. Deterministic and explainable: each figure
// says what it is built from, duplicates and "not for me" records never count, and a payslip's net
// pay is shown as what the payslip states, never as money received.

public sealed record MonthTotals(
    int Year,
    int Month,
    decimal PayslipNet,
    int Payslips,
    decimal IncomeReportedReceived,
    decimal BillsDue,
    int Bills,
    decimal BillsReportedPaid,
    decimal Receipts,
    int ReceiptCount,
    decimal Refunds,
    int ItemsWithoutAmount);

public sealed record SalaryEntry(string ItemId, DateTime? PayDate, string? Period, string? Employer, decimal? Gross, decimal? Net, IncomeEvidence Evidence);

public static class MonthlySummary
{
    public const string Explanation =
        "Bills are counted in the month they are due; payslips, receipts and refunds in the month of their date. " +
        "Payslip net pay is what the payslip states, not proof the money arrived. Possible duplicates and records marked \"not for me\" are left out.";

    /// <summary>The last <paramref name="months"/> calendar months up to and including today's, newest first.</summary>
    public static IReadOnlyList<MonthTotals> Build(IEnumerable<FinancialItem> items, DateTime today, int months = 6)
    {
        var counted = items.Where(Counts).ToList();
        var result = new List<MonthTotals>();
        var first = new DateTime(today.Year, today.Month, 1);

        for (var i = 0; i < months; i++)
        {
            var start = first.AddMonths(-i);
            var inMonth = counted.Where(x => DateOf(x) is { } d && d.Year == start.Year && d.Month == start.Month).ToList();

            var payslips = inMonth.Where(x => x.Kind is FinKind.Payslip or FinKind.Salary).ToList();
            var bills = inMonth.Where(IsBill).ToList();
            var receipts = inMonth.Where(x => x.Kind == FinKind.Receipt).ToList();
            var refunds = inMonth.Where(x => x.Kind == FinKind.Refund).ToList();

            result.Add(new MonthTotals(
                start.Year,
                start.Month,
                PayslipNet: payslips.Sum(x => x.Net ?? x.Amount ?? 0m),
                Payslips: payslips.Count,
                IncomeReportedReceived: payslips.Where(x => x.IncomeEvidence == IncomeEvidence.ReportedReceived || x.UserState is UserState.ReportedPaid or UserState.ConfirmedPaid)
                    .Sum(x => x.Net ?? x.Amount ?? 0m),
                BillsDue: bills.Sum(x => x.Amount ?? 0m),
                Bills: bills.Count,
                BillsReportedPaid: bills.Where(x => x.UserState is UserState.ReportedPaid or UserState.ConfirmedPaid).Sum(x => x.Amount ?? 0m),
                Receipts: receipts.Sum(x => x.Amount ?? 0m),
                ReceiptCount: receipts.Count,
                Refunds: refunds.Sum(x => x.Amount ?? 0m),
                ItemsWithoutAmount: inMonth.Count(x => ((IsBill(x) || x.IsExpense) && x.Amount == null) || (x.IsIncome && x.Net == null && x.Amount == null))));
        }

        return result;
    }

    /// <summary>Every payslip or salary notice, newest first, with what kind of evidence each figure is.</summary>
    public static IReadOnlyList<SalaryEntry> SalaryHistory(IEnumerable<FinancialItem> items) =>
        items.Where(x => x.IsIncome && Counts(x))
            .OrderByDescending(x => DateOf(x) ?? DateTime.MinValue)
            .Select(x => new SalaryEntry(x.Id, DateOf(x), x.Period, x.Counterparty, x.Gross, x.Net ?? x.Amount, x.IncomeEvidence))
            .ToList();

    /// <summary>A bill-type record, with or without a readable amount (IsObligation needs both amount and due date).</summary>
    private static bool IsBill(FinancialItem x) => x.Kind is FinKind.Invoice or FinKind.PaymentReminder or FinKind.DebtCollection or FinKind.Subscription
        or FinKind.Insurance or FinKind.LoanInstalment or FinKind.TaxNotice;

    private static bool Counts(FinancialItem x) =>
        x.PossibleDuplicateOf == null && x.UserState != UserState.NotApplicable && x.Kind != FinKind.NotFinancial;

    /// <summary>The date a record belongs to: the due date for bills, otherwise its own date, else when the email arrived.</summary>
    public static DateTime? DateOf(FinancialItem x) =>
        (IsBill(x) ? x.DueDate ?? x.InvoiceDate ?? x.EventDate : x.EventDate ?? x.InvoiceDate ?? x.DueDate)
        ?? x.Sources.FirstOrDefault()?.ReceivedUtc.Date;
}
