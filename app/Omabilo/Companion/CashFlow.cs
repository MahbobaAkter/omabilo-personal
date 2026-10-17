// Cash flow until the next payday, and next-step suggestions. All arithmetic happens here, in
// tested code; no model ever computes a number. Each line says where its amount came from, and
// anything left out is listed with the reason, so nothing is counted twice or silently.

public enum AmountBasis { UserEntered, FromEmail, Estimate, Calculated }

public sealed record CashLine(string Label, decimal Amount, AmountBasis Basis, DateTime? Date, string? ItemId, string Note);

public sealed record LeftOut(string Label, string Reason, string? ItemId);

public sealed record CashFlowPlan(
    DateTime Start,
    DateTime End,
    string PeriodLabel,
    decimal? StartingBalance,
    string BalanceNote,
    List<CashLine> Income,
    List<CashLine> Obligations,
    List<CashLine> Recurring,
    List<CashLine> Everyday,
    List<LeftOut> NotCounted,
    decimal IncomeTotal,
    decimal ObligationsTotal,
    decimal RecurringTotal,
    decimal EverydayTotal,
    decimal? ProjectedRemainder,
    string RemainderExplanation)
{
    public bool Shortfall => ProjectedRemainder is < 0;
}

public sealed record Suggestion(string Title, string Why, string Tone, IReadOnlyList<string> ItemIds);

public static class CashFlowPlanner
{
    public static CashFlowPlan Plan(IReadOnlyList<FinancialItem> items, CompanionSettings settings, IncomeOutlook income, DateTime today)
    {
        today = today.Date;

        // Until the day before payday: the salary is never counted before it arrives
        var end = income.NextPayday is { } payday && payday > today ? payday.AddDays(-1) : today.AddDays(29);
        var label = income.NextPayday is { } p && p > today
            ? $"Until payday ({InvoiceExplainer.Day(p)}, {(income.PaydayEvidence == IncomeEvidence.Estimated ? "estimated" : income.PaydayEvidence == IncomeEvidence.UserEntered ? "your date" : "from your payslip")})"
            : "Next 30 days (payday not known)";

        var notCounted = new List<LeftOut>();
        var incomeLines = new List<CashLine>();
        var balanceAsOf = settings.BalanceAsOfUtc?.Date ?? DateTime.MinValue;

        // Income dated in the period and after the balance was entered; estimates are shown, not counted
        foreach (var i in items.Where(i => i.IsIncome && i.Net is > 0 && i.EventDate is { } d && d.Date >= today && d.Date <= end && i.UserState != UserState.NotApplicable))
        {
            if (i.EventDate!.Value.Date <= balanceAsOf)
            {
                notCounted.Add(new(i.Title, "Already part of the balance you entered.", i.Id));
                continue;
            }

            // A salary on or near payday is the next salary itself: it arrives after this period
            if (income.NextPayday is { } np && Math.Abs((i.EventDate!.Value.Date - np).TotalDays) <= 3)
            {
                continue;
            }

            incomeLines.Add(new CashLine(i.Title, i.Net!.Value, AmountBasis.FromEmail, i.EventDate, i.Id,
                i.IncomeEvidence == IncomeEvidence.ReportedReceived ? "The sender says it was paid." : "Expected according to the message, not yet confirmed."));
        }

        if (income.NextPayday is { } next && next > today && income.ExpectedNet is { } net)
        {
            notCounted.Add(new($"Next salary, about {InvoiceExplainer.Money(net)}", $"Arrives on payday ({InvoiceExplainer.Day(next)}), after this period.", null));
        }

        // Bills: open, in euros, readable and not doubtful
        var obligationLines = new List<CashLine>();

        foreach (var i in items.Where(i => i.IsObligation && i.DueDate!.Value.Date <= end))
        {
            if (ItemRules.CountsAsOpenObligation(i))
            {
                var overdue = i.DueDate!.Value.Date < today;
                obligationLines.Add(new CashLine(i.Title, i.Amount!.Value, AmountBasis.FromEmail, i.DueDate, i.Id,
                    overdue ? "Overdue: counted, because nothing says it's paid." : "Due in this period."));
                continue;
            }

            var reason = i.UserState switch
            {
                UserState.ReportedPaid => "You reported it paid.",
                UserState.ConfirmedPaid => "Payment confirmed by a message.",
                UserState.Disputed => "You disputed it.",
                UserState.NotApplicable => "You marked it as not applicable.",
                _ when i.Suspicious => "Has warning signs. Verify it first; it isn't counted until you mark it reviewed.",
                _ when i.PossibleDuplicateOf != null => "May duplicate another bill, which is counted instead.",
                _ when i.Currency != "EUR" => $"In {i.Currency}, not euros.",
                _ => "Not counted.",
            };

            if (i.UserState is not (UserState.ReportedPaid or UserState.ConfirmedPaid or UserState.NotApplicable))
            {
                notCounted.Add(new(i.Title, reason, i.Id));
            }
        }

        foreach (var i in items.Where(i => i.Kind is FinKind.Invoice or FinKind.PaymentReminder or FinKind.DebtCollection && !i.IsObligation
            && i.UserState is UserState.None or UserState.Reviewed))
        {
            notCounted.Add(new(i.Title, i.Amount == null ? "The amount isn't clear." : "The due date isn't known.", i.Id));
        }

        // Recurring costs expected in the period, unless a real bill from the same biller already covers it
        var recurringLines = new List<CashLine>();

        foreach (var r in ItemRules.Recurring(items, today).Where(r => r.NextExpected <= end))
        {
            var covered = items.Any(i => i.IsObligation && InvoiceKeys.NormalizeProvider(i.Counterparty) == InvoiceKeys.NormalizeProvider(r.Counterparty)
                && Math.Abs((i.DueDate!.Value - r.NextExpected).TotalDays) <= 10);

            if (covered)
            {
                notCounted.Add(new($"{r.Counterparty} (usual monthly cost)", "This month's bill is already counted above.", null));
                continue;
            }

            recurringLines.Add(new CashLine($"{r.Counterparty} (usual monthly cost)", r.LastAmount, AmountBasis.Estimate, r.NextExpected, null, r.Basis));
        }

        var everyday = new List<CashLine>();
        var days = (int)(end - today).TotalDays + 1;

        if (settings.EverydaySpendPerDay is { } perDay and > 0)
        {
            everyday.Add(new CashLine($"Everyday spending ({InvoiceExplainer.Money(perDay)} a day × {days} days)", Math.Round(perDay * days, 2), AmountBasis.Estimate, null, null,
                "Your own estimate of daily spending."));
        }
        else
        {
            notCounted.Add(new("Everyday spending (food, transport…)", "Not entered. Add a daily estimate to include it.", null));
        }

        var incomeTotal = incomeLines.Sum(l => l.Amount);
        var obligationsTotal = obligationLines.Sum(l => l.Amount);
        var recurringTotal = recurringLines.Sum(l => l.Amount);
        var everydayTotal = everyday.Sum(l => l.Amount);

        decimal? remainder = null;
        string explanation;
        string balanceNote;

        if (settings.Balance is { } balance)
        {
            remainder = balance + incomeTotal - obligationsTotal - recurringTotal - everydayTotal;
            balanceNote = settings.BalanceAsOfUtc is { } asOf ? $"You entered this on {InvoiceExplainer.Day(asOf)}." : "You entered this.";
            explanation = "Your balance + income in this period − bills − usual monthly costs − everyday spending.";

            if (settings.BalanceAsOfUtc is { } stamp && (today - stamp.Date).TotalDays > 7)
            {
                balanceNote += " It's over a week old, so update it for a better estimate.";
            }
        }
        else
        {
            balanceNote = "Not entered. Omabilo can't see your bank account.";
            explanation = "Enter your current balance to see what's left. Without it Omabilo doesn't guess your real money.";
        }

        return new CashFlowPlan(today, end, label, settings.Balance, balanceNote, incomeLines, obligationLines, recurringLines, everyday, notCounted,
            incomeTotal, obligationsTotal, recurringTotal, everydayTotal, remainder, explanation);
    }

    public static List<Suggestion> Suggest(IReadOnlyList<FinancialItem> items, CashFlowPlan plan, IncomeOutlook income, CompanionSettings settings, DateTime today)
    {
        today = today.Date;
        var list = new List<Suggestion>();
        var open = items.Where(i => i.IsObligation && i.UserState is UserState.None or UserState.Reviewed).ToList();

        foreach (var i in open.Where(i => i.Suspicious && i.UserState == UserState.None))
        {
            list.Add(new($"Verify “{i.Title}” before paying", $"{i.Flags.FirstOrDefault() ?? "It has warning signs."} Contact the company using details you already trust.", "warning", [i.Id]));
        }

        foreach (var i in open.Where(i => i.Kind == FinKind.DebtCollection && !i.Suspicious))
        {
            list.Add(new($"Check the collection notice from {i.Counterparty ?? "an unnamed company"}",
                "Compare it with the original bill. If it's right but you can't pay, contact the agency about a payment plan. If it's wrong, dispute it in writing.", "warning", [i.Id]));
        }

        foreach (var i in open.Where(i => !i.Suspicious && i.DueDate!.Value.Date < today))
        {
            var days = (int)(today - i.DueDate!.Value.Date).TotalDays;
            list.Add(new($"Review the overdue bill from {i.Counterparty ?? "an unnamed company"}",
                $"It was due {days} day{(days == 1 ? "" : "s")} ago and no payment is recorded. If you've paid it, mark it paid. If not, pay it or contact {i.Counterparty ?? "the company"} to agree a new date.", "error", [i.Id]));
        }

        foreach (var i in open.Where(i => !i.Suspicious && i.DueDate!.Value.Date >= today && i.DueDate.Value.Date <= today.AddDays(3)))
        {
            list.Add(new($"{i.Title} is due {InvoiceExplainer.Relative(i.DueDate!.Value, today)}",
                $"{InvoiceExplainer.Money(i.Amount!.Value)}{(i.Reference != null ? $" with reference {i.Reference}" : "")}. Check the details, then pay it in your bank.", "warning", [i.Id]));
        }

        if (plan.Shortfall)
        {
            list.Add(new("Your bills are more than your money until payday",
                $"The plan comes to {InvoiceExplainer.Money(plan.ProjectedRemainder!.Value)}. Contact the billers early to ask for a later due date or a payment plan, and look at spending you can pause.", "error",
                plan.Obligations.Select(o => o.ItemId).OfType<string>().ToList()));
        }

        foreach (var r in ItemRules.Recurring(items, today).Where(r => r.Category == Categories.Subscriptions))
        {
            list.Add(new($"Do you still use {r.Counterparty}?", $"It has charged about {InvoiceExplainer.Money(r.LastAmount)} a month {r.Occurrences} times. Cancelling unused subscriptions frees money every month.", "info", []));
        }

        if (settings.Balance == null && items.Count > 0)
        {
            list.Add(new("Add your current balance", "Omabilo can't see your bank. With your balance it can estimate what's left until payday.", "info", []));
        }

        if (income.History.Count > 0 && income.NextPayday == null)
        {
            list.Add(new("Confirm your payday", income.PaydayBasis + " You can ask your employer or check your contract.", "info", income.History.Take(3).Select(i => i.Id).ToList()));
        }

        var review = items.Where(i => ItemRules.StatusOf(i, today).NeedsReview).ToList();
        if (review.Count > 0)
        {
            list.Add(new($"Review {review.Count} item{(review.Count == 1 ? "" : "s")} Omabilo isn't sure about", "Confirm or correct them so your plan is based on the right numbers.", "info", review.Select(i => i.Id).ToList()));
        }

        return list;
    }
}
