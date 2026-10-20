using Xunit;

// Expected salary is never money on the account: a payslip or an accountant's payroll document only
// counts in the payday plan once the person marks it received, or a "salary paid" message says so.
// Fictional names and amounts only.

public class IncomeReceiptTests
{
    private static readonly DateTime Today = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private static FinancialItem Pay(string id, FinKind kind, int dayOffset, IncomeEvidence evidence, UserState state = UserState.None) => new()
    {
        Id = id, UserId = "u1", Kind = kind, Title = "Salary from Kuura Software", Counterparty = "Kuura Software", Net = 2412.40m, Amount = 2412.40m,
        EventDate = Today.AddDays(dayOffset), IncomeEvidence = evidence, UserState = state, CreatedUtc = Today, UpdatedUtc = Today,
    };

    private static CashFlowPlan PlanFor(params FinancialItem[] items)
    {
        // Payday a month away, so pay dated this week is income inside the period, not the next salary
        var settings = new CompanionSettings { Balance = 300m, BalanceAsOfUtc = Today.AddDays(-10), PaydayDay = 18 };
        return CashFlowPlanner.Plan(items, settings, ItemRules.Income(items, settings, Today), Today);
    }

    [Fact]
    public void An_expected_payslip_in_the_period_is_listed_not_counted()
    {
        var plan = PlanFor(Pay("slip", FinKind.Payslip, 3, IncomeEvidence.Payslip));

        Assert.Equal(0m, plan.IncomeTotal);
        Assert.Contains(plan.NotCounted, n => n.ItemId == "slip" && n.Reason.Contains("not yet received"));
    }

    [Fact]
    public void A_payslip_whose_date_has_passed_is_still_expected_until_confirmed()
    {
        var plan = PlanFor(Pay("slip", FinKind.Payslip, -4, IncomeEvidence.Payslip));

        Assert.Equal(0m, plan.IncomeTotal);
        Assert.Equal(300m, plan.ProjectedRemainder + plan.ObligationsTotal + plan.RecurringTotal + plan.EverydayTotal);
        Assert.Contains(plan.NotCounted, n => n.ItemId == "slip" && n.Reason.Contains("not confirmed as received"));
    }

    [Fact]
    public void Marking_a_payslip_received_counts_it_once()
    {
        var plan = PlanFor(Pay("slip", FinKind.Payslip, -4, IncomeEvidence.Payslip, UserState.ReportedPaid));

        Assert.Equal(2412.40m, Assert.Single(plan.Income).Amount);
        Assert.DoesNotContain(plan.NotCounted, n => n.ItemId == "slip");
    }

    [Fact]
    public void A_payslip_and_its_salary_paid_message_count_once_and_the_payslip_is_not_listed_as_missing()
    {
        var plan = PlanFor(Pay("slip", FinKind.Payslip, -4, IncomeEvidence.Payslip), Pay("bank", FinKind.Salary, -4, IncomeEvidence.ReportedReceived));

        Assert.Equal("bank", Assert.Single(plan.Income).ItemId);
        Assert.DoesNotContain(plan.NotCounted, n => n.ItemId == "slip");
    }

    [Fact]
    public void Received_income_has_its_own_status_and_rule()
    {
        var marked = Pay("slip", FinKind.Payslip, -4, IncomeEvidence.Payslip, UserState.ReportedPaid);
        Assert.True(ItemRules.IncomeReceived(marked));
        Assert.Equal(DisplayStatus.IncomeReceived, ItemRules.StatusOf(marked, Today).Status);

        var expected = Pay("slip2", FinKind.Payslip, -4, IncomeEvidence.Payslip);
        Assert.False(ItemRules.IncomeReceived(expected));
        Assert.Equal(DisplayStatus.ExpectedIncome, ItemRules.StatusOf(expected, Today).Status);

        Assert.False(ItemRules.IncomeReceived(Pay("na", FinKind.Salary, -4, IncomeEvidence.ReportedReceived, UserState.NotApplicable)));
    }

    // Found on a local run: a payslip for 23 Oct with the payday entered as the 30th listed the same
    // October pay twice under expected income, and once marked received it stayed "expected" in reports
    [Fact]
    public void An_expected_payslip_and_the_payday_estimate_are_one_pay_in_the_report()
    {
        var settings = new CompanionSettings { Balance = 450m, BalanceAsOfUtc = Today, PaydayDay = 30 };
        FinancialItem[] items = [Pay("slip", FinKind.Payslip, 4, IncomeEvidence.Payslip)];

        var report = MonthlyReports.For(items, 2026, 10, Today, ItemRules.Income(items, settings, Today));

        Assert.Equal(2412.40m, report.ExpectedIncomeTotal);
        Assert.Equal("slip", Assert.Single(report.ExpectedIncome).ItemId);
        Assert.Equal(0m, report.IncomeTotal);
    }

    [Fact]
    public void Pay_marked_received_counts_once_in_the_plan_the_report_and_month_by_month()
    {
        var settings = new CompanionSettings { Balance = 450m, BalanceAsOfUtc = Today, PaydayDay = 30 };
        FinancialItem[] items = [Pay("slip", FinKind.Payslip, 4, IncomeEvidence.Payslip, UserState.ReportedPaid)];
        var outlook = ItemRules.Income(items, settings, Today);

        var plan = CashFlowPlanner.Plan(items, settings, outlook, Today);
        var report = MonthlyReports.For(items, 2026, 10, Today, outlook);
        var month = MonthlySummary.Build(items, Today).Single(m => m.Year == 2026 && m.Month == 10);

        Assert.Equal(2412.40m, plan.IncomeTotal);
        Assert.Equal(2412.40m, report.IncomeTotal);
        Assert.Empty(report.ExpectedIncome);
        Assert.Equal(2412.40m, month.IncomeReportedReceived);
    }

    [Fact]
    public async Task Marking_pay_received_records_receiving_not_paying()
    {
        var rig = new CompanionRig();
        var t = DateTime.UtcNow.Date;
        rig.Google.Add("m-pay", "Kuura Software <payroll@kuura.example>", "Palkkalaskelma lokakuu", Mails.Payslip(t.AddDays(-2)), t.AddDays(-3));
        await rig.ConnectAsync("alice");
        await rig.Engine.SyncAsync("alice", default);
        var slip = rig.Store.Items.Values.Single(i => i.IsIncome);

        var item = await rig.Service.SetStateAsync("alice", slip.Id, UserState.ReportedPaid, default);

        Assert.True(ItemRules.IncomeReceived(item!));
        Assert.Contains(item!.Evidence, e => e.StartsWith("You reported receiving this"));
    }
}
