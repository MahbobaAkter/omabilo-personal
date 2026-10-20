// What the top of Home says: how things look overall, the bills ahead, where the money goes until
// payday, and the one thing worth doing next. Plain C# built only from the person's saved records,
// their reminders and the payday plan, so the tests check what the screen draws. Nothing here is a
// score or a guess about the bank: each level and number comes from records Omabilo holds.

public enum HealthLevel { Steady, Watch, Act }

/// <param name="Tone">success, warning or error: the dot and pill colour.</param>
/// <param name="Facts">Short lines saying why, most urgent first.</param>
public sealed record HomeHealth(HealthLevel Level, string Title, string Tone, IReadOnlyList<string> Facts);

/// <summary>Open bills due in the next <see cref="HomeSummary.AheadDays"/> days, and those already overdue.</summary>
public sealed record BillsAhead(decimal DueTotal, int DueCount, decimal OverdueTotal, int OverdueCount);

/// <param name="Role">in, bills, recurring, everyday or planned: picks the segment colour.</param>
public sealed record FlowPart(string Label, decimal Amount, string Role);

/// <summary>Money in and out until payday, from the payday plan. Left is null without a balance.</summary>
public sealed record MoneyFlow(string PeriodLabel, IReadOnlyList<FlowPart> In, IReadOnlyList<FlowPart> Out, decimal InTotal, decimal OutTotal, decimal? Left);

/// <summary>Open bills grouped by the week they fall due, for the free plan's view of the weeks ahead.</summary>
public sealed record BillWeek(string Label, decimal Total, int Count);

/// <summary>The person's own debts and goals, as they entered them on the Debts and Goals screens.</summary>
public sealed record PlansAtAGlance(decimal DebtTotal, int DebtCount, decimal MonthlyPayments, decimal Saved, decimal Target, int GoalCount);

public enum NextActionKind { OpenItem, OpenDebt, OpenPayday, AddBill, OpenInbox }

public enum TodayKind { Overdue, DueToday, Review, Missing, Unread, AddBalance }

/// <summary>One row of Home's Today list: what it is, why, and the one button that opens it.</summary>
public sealed record TodayItem(TodayKind Kind, string Title, string Detail, string Action, string? ItemId = null);

public sealed record NextAction(NextActionKind Kind, string Title, string Why, string ButtonText, string? ItemId = null);

public static class HomeSummary
{
    public const int AheadDays = 30;

    public static HomeHealth Health(IReadOnlyList<Reminder> reminders, CashFlowPlan? plan)
    {
        var overdue = reminders.Count(r => r.Kind == ReminderKind.Overdue);
        var today = reminders.Count(r => r.Kind == ReminderKind.DueToday);
        var soon = reminders.Count(r => r.Kind is ReminderKind.DueSoon or ReminderKind.DebtPayment);
        var check = reminders.Count(r => r.Kind == ReminderKind.NeedsReview);
        var shortfall = plan?.Shortfall == true;

        var facts = new List<string>();
        if (overdue > 0) facts.Add(Count(overdue, "bill") + " overdue");
        if (shortfall) facts.Add("Bills are more than your money until payday");
        if (today > 0) facts.Add(Count(today, "bill") + " due today");
        if (soon > 0) facts.Add(Count(soon, "payment") + $" due in the next {Attention.SoonDays} days");
        if (check > 0) facts.Add(Count(check, "record") + " to check");

        if (overdue > 0 || shortfall)
        {
            return new(HealthLevel.Act, "Needs attention", "error", facts);
        }

        if (today > 0 || soon > 0 || check > 0)
        {
            return new(HealthLevel.Watch, "A few things coming up", "warning", facts);
        }

        return new(HealthLevel.Steady, "On track", "success", [$"Nothing overdue or due in the next {Attention.SoonDays} days"]);
    }

    public static BillsAhead Ahead(IEnumerable<FinancialItem> items, DateTime today)
    {
        today = today.Date;
        var open = items.Where(i => OpenBill(i)).ToList();
        var overdue = open.Where(i => ItemRules.StatusOf(i, today).Status == DisplayStatus.Overdue).ToList();
        var due = open.Where(i => !overdue.Contains(i) && i.DueDate!.Value.Date >= today && i.DueDate.Value.Date <= today.AddDays(AheadDays)).ToList();

        return new(due.Sum(i => i.Amount!.Value), due.Count, overdue.Sum(i => i.Amount!.Value), overdue.Count);
    }

    /// <summary>Four weeks from today. Only bills with an amount count as open (see FinancialItem.IsObligation).</summary>
    public static IReadOnlyList<BillWeek> Weeks(IEnumerable<FinancialItem> items, DateTime today)
    {
        today = today.Date;
        var open = items.Where(i => OpenBill(i) && i.DueDate!.Value.Date >= today).ToList();
        string[] labels = ["This week", "Next week", "In 2 weeks", "In 3 weeks"];

        return labels.Select((label, w) =>
        {
            var inWeek = open.Where(i => (i.DueDate!.Value.Date - today).Days / 7 == w).ToList();
            return new BillWeek(label, inWeek.Sum(i => i.Amount!.Value), inWeek.Count);
        }).ToList();
    }

    public static MoneyFlow Flow(CashFlowPlan plan)
    {
        var inParts = new List<FlowPart>();
        if (plan.StartingBalance is { } balance && balance > 0) inParts.Add(new("Balance", balance, "in"));
        if (plan.IncomeTotal > 0) inParts.Add(new("Income", plan.IncomeTotal, "in"));

        var outParts = new List<FlowPart>();
        if (plan.ObligationsTotal > 0) outParts.Add(new("Bills", plan.ObligationsTotal, "bills"));
        if (plan.RecurringTotal > 0) outParts.Add(new("Recurring", plan.RecurringTotal, "recurring"));
        if (plan.EverydayTotal > 0) outParts.Add(new("Everyday", plan.EverydayTotal, "everyday"));
        if (plan.PlannedTotal > 0) outParts.Add(new("Debts & savings", plan.PlannedTotal, "planned"));

        return new(plan.PeriodLabel, inParts, outParts, inParts.Sum(p => p.Amount), outParts.Sum(p => p.Amount), plan.ProjectedRemainder);
    }

    /// <summary>The single most useful thing to do now: what is late, then what needs checking, then the plan, then what is next.</summary>
    public static NextAction Next(IReadOnlyList<Reminder> reminders, CashFlowPlan? plan, bool hasItems)
    {
        Reminder? First(ReminderKind kind) => reminders.FirstOrDefault(r => r.Kind == kind);

        if (First(ReminderKind.Overdue) is { } late)
        {
            return new(NextActionKind.OpenItem, $"Check the overdue bill from {late.Title}", late.Detail, "Open the bill", late.ItemId);
        }

        if (First(ReminderKind.DueToday) is { } now)
        {
            return new(NextActionKind.OpenItem, $"{now.Title} is due today", now.Detail, "Open the bill", now.ItemId);
        }

        if (plan?.Shortfall == true)
        {
            return new(NextActionKind.OpenPayday, "Plan for a tight month", "Your bills and usual costs are more than your money until payday.", "Open the payday plan");
        }

        if (First(ReminderKind.NeedsReview) is { } check)
        {
            return new(NextActionKind.OpenItem, $"Check what Omabilo read from {check.Title}", check.Detail.Replace("Check this: ", ""), "Review it", check.ItemId);
        }

        if (plan != null && plan.StartingBalance == null)
        {
            return new(NextActionKind.OpenPayday, "Add your current balance", "Omabilo can't see your bank. With your balance it can estimate what's left until payday.", "Add balance");
        }

        if (reminders.FirstOrDefault(r => r.Kind is ReminderKind.DueSoon or ReminderKind.DebtPayment) is { } soon)
        {
            return soon.DebtId != null
                ? new(NextActionKind.OpenDebt, $"{soon.Title} payment is coming up", soon.Detail, "Open debts")
                : new(NextActionKind.OpenItem, $"{soon.Title} is due soon", soon.Detail, "Open the bill", soon.ItemId);
        }

        return hasItems
            ? new(NextActionKind.OpenInbox, "Nothing needs you right now", $"No bill is overdue or due in the next {Attention.SoonDays} days. New bills appear here when they arrive.", "Open My inbox")
            : new(NextActionKind.AddBill, "Add your first bill", "Upload a PDF or photo, or paste the text. Omabilo reads it and reminds you before it's due.", "Add a bill");
    }

    /// <summary>Home's one "Today" list: what is late or due today, what Omabilo read and isn't sure about,
    /// records missing details, documents it couldn't read, then a missing balance. Each record shows once.</summary>
    public static IReadOnlyList<TodayItem> TodayList(IReadOnlyList<Reminder> reminders, IEnumerable<FinancialItem> items, CashFlowPlan? plan)
    {
        var list = new List<TodayItem>();
        var seen = new HashSet<string>();
        var open = items.Where(i => i.UserState is UserState.None && i.PossibleDuplicateOf == null).ToList();
        var byId = open.ToDictionary(i => i.Id);

        TodayItem MissingRow(FinancialItem i) => new(TodayKind.Missing, $"Add details to {NameOf(i)}", "Missing: " + string.Join(", ", i.Missing.Take(2)), "Add", i.Id);

        void FromReminders(ReminderKind kind, TodayKind todayKind, string action)
        {
            foreach (var r in reminders.Where(r => r.Kind == kind && r.ItemId != null && seen.Add(r.ItemId)))
            {
                var title = kind switch
                {
                    ReminderKind.Overdue => $"Overdue bill from {r.Title}",
                    ReminderKind.DueToday => $"{r.Title} is due today",
                    _ => $"Check what Omabilo read from {r.Title}",
                };
                list.Add(kind == ReminderKind.NeedsReview && byId.TryGetValue(r.ItemId, out var item) && item.Missing.Count > 0
                    ? MissingRow(item)
                    : new(todayKind, title, r.Detail.Replace("Check this: ", ""), action, r.ItemId));
            }
        }

        FromReminders(ReminderKind.Overdue, TodayKind.Overdue, "Open");
        FromReminders(ReminderKind.DueToday, TodayKind.DueToday, "Open");
        FromReminders(ReminderKind.NeedsReview, TodayKind.Review, "Check");

        foreach (var i in open.Where(i => i.Missing.Count > 0 && seen.Add(i.Id)))
        {
            list.Add(MissingRow(i));
        }

        foreach (var i in open.Where(i => i.Attachments.Any(a => !a.WasRead && a.Status != AttachmentStatus.NotSupported) && seen.Add(i.Id)))
        {
            list.Add(new(TodayKind.Unread, $"A document from {NameOf(i)} couldn't be read", "Open it and check the details yourself.", "Check", i.Id));
        }

        if (plan != null && plan.StartingBalance == null)
        {
            list.Add(new(TodayKind.AddBalance, "Add your current balance", "Then Omabilo can estimate what's left until payday.", "Add"));
        }

        return list;
    }

    /// <summary>Open bills with an amount, due today or later, soonest first.</summary>
    public static IReadOnlyList<FinancialItem> UpcomingBills(IEnumerable<FinancialItem> items, DateTime today, int take) =>
        items.Where(i => OpenBill(i) && i.DueDate!.Value.Date >= today.Date).OrderBy(i => i.DueDate).Take(take).ToList();

    /// <summary>The latest receipts, refunds and income with an amount, newest first.</summary>
    public static IReadOnlyList<FinancialItem> RecentActivity(IEnumerable<FinancialItem> items, int take) =>
        items.Where(i => i.Amount != null && i.PossibleDuplicateOf == null && i.UserState != UserState.NotApplicable && ActivityDate(i) != null
                && i.Kind is FinKind.Receipt or FinKind.Refund or FinKind.Salary or FinKind.Payslip or FinKind.PaymentConfirmation)
            .OrderByDescending(ActivityDate).Take(take).ToList();

    public static DateTime? ActivityDate(FinancialItem i) => i.EventDate ?? i.InvoiceDate;

    /// <summary>True when the record is money coming in rather than going out.</summary>
    public static bool IsMoneyIn(FinancialItem i) => i.Kind is FinKind.Refund or FinKind.Salary or FinKind.Payslip;

    public static string NameOf(FinancialItem i) => string.IsNullOrWhiteSpace(i.Counterparty) ? (string.IsNullOrWhiteSpace(i.Title) ? "an unnamed sender" : i.Title) : i.Counterparty!;

    public static PlansAtAGlance Plans(MyPlansSnapshot plans) =>
        new(plans.TotalDebt, plans.Debts.Count, plans.MonthlyMinimums, plans.TotalSaved, plans.Goals.Sum(g => g.Goal.Target), plans.Goals.Count);

    private static bool OpenBill(FinancialItem i) => i.IsObligation && i.DueDate != null && ItemRules.CountsAsOpenObligation(i);

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
