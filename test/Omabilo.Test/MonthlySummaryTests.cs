using Xunit;

public class MonthlySummaryTests
{
    private static readonly DateTime Today = new(2026, 10, 17);

    private static FinancialItem Item(string id, FinKind kind, decimal? amount, DateTime date, UserState state = UserState.None,
        string? duplicateOf = null, decimal? net = null, IncomeEvidence evidence = IncomeEvidence.None) => new()
    {
        Id = id,
        UserId = "u1",
        Kind = kind,
        Amount = amount,
        Net = net,
        DueDate = kind is FinKind.Invoice or FinKind.PaymentReminder ? date : null,
        EventDate = kind is FinKind.Invoice or FinKind.PaymentReminder ? null : date,
        UserState = state,
        PossibleDuplicateOf = duplicateOf,
        IncomeEvidence = evidence,
        Counterparty = kind == FinKind.Payslip ? "Kuura Software Oy" : "Revontuli Energy Oy",
    };

    [Fact]
    public void Totals_each_month_without_duplicates_or_not_applicable_records()
    {
        var items = new[]
        {
            Item("b1", FinKind.Invoice, 64.20m, new(2026, 10, 29)),
            Item("b1dup", FinKind.Invoice, 64.20m, new(2026, 10, 29), duplicateOf: "b1"),
            Item("b2", FinKind.Invoice, 30m, new(2026, 10, 5), UserState.ReportedPaid),
            Item("na", FinKind.Invoice, 999m, new(2026, 10, 6), UserState.NotApplicable),
            Item("r1", FinKind.Receipt, 12.50m, new(2026, 10, 2)),
            Item("rf", FinKind.Refund, 5m, new(2026, 10, 3)),
            Item("sep", FinKind.Invoice, 40m, new(2026, 9, 20)),
        };

        var months = MonthlySummary.Build(items, Today);

        Assert.Equal(6, months.Count);
        var oct = months[0];
        Assert.Equal((2026, 10), (oct.Year, oct.Month));
        Assert.Equal(94.20m, oct.BillsDue);
        Assert.Equal(2, oct.Bills);
        Assert.Equal(30m, oct.BillsReportedPaid);
        Assert.Equal(12.50m, oct.Receipts);
        Assert.Equal(5m, oct.Refunds);
        Assert.Equal(40m, months[1].BillsDue);
        Assert.Equal((2026, 5), (months[5].Year, months[5].Month));
    }

    [Fact]
    public void Payslip_net_is_not_counted_as_received_until_reported()
    {
        var items = new[]
        {
            Item("p1", FinKind.Payslip, null, new(2026, 9, 30), net: 2412.40m, evidence: IncomeEvidence.Payslip),
            Item("p0", FinKind.Payslip, null, new(2026, 8, 29), net: 2400m, evidence: IncomeEvidence.ReportedReceived),
        };

        var months = MonthlySummary.Build(items, Today);

        Assert.Equal(2412.40m, months[1].PayslipNet);
        Assert.Equal(0m, months[1].IncomeReportedReceived);
        Assert.Equal(2400m, months[2].IncomeReportedReceived);

        var history = MonthlySummary.SalaryHistory(items);
        Assert.Equal(["p1", "p0"], history.Select(h => h.ItemId));
        Assert.Equal(IncomeEvidence.Payslip, history[0].Evidence);
    }

    [Fact]
    public void Counts_records_without_an_amount_instead_of_guessing()
    {
        var items = new[] { Item("x", FinKind.Invoice, null, new(2026, 10, 25)) };

        var oct = MonthlySummary.Build(items, Today)[0];

        Assert.Equal(0m, oct.BillsDue);
        Assert.Equal(1, oct.ItemsWithoutAmount);
    }
}
