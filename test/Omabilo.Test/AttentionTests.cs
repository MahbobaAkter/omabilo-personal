using Xunit;

// Step 4 of the V1 roadmap: Home's reminders come only from saved records. Fictional names and amounts only.

public class AttentionTests
{
    private static readonly DateTime Today = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private static FinancialItem Bill(string id, string party, decimal? amount, int? dueIn, UserState state = UserState.None, double confidence = 0.9) => new()
    {
        Id = id, UserId = "u1", Kind = FinKind.Invoice, Title = $"Invoice from {party}", Counterparty = party, Amount = amount,
        DueDate = dueIn is { } d ? Today.AddDays(d) : null, Confidence = confidence, UserState = state, CreatedUtc = Today, UpdatedUtc = Today,
    };

    private static DebtRecord Debt(string id, int? dueDay, decimal balance = 1250m) =>
        new() { Id = id, UserId = "u1", Name = "Test Visa card", Balance = balance, Apr = 18m, MinPayment = 60m, DueDay = dueDay };

    [Fact]
    public void Bills_are_listed_by_urgency_and_each_only_once()
    {
        var items = new List<FinancialItem>
        {
            Bill("soon", "Kuusi Water Oy", 30m, 5),
            Bill("late", "Revontuli Energy Oy", 64.20m, -3),
            Bill("today", "Nimbus Mobile", 19.90m, 0),
            Bill("unsure-late", "Kotikatu", 120m, -1, confidence: 0.4),
            Bill("far", "Kotikatu Rent", 820m, 20),
            Bill("paid", "Lumi Insurance", 45m, 2, UserState.ReportedPaid),
        };

        var r = Attention.Build(items, null, [], Today);

        Assert.Equal(["late", "unsure-late", "today", "soon"], r.Select(x => x.ItemId));
        Assert.Equal([ReminderKind.Overdue, ReminderKind.Overdue, ReminderKind.DueToday, ReminderKind.DueSoon], r.Select(x => x.Kind));
        Assert.Equal("64,20 € was due 16 Oct 2026, 3 days ago", r[0].Detail);
        Assert.Equal("30,00 € due 24 Oct 2026, in 5 days", r[3].Detail);
        Assert.All(r, x => Assert.True(x.IsTask));
    }

    [Fact]
    public void An_unclear_item_without_a_deadline_asks_for_review()
    {
        var r = Attention.Build([Bill("unsure", "Unknown sender", 50m, 30, confidence: 0.4)], null, [], Today);

        var only = Assert.Single(r);
        Assert.Equal(ReminderKind.NeedsReview, only.Kind);
        Assert.StartsWith("Check this:", only.Detail);

        var reviewed = Bill("unsure", "Unknown sender", 50m, 30, UserState.Reviewed, confidence: 0.4);
        Assert.Empty(Attention.Build([reviewed], null, [], Today));
    }

    [Fact]
    public void A_debt_payment_due_within_a_week_is_a_reminder()
    {
        var r = Attention.Build([], null, [Debt("d-soon", 22), Debt("d-later", 5), Debt("d-nodate", null), Debt("d-paid", 20, balance: 0)], Today);

        var only = Assert.Single(r);
        Assert.Equal("d-soon", only.DebtId);
        Assert.Equal(ReminderKind.DebtPayment, only.Kind);
        Assert.StartsWith("60,00 € due 22 Oct 2026, in 3 days", only.Detail);
    }

    [Theory]
    [InlineData(19, 2026, 10, 19)]
    [InlineData(25, 2026, 10, 25)]
    [InlineData(5, 2026, 11, 5)]
    public void The_next_due_date_is_this_month_or_next(int day, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), Attention.NextDueDate(day, Today));

    [Fact]
    public void A_due_day_past_the_end_of_a_short_month_falls_on_its_last_day() =>
        Assert.Equal(new DateTime(2027, 2, 28), Attention.NextDueDate(31, new DateTime(2027, 2, 10)));

    [Fact]
    public void Payday_within_a_week_shows_last_and_is_not_a_task()
    {
        var income = new IncomeOutlook(Today.AddDays(4), "From the payday you entered.", IncomeEvidence.UserEntered, 2400m, "", IncomeEvidence.UserEntered, []);
        var r = Attention.Build([Bill("soon", "Kuusi Water Oy", 30m, 6)], income, [], Today);

        Assert.Equal([ReminderKind.DueSoon, ReminderKind.Payday], r.Select(x => x.Kind));
        Assert.False(r[1].IsTask);
        Assert.StartsWith("About 2 400,00 € expected 23 Oct 2026", r[1].Detail.Replace(' ', ' '));

        var far = income with { NextPayday = Today.AddDays(12) };
        Assert.Empty(Attention.Build([], far, [], Today));
    }
}
