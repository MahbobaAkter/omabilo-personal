using Xunit;

/// <summary>The V1 menu, monthly reports, debt payments in the payday plan and the finance assistant.
/// Fictional Finnish companies and amounts only.</summary>
public class AppSectionTests
{
    [Fact]
    public void Debts_and_goals_are_separate_destinations_with_their_own_addresses()
    {
        var debts = AppSections.ByKey("my-debts");
        var goals = AppSections.ByKey("my-goals");

        Assert.Equal(("Debts & Loans", "/debts"), (debts.Label, debts.Path));
        Assert.Equal(("Goals & Savings", "/goals"), (goals.Label, goals.Path));
        Assert.Equal("target", goals.IconName);
        Assert.DoesNotContain(AppSections.All, s => s.Label.Contains("Debt") && s.Label.Contains("Goal"));
        Assert.DoesNotContain(AppSections.All, s => s.IconName.Contains("piggy"));
        Assert.Contains("my-debts", AppSections.PhoneBar);
    }

    [Fact]
    public void The_menu_has_the_nine_destinations_in_order_each_with_a_unique_key_and_path()
    {
        Assert.Equal(["Home", "Financial Inbox", "My Money", "Debts & Loans", "Goals & Savings", "Payday Planner", "Monthly Reports", "Finland Tax & Finance Assistant", "Settings"],
            AppSections.All.Select(s => s.Label));
        Assert.Equal(AppSections.All.Count, AppSections.All.Select(s => s.Key).Distinct().Count());
        Assert.Equal(AppSections.All.Count, AppSections.All.Select(s => s.Path).Distinct().Count());
        Assert.All(AppSections.All, s => Assert.False(s.Path.StartsWith("/api") || s.Path.StartsWith("/ikon")));
    }

    [Theory]
    [InlineData("/debts", "my-debts")]
    [InlineData("/goals/", "my-goals")]
    [InlineData("/REPORTS", "my-reports")]
    [InlineData("/payday?x=1", "my-payday")]
    [InlineData("/", "my-overview")]
    [InlineData(null, "my-overview")]
    [InlineData("/nothing-here", "my-overview")]
    [InlineData("/plans", "my-debts")]
    [InlineData("/money", "my-money")]
    [InlineData("/invoices", "my-money")]
    public void A_path_opens_its_destination_and_unknown_paths_open_home(string? path, string key) =>
        Assert.Equal(key, AppSections.ForPath(path).Key);

    [Fact]
    public void The_old_invoices_key_opens_my_money_and_is_no_longer_in_the_menu()
    {
        Assert.Equal("my-money", AppSections.ByKey("my-invoices").Key);
        Assert.DoesNotContain(AppSections.All, s => s.Key == "my-invoices" || s.Label == "Invoices");
        Assert.Contains("my-money", AppSections.PhoneBar);
    }
}

public class MonthlyReportTests
{
    private static readonly DateTime Today = new(2026, 10, 19);

    private static FinancialItem Bill(string id, decimal? amount, DateTime due, UserState state = UserState.None, string company = "Revontuli Energy Oy", string category = Categories.Utilities) => new()
    {
        Id = id, UserId = "u1", Kind = FinKind.Invoice, Title = $"Bill from {company}", Counterparty = company, Category = category, Amount = amount, DueDate = due, UserState = state,
    };

    private static FinancialItem Pay(string id, FinKind kind, decimal net, DateTime date, IncomeEvidence evidence, UserState state = UserState.None, decimal? gross = null) => new()
    {
        Id = id, UserId = "u1", Kind = kind, Title = "Salary from Kuura Software", Counterparty = "Kuura Software Oy", Category = Categories.Income,
        Net = net, Amount = net, Gross = gross, EventDate = date, IncomeEvidence = evidence, UserState = state,
    };

    private static FinancialItem Receipt(string id, FinKind kind, decimal amount, DateTime date, string company = "Lähikauppa Oy", string category = Categories.Groceries) => new()
    {
        Id = id, UserId = "u1", Kind = kind, Title = $"{kind} from {company}", Counterparty = company, Category = category, Amount = amount, EventDate = date,
    };

    [Fact]
    public void Income_minus_paid_expenses_gives_the_net_and_unpaid_bills_are_kept_apart()
    {
        var items = new[]
        {
            Pay("sal", FinKind.Salary, 2412.40m, new(2026, 10, 15), IncomeEvidence.ReportedReceived),
            Bill("paid", 64.20m, new(2026, 10, 5), UserState.ReportedPaid),
            Bill("open", 30m, new(2026, 10, 28)),
            Bill("late", 12m, new(2026, 10, 2)),
            Receipt("r1", FinKind.Receipt, 45.10m, new(2026, 10, 9)),
            Receipt("ref", FinKind.Refund, 10m, new(2026, 10, 11), "Verkkokauppa Oy", Categories.Shopping),
            Bill("dup", 64.20m, new(2026, 10, 5), UserState.ReportedPaid) with { PossibleDuplicateOf = "paid" },
            Bill("na", 99m, new(2026, 10, 6), UserState.ReportedPaid) with { UserState = UserState.NotApplicable },
        };

        var r = MonthlyReports.For(items, 2026, 10, Today);

        Assert.Equal(2412.40m, r.IncomeTotal);
        Assert.Equal(64.20m + 45.10m - 10m, r.ExpenseTotal);
        Assert.Equal(2412.40m - 99.30m, r.Net);
        Assert.Equal(["open", "late"], r.BillsStillOpen.Select(l => l.ItemId).OrderByDescending(x => x));
        Assert.Equal(42m, r.BillsStillOpenTotal);
        Assert.Contains(r.ExpenseCategories, c => c.Category == Categories.Shopping && c.Amount == -10m);
        Assert.Equal(r.ExpenseTotal, r.ExpenseCategories.Sum(c => c.Amount));
    }

    [Fact]
    public void A_payslip_and_a_salary_paid_email_for_the_same_pay_count_once_and_a_payslip_alone_is_not_money_received()
    {
        var both = MonthlyReports.For([
            Pay("slip", FinKind.Payslip, 2412.40m, new(2026, 10, 15), IncomeEvidence.Payslip),
            Pay("bank", FinKind.Salary, 2412.40m, new(2026, 10, 15), IncomeEvidence.ReportedReceived),
        ], 2026, 10, Today);

        Assert.Equal("bank", Assert.Single(both.Income).ItemId);
        Assert.Empty(both.PayslipsNotConfirmed);

        var slipOnly = MonthlyReports.For([Pay("slip", FinKind.Payslip, 2412.40m, new(2026, 10, 15), IncomeEvidence.Payslip)], 2026, 10, Today);
        Assert.Equal(0m, slipOnly.IncomeTotal);
        Assert.Single(slipOnly.PayslipsNotConfirmed);

        var marked = MonthlyReports.For([Pay("slip", FinKind.Payslip, 2412.40m, new(2026, 10, 15), IncomeEvidence.Payslip, UserState.ReportedPaid)], 2026, 10, Today);
        Assert.Equal(2412.40m, marked.IncomeTotal);
    }

    [Fact]
    public void Future_salary_is_expected_income_not_income()
    {
        var r = MonthlyReports.For([Pay("next", FinKind.Payslip, 2412.40m, new(2026, 10, 30), IncomeEvidence.Scheduled)], 2026, 10, Today);

        Assert.Equal(0m, r.IncomeTotal);
        Assert.Equal(2412.40m, r.ExpectedIncomeTotal);
    }

    [Fact]
    public void A_receipt_for_a_bill_already_counted_as_paid_is_not_counted_again()
    {
        var r = MonthlyReports.For([
            Bill("bill", 39.90m, new(2026, 10, 5), UserState.ReportedPaid, "Elisa Oyj", Categories.PhoneInternet),
            Receipt("rcpt", FinKind.Receipt, 39.90m, new(2026, 10, 6), "Elisa Oyj", Categories.PhoneInternet),
        ], 2026, 10, Today);

        Assert.Equal(39.90m, r.ExpenseTotal);
        Assert.Contains(r.LeftOut, l => l.Contains("same payment"));
    }

    [Fact]
    public void Months_without_records_are_empty_and_other_currencies_are_left_out_with_a_note()
    {
        var reports = MonthlyReports.Build([Bill("usd", 20m, new(2026, 10, 5), UserState.ReportedPaid) with { Currency = "USD" }], Today);

        Assert.Equal(6, reports.Count);
        Assert.Equal((2026, 10), (reports[0].Year, reports[0].Month));
        Assert.All(reports, r => Assert.False(r.HasActuals));
        Assert.Contains(reports[0].LeftOut, l => l.Contains("another currency"));
    }
}

public class PaydayDebtTests
{
    private static readonly DateTime Today = new(2026, 10, 19);

    private static CashFlowPlan PlanWith(IReadOnlyList<FinancialItem> items, params DebtRecord[] debts)
    {
        var settings = new CompanionSettings { Balance = 1000m, BalanceAsOfUtc = Today, PaydayDay = 30, EverydaySpendPerDay = 0m, SavingsPerPeriod = 50m };
        return CashFlowPlanner.Plan(items, settings, ItemRules.Income(items, settings, Today), Today, debts);
    }

    private static DebtRecord Loan(int dueDay, decimal payment = 120m, DateTime? lastPaid = null) => new()
    {
        Id = "d1", UserId = "u1", Name = "Car loan", Lender = "Pohjola Rahoitus Oy", Balance = 4000m, MinPayment = payment, DueDay = dueDay,
        LastPaymentUtc = lastPaid, CreatedUtc = new(2026, 1, 1),
    };

    [Fact]
    public void A_debt_payment_due_before_payday_and_planned_savings_are_taken_off_what_is_left()
    {
        var plan = PlanWith([], Loan(25));

        Assert.Equal([120m, 50m], plan.Planned.Select(p => p.Amount));
        Assert.Equal(1000m - 170m, plan.ProjectedRemainder);
    }

    [Fact]
    public void A_debt_payment_after_payday_or_already_recorded_is_not_counted()
    {
        Assert.DoesNotContain(PlanWith([], Loan(5)).Planned, p => p.Label.Contains("Car loan"));

        var recorded = PlanWith([], Loan(25, lastPaid: new(2026, 10, 18)));
        Assert.DoesNotContain(recorded.Planned, p => p.Label.Contains("Car loan"));
        Assert.Contains(recorded.NotCounted, n => n.Label.Contains("Car loan") && n.Reason.Contains("recorded a payment"));
    }

    [Fact]
    public void A_loan_bill_from_the_same_lender_is_counted_once_not_twice()
    {
        var bill = new FinancialItem
        {
            Id = "b1", UserId = "u1", Kind = FinKind.LoanInstalment, Title = "Loan instalment", Counterparty = "Pohjola Rahoitus Oy", Amount = 120m, DueDate = new(2026, 10, 25),
        };

        var plan = PlanWith([bill], Loan(25));

        Assert.Equal(120m, plan.ObligationsTotal);
        Assert.DoesNotContain(plan.Planned, p => p.Label.Contains("Car loan"));
        Assert.Equal(1000m - 120m - 50m, plan.ProjectedRemainder);
    }
}

public class DebtPaymentTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);

    private static async Task<(PlanService Service, InMemoryCompanionStore Store, string DebtId)> RigAsync()
    {
        var store = new InMemoryCompanionStore();
        var service = new PlanService(store, () => Now);
        Assert.Empty(await service.SaveDebtAsync("alice", null, new DebtForm("Credit card", "Nordea", MyDebtKind.CreditCard, "1 000,00", "18", "100", "20"), default));
        return (service, store, store.Debts.Values.Single().Id);
    }

    [Fact]
    public async Task Recording_a_payment_lowers_the_balance_shows_progress_and_can_be_undone()
    {
        var (service, store, id) = await RigAsync();

        var (before, error) = await service.RecordDebtPaymentAsync("alice", id, 250m, default);

        Assert.Null(error);
        var debt = store.Debts[id];
        Assert.Equal((750m, 1000m, 250m), (debt.Balance, debt.StartingBalance!.Value, debt.LastPaymentAmount!.Value));
        Assert.Equal(25d, PlanRules.PaidPercent(debt));
        Assert.Contains(store.Audit, a => a.Entry.Action == "debt_payment_recorded");

        Assert.True(await service.RestoreDebtAsync("alice", before!, default));
        Assert.Equal(1000m, store.Debts[id].Balance);
    }

    [Fact]
    public async Task A_payment_above_the_balance_or_on_someone_elses_debt_is_refused()
    {
        var (service, store, id) = await RigAsync();

        Assert.NotNull((await service.RecordDebtPaymentAsync("alice", id, 1500m, default)).Error);
        Assert.NotNull((await service.RecordDebtPaymentAsync("alice", id, 0m, default)).Error);
        Assert.NotNull((await service.RecordDebtPaymentAsync("bob", id, 10m, default)).Error);
        Assert.False(await service.RestoreDebtAsync("bob", store.Debts[id] with { Balance = 0m }, default));
        Assert.Equal(1000m, store.Debts[id].Balance);
    }

    [Fact]
    public async Task Editing_a_debt_keeps_the_starting_balance_and_the_payments_recorded()
    {
        var (service, store, id) = await RigAsync();
        await service.RecordDebtPaymentAsync("alice", id, 100m, default);

        await service.SaveDebtAsync("alice", id, new DebtForm("Credit card", "Nordea", MyDebtKind.CreditCard, "880,00", "18", "100", "20"), default);

        Assert.Equal((880m, 1000m, 100m), (store.Debts[id].Balance, store.Debts[id].StartingBalance!.Value, store.Debts[id].LastPaymentAmount!.Value));
    }

    [Fact]
    public void A_passed_due_day_without_a_recorded_payment_is_flagged_but_never_called_unpaid()
    {
        var d = new DebtRecord { Id = "d", UserId = "u", Name = "Loan", Balance = 500m, MinPayment = 50m, DueDay = 10, CreatedUtc = new(2026, 9, 1) };

        Assert.True(PlanRules.PaymentNotRecorded(d, new(2026, 10, 19)));
        Assert.False(PlanRules.PaymentNotRecorded(d with { LastPaymentUtc = new(2026, 10, 9) }, new(2026, 10, 19)));
        Assert.False(PlanRules.PaymentNotRecorded(d, new(2026, 10, 8)));
        Assert.False(PlanRules.PaymentNotRecorded(d with { CreatedUtc = new(2026, 10, 15) }, new(2026, 10, 19)));
    }

    [Fact]
    public void Debt_free_date_needs_a_workable_payment_on_every_debt()
    {
        var a = new DebtRecord { Id = "a", UserId = "u", Name = "A", Balance = 600m, MinPayment = 100m };
        var b = new DebtRecord { Id = "b", UserId = "u", Name = "B", Balance = 300m, MinPayment = 100m };

        Assert.Equal(6, PlanRules.MonthsUntilDebtFree([a, b]));
        Assert.Null(PlanRules.MonthsUntilDebtFree([a, b with { MinPayment = null }]));
        Assert.Equal(["interest rate", "due day"], PlanRules.MissingForPlan(a));
    }
}

public class FinanceAssistantTests
{
    private static readonly DateTime Today = new(2026, 10, 19);

    private static FinancialItem Bill(string id, decimal? amount, decimal? vat, DateTime date, UserState state = UserState.None) => new()
    {
        Id = id, UserId = "u1", Kind = FinKind.Invoice, Title = "Bill", Counterparty = $"Yritys {id} Oy", Amount = amount, Vat = vat, InvoiceDate = date, DueDate = date.AddDays(14), UserState = state,
        Confidence = 0.95, AnalysisVersion = 99,
    };

    [Fact]
    public void VAT_is_summed_from_the_invoices_that_show_it_and_the_rest_are_listed_as_missing()
    {
        var v = FinanceAssistant.Vat([Bill("a", 125.50m, 25.50m, new(2026, 10, 2)), Bill("b", 114m, 14m, new(2026, 10, 3)), Bill("c", 80m, null, new(2026, 10, 4)),
            Bill("old", 50m, 10m, new(2026, 9, 30))], 2026, 10);

        Assert.Equal(39.50m, v.VatTotal);
        Assert.Equal([25.5m, 14m], v.Lines.Select(l => l.RatePercent!.Value));
        Assert.Equal("Yritys c Oy", Assert.Single(v.WithoutVat).Label);
        Assert.Equal(2, FinanceAssistant.Vat([Bill("a", 125.50m, 25.50m, new(2026, 10, 2)), Bill("old", 50m, 10m, new(2026, 9, 30))], 2026, null).Lines.Count);
    }

    [Fact]
    public void The_tax_card_compares_the_income_limit_with_gross_pay_counting_each_pay_once()
    {
        FinancialItem Slip(string id, FinKind kind, DateTime d) => new()
        {
            Id = id, UserId = "u1", Kind = kind, Title = "Pay", Counterparty = "Kuura Software Oy", Gross = 3000m, Net = 2300m, EventDate = d,
        };
        var items = new[] { Slip("s1", FinKind.Payslip, new(2026, 8, 15)), Slip("s2", FinKind.Payslip, new(2026, 9, 15)), Slip("b2", FinKind.Salary, new(2026, 9, 15)), Slip("old", FinKind.Payslip, new(2025, 12, 15)) };

        var status = FinanceAssistant.TaxCard(items, new CompanionSettings { TaxRatePercent = 18m, TaxAdditionalRatePercent = 38m, TaxIncomeLimit = 10000m }, Today);

        Assert.Equal((6000m, 2, 4000m), (status.GrossSoFar, status.Payslips, status.LeftUnderLimit!.Value));
        Assert.Contains("left under your income limit", status.Summary);

        var over = FinanceAssistant.TaxCard(items, new CompanionSettings { TaxIncomeLimit = 5000m, TaxAdditionalRatePercent = 38m }, Today);
        Assert.Contains("over your income limit", over.Summary);
        Assert.Contains("Enter the numbers", FinanceAssistant.TaxCard(items, new CompanionSettings(), Today).Summary);
    }

    [Fact]
    public void Due_before_payday_lists_open_bills_and_debt_payments_from_the_plan()
    {
        var items = new[] { Bill("a", 64.20m, null, new(2026, 10, 10)) };
        var settings = new CompanionSettings { Balance = 500m, BalanceAsOfUtc = Today, PaydayDay = 30, EverydaySpendPerDay = 0m };
        var debt = new DebtRecord { Id = "d", UserId = "u1", Name = "Car loan", Balance = 2000m, MinPayment = 150m, DueDay = 26, CreatedUtc = new(2026, 1, 1) };
        var plan = CashFlowPlanner.Plan(items, settings, ItemRules.Income(items, settings, Today), Today, [debt]);

        var answer = FinanceAssistant.Answer(AssistantQuestion.DueBeforePayday, items, plan, Today);

        Assert.Equal(2, answer.Sources.Count);
        Assert.Contains("214,20", answer.Summary.Replace(' ', ' '));
        Assert.Contains(answer.Sources, s => s.ItemId == "a");
    }

    [Fact]
    public void The_accountant_pack_covers_last_month_and_the_draft_is_text_for_the_person_to_send()
    {
        var items = new[] { Bill("sep", 120m, 23.90m, new(2026, 9, 5)), Bill("oct", 50m, null, new(2026, 10, 5)) };

        var pack = FinanceAssistant.ForAccountant(items, 2026, 9);
        var draft = FinanceAssistant.AccountantDraft(pack, "September 2026");

        Assert.Equal("sep", Assert.Single(pack.Sources).ItemId);
        Assert.StartsWith("Hello,", draft);
        Assert.Contains("Yritys sep Oy", draft);
        Assert.DoesNotContain("Yritys oct Oy", draft);
    }

    [Fact]
    public void Review_this_month_lists_overdue_and_unreviewed_bills_but_not_reviewed_ones()
    {
        var items = new[]
        {
            Bill("late", 40m, null, new(2026, 9, 20)),
            Bill("new", 30m, null, new(2026, 10, 10)) with { DueDate = new(2026, 10, 28) },
            Bill("done", 30m, null, new(2026, 10, 10), UserState.Reviewed) with { DueDate = new(2026, 10, 28) },
        };

        var answer = FinanceAssistant.ReviewThisMonth(items, Today);

        Assert.Contains(answer.Sources, s => s.ItemId == "late");
        Assert.Contains(answer.Sources, s => s.ItemId == "new");
        Assert.DoesNotContain(answer.Sources, s => s.ItemId == "done");
    }

    [Fact]
    public void The_glossary_explains_the_common_finnish_terms_once_each()
    {
        Assert.Equal(FinanceAssistant.Glossary.Count, FinanceAssistant.Glossary.Select(g => g.Finnish).Distinct().Count());
        Assert.Contains(FinanceAssistant.Glossary, g => g.Finnish == "Eräpäivä" && g.English == "Due date");
        Assert.Contains(FinanceAssistant.Glossary, g => g.Finnish == "Viitenumero");
    }
}
