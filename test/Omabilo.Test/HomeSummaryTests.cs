using Xunit;

// The Nordic Home: how things look, bills ahead, the money flow and the one next step all come from
// saved records and the payday plan, never a guess. Fictional names and amounts only.

public class HomeSummaryTests
{
    private static readonly DateTime Today = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private static FinancialItem Bill(string id, string party, decimal? amount, int dueIn, UserState state = UserState.None, double confidence = 0.9) => new()
    {
        Id = id, UserId = "u1", Kind = FinKind.Invoice, Title = $"Invoice from {party}", Counterparty = party, Amount = amount,
        DueDate = Today.AddDays(dueIn), Confidence = confidence, UserState = state, CreatedUtc = Today, UpdatedUtc = Today,
    };

    private static CashFlowPlan Plan(decimal? balance, decimal income, decimal bills, decimal recurring, decimal everyday, decimal? left) =>
        new(Today, Today.AddDays(6), "19 Oct – 25 Oct 2026", balance, "You entered it", [], [], [], [], [],
            income, bills, recurring, everyday, left, "Balance + income − bills − usual costs");

    private static IReadOnlyList<Reminder> Reminders(params FinancialItem[] items) => Attention.Build(items, null, [], Today);

    [Fact]
    public void Health_is_on_track_only_when_nothing_is_late_or_coming_up()
    {
        var calm = HomeSummary.Health(Reminders(Bill("far", "Kotikatu Rent", 820m, 20)), null);
        Assert.Equal((HealthLevel.Steady, "On track", "success"), (calm.Level, calm.Title, calm.Tone));

        var soon = HomeSummary.Health(Reminders(Bill("soon", "Kuusi Water Oy", 30m, 3)), null);
        Assert.Equal(HealthLevel.Watch, soon.Level);
        Assert.Contains("1 payment due in the next 7 days", soon.Facts);

        var late = HomeSummary.Health(Reminders(Bill("late", "Revontuli Energy Oy", 64.20m, -2), Bill("soon", "Kuusi Water Oy", 30m, 3)), null);
        Assert.Equal((HealthLevel.Act, "error"), (late.Level, late.Tone));
        Assert.Equal("1 bill overdue", late.Facts[0]);
    }

    [Fact]
    public void A_shortfall_in_the_payday_plan_needs_attention()
    {
        var h = HomeSummary.Health([], Plan(200m, 0m, 350m, 0m, 0m, -150m));
        Assert.Equal(HealthLevel.Act, h.Level);
        Assert.Contains("Bills are more than your money until payday", h.Facts);
    }

    [Fact]
    public void Bills_ahead_count_open_bills_and_skip_paid_ones()
    {
        var a = HomeSummary.Ahead([
            Bill("late", "Revontuli Energy Oy", 64.20m, -2),
            Bill("soon", "Kuusi Water Oy", 30m, 3),
            Bill("later", "Kotikatu Rent", 820m, 25),
            Bill("noamount", "Nimbus Mobile", null, 10),
            Bill("beyond", "Lumi Insurance", 45m, 40),
            Bill("paid", "Lumi Insurance", 45m, 5, UserState.ReportedPaid),
        ], Today);

        // A bill without an amount isn't an open obligation yet, so it adds nothing here
        Assert.Equal((850m, 2, 64.20m, 1), (a.DueTotal, a.DueCount, a.OverdueTotal, a.OverdueCount));
    }

    [Fact]
    public void Weeks_group_open_bills_by_when_they_fall_due()
    {
        var w = HomeSummary.Weeks([Bill("a", "Kuusi Water Oy", 30m, 0), Bill("b", "Nimbus Mobile", 19.90m, 6), Bill("c", "Kotikatu Rent", 820m, 9), Bill("late", "Revontuli Energy Oy", 64.20m, -2)], Today);

        Assert.Equal(["This week", "Next week", "In 2 weeks", "In 3 weeks"], w.Select(x => x.Label));
        Assert.Equal([49.90m, 820m, 0m, 0m], w.Select(x => x.Total));
        Assert.Equal([2, 1, 0, 0], w.Select(x => x.Count));
    }

    [Fact]
    public void The_money_flow_splits_in_and_out_and_keeps_an_unknown_remainder_unknown()
    {
        var f = HomeSummary.Flow(Plan(400m, 2100m, 900m, 120m, 140m, 1340m));
        Assert.Equal(["Balance", "Income"], f.In.Select(p => p.Label));
        Assert.Equal(["Bills", "Recurring", "Everyday"], f.Out.Select(p => p.Label));
        Assert.Equal((2500m, 1160m, (decimal?)1340m), (f.InTotal, f.OutTotal, f.Left));

        var noBalance = HomeSummary.Flow(Plan(null, 2100m, 900m, 0m, 0m, null));
        Assert.Equal(["Income"], noBalance.In.Select(p => p.Label));
        Assert.Null(noBalance.Left);
    }

    [Fact]
    public void Debts_and_goals_at_a_glance_come_from_what_the_person_entered()
    {
        var debt = new DebtRecord { Id = "d1", UserId = "u1", Name = "Lumi card", Balance = 1200m, MinPayment = 60m };
        var loan = new DebtRecord { Id = "d2", UserId = "u1", Name = "Car loan", Balance = 3400m };
        var goal = new GoalProgress(new GoalRecord { Id = "g1", UserId = "u1", Name = "Emergency fund", Target = 3000m }, 1200m, 40, [], "");

        var p = HomeSummary.Plans(new MyPlansSnapshot([debt, loan], [goal]));
        Assert.Equal((4600m, 2, 60m, 1200m, 3000m, 1), (p.DebtTotal, p.DebtCount, p.MonthlyPayments, p.Saved, p.Target, p.GoalCount));

        var none = HomeSummary.Plans(new MyPlansSnapshot([], []));
        Assert.Equal((0, 0), (none.DebtCount, none.GoalCount));
    }

    [Fact]
    public void The_next_step_is_the_most_urgent_thing()
    {
        var late = HomeSummary.Next(Reminders(Bill("late", "Revontuli Energy Oy", 64.20m, -2), Bill("soon", "Kuusi Water Oy", 30m, 3)), null, true);
        Assert.Equal((NextActionKind.OpenItem, "late"), (late.Kind, late.ItemId));
        Assert.Contains("Revontuli Energy Oy", late.Title);

        var shortfall = HomeSummary.Next(Reminders(Bill("soon", "Kuusi Water Oy", 30m, 3)), Plan(100m, 0m, 300m, 0m, 0m, -200m), true);
        Assert.Equal(NextActionKind.OpenPayday, shortfall.Kind);

        var noBalance = HomeSummary.Next(Reminders(Bill("soon", "Kuusi Water Oy", 30m, 3)), Plan(null, 0m, 30m, 0m, 0m, null), true);
        Assert.Equal("Add your current balance", noBalance.Title);

        var soonFree = HomeSummary.Next(Reminders(Bill("soon", "Kuusi Water Oy", 30m, 3)), null, true);
        Assert.Equal((NextActionKind.OpenItem, "soon"), (soonFree.Kind, soonFree.ItemId));

        Assert.Equal(NextActionKind.OpenInbox, HomeSummary.Next([], null, true).Kind);
        Assert.Equal(NextActionKind.AddBill, HomeSummary.Next([], null, false).Kind);
    }

    [Fact]
    public void Today_lists_late_bills_first_and_each_record_once()
    {
        var late = Bill("late", "Revontuli Energy Oy", 64.20m, -2);
        var unsure = Bill("unsure", "Kuusi Water Oy", 30m, 12, confidence: 0.3) with { Missing = ["due date"] };
        var missing = Bill("missing", "Kotikatu Rent", null, 20) with { Missing = ["amount"] };
        var done = Bill("done", "Lumi Telecom", 25m, -5, state: UserState.ReportedPaid) with { Missing = ["reference"] };
        var items = new[] { missing, unsure, late, done };

        var list = HomeSummary.TodayList(Reminders(items), items, Plan(null, 0m, 0m, 0m, 0m, null));

        Assert.Equal(TodayKind.Overdue, list[0].Kind);
        Assert.Equal("late", list[0].ItemId);
        Assert.Single(list, t => t is { Kind: TodayKind.Missing, ItemId: "unsure" });
        Assert.Contains(list, t => t is { Kind: TodayKind.Missing, ItemId: "missing" });
        Assert.DoesNotContain(list, t => t.ItemId == "done");
        Assert.Equal(TodayKind.AddBalance, list[^1].Kind);
        Assert.DoesNotContain(HomeSummary.TodayList([], [], null), t => t.Kind == TodayKind.AddBalance);
    }

    [Fact]
    public void Upcoming_bills_are_open_bills_with_an_amount_soonest_first()
    {
        var items = new[] { Bill("far", "Kotikatu Rent", 820m, 20), Bill("near", "Kuusi Water Oy", 30m, 3), Bill("late", "Revontuli Energy Oy", 64m, -2), Bill("blank", "Lumi Telecom", null, 5) };

        Assert.Equal(["near", "far"], HomeSummary.UpcomingBills(items, Today, 4).Select(i => i.Id));
    }
}
