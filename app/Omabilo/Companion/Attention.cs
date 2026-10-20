// The Home screen's reminders, built only from what the person saved: their bills and payslips,
// their own debts, and their payday. Plain C# and deterministic, so the screen and the tests agree.
// Nothing here sends anything; a reminder is a line on the screen until the record changes.

public enum ReminderKind { Overdue, DueToday, DueSoon, DebtPayment, NeedsReview, Payday }

/// <summary>One line on Home. ItemId or DebtId says what it opens; Tone is error, warning, info or success.</summary>
public sealed record Reminder(ReminderKind Kind, string Title, string Detail, DateTime? Date, string Tone, string? ItemId = null, string? DebtId = null)
{
    /// <summary>True for what has a deadline or needs a decision; payday is good news, not a task.</summary>
    public bool IsTask => Kind != ReminderKind.Payday;
}

public static class Attention
{
    public const int SoonDays = 7;

    public static IReadOnlyList<Reminder> Build(IReadOnlyList<FinancialItem> items, IncomeOutlook? income, IReadOnlyList<DebtRecord> debts, DateTime today)
    {
        today = today.Date;
        var list = new List<Reminder>();
        var shown = new HashSet<string>();

        // Each item appears once, under its most urgent reason
        foreach (var i in items.Where(i => ItemRules.StatusOf(i, today).Status == DisplayStatus.Overdue).OrderBy(i => i.DueDate))
        {
            var late = (today - i.DueDate!.Value.Date).Days;
            list.Add(new(ReminderKind.Overdue, Name(i), Cap($"{AmountOf(i)}was due {InvoiceExplainer.Day(i.DueDate.Value)}, {late} day{(late == 1 ? "" : "s")} ago"), i.DueDate, "error", i.Id));
            shown.Add(i.Id);
        }

        foreach (var i in items.Where(i => !shown.Contains(i.Id) && OpenBill(i) && i.DueDate!.Value.Date == today))
        {
            list.Add(new(ReminderKind.DueToday, Name(i), Cap($"{AmountOf(i)}due today"), i.DueDate, "warning", i.Id));
            shown.Add(i.Id);
        }

        foreach (var i in items.Where(i => !shown.Contains(i.Id) && OpenBill(i) && i.DueDate!.Value.Date > today && i.DueDate.Value.Date <= today.AddDays(SoonDays)).OrderBy(i => i.DueDate))
        {
            var days = (i.DueDate!.Value.Date - today).Days;
            list.Add(new(ReminderKind.DueSoon, Name(i), Cap($"{AmountOf(i)}due {InvoiceExplainer.Day(i.DueDate.Value)}, in {days} day{(days == 1 ? "" : "s")}"), i.DueDate, "warning", i.Id));
            shown.Add(i.Id);
        }

        foreach (var i in items.Where(i => !shown.Contains(i.Id)))
        {
            if (ItemRules.StatusOf(i, today) is { NeedsReview: true } s)
            {
                list.Add(new(ReminderKind.NeedsReview, Name(i), "Check this: " + (s.ReviewReason ?? "Omabilo isn't sure it read it right."), null, "info", i.Id));
            }
        }

        foreach (var d in debts.Where(d => d.Balance > 0 && d.DueDay is not null))
        {
            var next = NextDueDate(d.DueDay!.Value, today);

            if (next <= today.AddDays(SoonDays))
            {
                var days = (next - today).Days;
                var when = days == 0 ? "due today" : $"due {InvoiceExplainer.Day(next)}, in {days} day{(days == 1 ? "" : "s")}";
                var amount = d.MinPayment is > 0 ? $"{InvoiceExplainer.Money(d.MinPayment.Value)} " : "Payment ";
                list.Add(new(ReminderKind.DebtPayment, d.Name, $"{amount}{when} (from the due day you entered)", next, "warning", DebtId: d.Id));
            }
        }

        if (income?.NextPayday is { } pay && pay.Date >= today && pay.Date <= today.AddDays(SoonDays))
        {
            var net = income.ExpectedNet is { } n ? $"about {InvoiceExplainer.Money(n)} " : "";
            list.Add(new(ReminderKind.Payday, "Payday", Cap($"{net}expected {InvoiceExplainer.Day(pay)}. {income.PaydayBasis}"), pay, "success"));
        }

        // Most urgent first: a debt payment ranks with the bills due on the same day, then by date
        return list.OrderBy(r => r.Kind != ReminderKind.DebtPayment ? r.Kind : r.Date == today ? ReminderKind.DueToday : ReminderKind.DueSoon)
            .ThenBy(r => r.Date ?? DateTime.MaxValue).ToList();
    }

    /// <summary>The next date on or after today with the given day of month, moved to the month's last day when it is shorter.</summary>
    public static DateTime NextDueDate(int dueDay, DateTime today)
    {
        today = today.Date;
        var thisMonth = new DateTime(today.Year, today.Month, Math.Min(dueDay, DateTime.DaysInMonth(today.Year, today.Month)));

        if (thisMonth >= today)
        {
            return thisMonth;
        }

        var nextMonth = today.AddMonths(1);
        return new DateTime(nextMonth.Year, nextMonth.Month, Math.Min(dueDay, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month)));
    }

    private static bool OpenBill(FinancialItem i) => i.IsObligation && i.DueDate != null && ItemRules.CountsAsOpenObligation(i);

    private static string Name(FinancialItem i) => string.IsNullOrWhiteSpace(i.Counterparty) ? i.Title : i.Counterparty!;

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string AmountOf(FinancialItem i) => i.Amount is { } a ? $"{InvoiceExplainer.Money(a)} " : "";
}
