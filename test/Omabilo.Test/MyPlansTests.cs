using Xunit;

// Step 3 of the V1 roadmap: a signed-in person's own debts and savings goals. Add, change, delete,
// reload, and never see or touch another person's records. Fictional names and amounts only.

public class MyPlansTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);

    private static (PlanService Service, InMemoryCompanionStore Store) Rig()
    {
        var store = new InMemoryCompanionStore();
        return (new PlanService(store, () => Now), store);
    }

    private static DebtForm Card(string balance = "1 250,00", string? apr = "18", string? min = "60", string? due = "15") =>
        new("Visa card", "Kuusi Bank", MyDebtKind.CreditCard, balance, apr, min, due);

    [Fact]
    public async Task A_debt_is_added_changed_reloaded_and_deleted()
    {
        var (svc, _) = Rig();

        Assert.Empty(await svc.SaveDebtAsync("alice", null, Card(), default));
        var debt = Assert.Single((await svc.LoadAsync("alice", default)).Debts);
        Assert.Equal(1250m, debt.Balance);
        Assert.Equal(18m, debt.Apr);
        Assert.Equal(15, debt.DueDay);

        Assert.Empty(await svc.SaveDebtAsync("alice", debt.Id, Card(balance: "1 100,00"), default));
        var changed = Assert.Single((await svc.LoadAsync("alice", default)).Debts);
        Assert.Equal(debt.Id, changed.Id);
        Assert.Equal(1100m, changed.Balance);
        Assert.Equal(debt.CreatedUtc, changed.CreatedUtc);

        Assert.True(await svc.DeleteDebtAsync("alice", debt.Id, default));
        Assert.Empty((await svc.LoadAsync("alice", default)).Debts);
    }

    [Fact]
    public async Task Bad_debt_numbers_are_refused_with_reasons()
    {
        var (svc, store) = Rig();
        var errors = await svc.SaveDebtAsync("alice", null, new DebtForm(" ", null, MyDebtKind.Loan, "lots", "250", "-5", "40"), default);

        Assert.Equal(5, errors.Count);
        Assert.Empty(store.Debts);
    }

    [Fact]
    public async Task Another_person_never_sees_changes_or_deletes_my_records()
    {
        var (svc, store) = Rig();
        await svc.SaveDebtAsync("alice", null, Card(), default);
        await svc.SaveGoalAsync("alice", null, new GoalForm("Emergency fund", "1 000", null, true), default);
        var mine = await svc.LoadAsync("alice", default);
        var debtId = mine.Debts[0].Id;
        var goalId = mine.Goals[0].Goal.Id;
        await svc.AddContributionAsync("alice", goalId, 200m, default);

        var theirs = await svc.LoadAsync("bob", default);
        Assert.Empty(theirs.Debts);
        Assert.Empty(theirs.Goals);

        Assert.Equal(["That debt doesn't exist any more."], await svc.SaveDebtAsync("bob", debtId, Card(balance: "1"), default));
        Assert.False(await svc.DeleteDebtAsync("bob", debtId, default));
        Assert.Equal(["That goal doesn't exist any more."], await svc.SaveGoalAsync("bob", goalId, new GoalForm("Mine now", "5", null, false), default));
        Assert.False(await svc.DeleteGoalAsync("bob", goalId, default));
        Assert.Equal("That goal doesn't exist any more.", await svc.AddContributionAsync("bob", goalId, 50m, default));
        Assert.False(await svc.DeleteContributionAsync("bob", store.Contributions.Keys.Single(), default));

        // A forged row with alice's id, saved as bob, doesn't replace hers
        await store.SaveDebtAsync(mine.Debts[0] with { UserId = "bob", Balance = 1m }, default);

        var after = await svc.LoadAsync("alice", default);
        Assert.Equal(1250m, after.Debts.Single().Balance);
        Assert.Equal(200m, after.Goals.Single().Saved);
    }

    [Fact]
    public async Task Goal_money_goes_in_and_out_but_never_below_zero()
    {
        var (svc, _) = Rig();
        await svc.SaveGoalAsync("alice", null, new GoalForm("Holiday in Lapland", "1 200,00", "31.12.2027", false), default);
        var goalId = (await svc.LoadAsync("alice", default)).Goals[0].Goal.Id;

        Assert.Null(await svc.AddContributionAsync("alice", goalId, 300m, default));
        Assert.Null(await svc.AddContributionAsync("alice", goalId, -100m, default));
        Assert.Equal("You can take out at most 200,00 €.", await svc.AddContributionAsync("alice", goalId, -500m, default));
        Assert.Equal("Enter an amount in euros, for example 50,00.", await svc.AddContributionAsync("alice", goalId, null, default));

        var goal = (await svc.LoadAsync("alice", default)).Goals.Single();
        Assert.Equal(200m, goal.Saved);
        Assert.Equal(2, goal.Contributions.Count);
        Assert.InRange(goal.Percent, 16.6, 16.7);
        Assert.Contains("a month until", goal.Pace);

        // Removing a mistyped entry is the undo
        Assert.True(await svc.DeleteContributionAsync("alice", goal.Contributions.First(c => c.Amount < 0).Id, default));
        Assert.Equal(300m, (await svc.LoadAsync("alice", default)).Goals.Single().Saved);
    }

    [Fact]
    public async Task Deleting_a_goal_deletes_its_entries_and_delete_all_removes_plans()
    {
        var (svc, store) = Rig();
        await svc.SaveGoalAsync("alice", null, new GoalForm("Bike", "800", null, false), default);
        var goalId = (await svc.LoadAsync("alice", default)).Goals[0].Goal.Id;
        await svc.AddContributionAsync("alice", goalId, 80m, default);

        Assert.True(await svc.DeleteGoalAsync("alice", goalId, default));
        Assert.Empty(store.Contributions);

        await svc.SaveDebtAsync("alice", null, Card(), default);
        await svc.SaveDebtAsync("bob", null, Card(), default);
        await store.DeleteAllForUserAsync("alice", default);
        Assert.Empty((await svc.LoadAsync("alice", default)).Debts);
        Assert.Single((await svc.LoadAsync("bob", default)).Debts);
    }

    [Fact]
    public void Goal_dates_and_targets_are_checked()
    {
        var (_, errors) = PlanRules.ParseGoal(new GoalForm("Car", "0", "1.1.2020", false), "alice", "g", Now);
        Assert.Equal(2, errors.Count);

        var (goal, ok) = PlanRules.ParseGoal(new GoalForm(" Car ", "3 000", "", false), "alice", "g", Now);
        Assert.Empty(ok);
        Assert.Equal("Car", goal!.Name);
        Assert.Null(goal.TargetDate);
    }

    [Theory]
    [InlineData(1200, 0, 100, 12)]
    [InlineData(1000, 12, 100, 11)]
    [InlineData(0, 10, 50, 0)]
    public void Payoff_months_follow_payment_and_interest(int balance, int apr, int payment, int months) =>
        Assert.Equal(months, PlanRules.MonthsToPayOff(balance, apr, payment));

    [Fact]
    public void A_payment_below_the_interest_never_pays_off()
    {
        Assert.Null(PlanRules.MonthsToPayOff(10_000m, 24m, 150m));
        Assert.Null(PlanRules.MonthsToPayOff(500m, 5m, null));
        Assert.Contains("interest grows faster", PlanRules.PayoffText(new DebtRecord { Id = "d", UserId = "a", Balance = 10_000m, Apr = 24m, MinPayment = 150m }));
    }
}
