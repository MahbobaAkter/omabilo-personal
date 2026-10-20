// Cash flow until the next payday, and next-step suggestions. All arithmetic happens here, in
// tested code; no model ever computes a number. Each line says where its amount came from, and
// anything left out is listed with the reason, so nothing is counted twice or silently.

public enum AmountBasis { UserEntered, FromEmail, Estimate, Calculated, Uploaded }

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

    /// <summary>Money the person plans to use before payday: debt payments from their debt list and
    /// the savings they set aside. Already taken off <see cref="ProjectedRemainder"/>.</summary>
    public List<CashLine> Planned { get; init; } = [];

    public decimal PlannedTotal => Planned.Sum(l => l.Amount);
}

public sealed record Suggestion(string Title, string Why, string Tone, IReadOnlyList<string> ItemIds);

public static class CashFlowPlanner
{
    /// <summary>An amount read from a PDF the person uploaded, or from an email.</summary>
    private static AmountBasis BasisOf(FinancialItem i) =>
        i.Sources.Count > 0 && BillUpload.IsUploadId(i.Sources[0].GmailId) ? AmountBasis.Uploaded : AmountBasis.FromEmail;

    public static CashFlowPlan Plan(IReadOnlyList<FinancialItem> items, CompanionSettings settings, IncomeOutlook income, DateTime today,
        IReadOnlyList<DebtRecord>? debts = null)
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

        // Income dated in the period and after the balance was entered; only received income counts
        foreach (var i in items.Where(i => i.IsIncome && i.Net is > 0 && i.EventDate is { } d && d.Date >= today && d.Date <= end && i.UserState != UserState.NotApplicable))
        {
            if (i.EventDate!.Value.Date <= balanceAsOf)
            {
                notCounted.Add(new(i.Title, "Already part of the balance you entered.", i.Id));
                continue;
            }

            // The same pay reported twice (as in Monthly Reports and month by month): the first is counted
            if (i.PossibleDuplicateOf != null)
            {
                notCounted.Add(new(i.Title, "May duplicate another pay, which is counted instead.", i.Id));
                continue;
            }

            // A salary on or near payday is the next salary itself: it arrives after this period
            if (income.NextPayday is { } np && Math.Abs((i.EventDate!.Value.Date - np).TotalDays) <= 3)
            {
                continue;
            }

            // Expected pay (a payslip, a payroll document, a scheduled payment) is shown, never counted
            if (!ItemRules.IncomeReceived(i))
            {
                notCounted.Add(new(i.Title, "Expected, not yet received. Mark it received once it's on your account.", i.Id));
                continue;
            }

            incomeLines.Add(new CashLine(i.Title, i.Net!.Value, BasisOf(i), i.EventDate, i.Id,
                i.UserState is UserState.ReportedPaid or UserState.ConfirmedPaid ? "You marked it received." : "The sender says it was paid."));
        }

        // A salary paid after the balance was entered is money on the account the balance doesn't
        // show yet; a payslip and a "salary paid" email for the same pay are added once
        if (settings.Balance != null && settings.BalanceAsOfUtc != null)
        {
            var since = items.Where(i => i.IsIncome && i.Net is > 0 && i.EventDate is { } d && d.Date > balanceAsOf && d.Date < today
                && i.UserState != UserState.NotApplicable && i.PossibleDuplicateOf == null).ToList();
            var received = since.Where(ItemRules.IncomeReceived).DistinctBy(i => (i.EventDate!.Value.Date, i.Net)).ToList();

            // A payslip whose pay date has passed is still only expected until the pay is confirmed
            foreach (var i in since.Where(i => !ItemRules.IncomeReceived(i) && !received.Any(r => r.EventDate!.Value.Date == i.EventDate!.Value.Date && r.Net == i.Net)))
            {
                notCounted.Add(new(i.Title, $"Expected on {InvoiceExplainer.Day(i.EventDate!.Value)}, not confirmed as received. Mark it received once it's on your account.", i.Id));
            }

            foreach (var i in received)
            {
                incomeLines.Add(new CashLine(i.Title, i.Net!.Value, BasisOf(i), i.EventDate, i.Id,
                    $"Paid on {InvoiceExplainer.Day(i.EventDate!.Value)}, after you entered your balance, so it's added to it."));
            }
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
                obligationLines.Add(new CashLine(i.Title, i.Amount!.Value, BasisOf(i), i.DueDate, i.Id,
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

        // Debt payments from the person's own debt list, unless a bill for the same payment is counted above
        var planned = new List<CashLine>();

        foreach (var d in debts ?? [])
        {
            if (PlanRules.NextPayment(d, today) is not { } due || due > end)
            {
                continue;
            }

            if (d.MinPayment is not > 0)
            {
                notCounted.Add(new($"{d.Name} (debt payment)", "Its monthly payment isn't entered on the Debts screen.", null));
                continue;
            }

            if (d.LastPaymentUtc is { } paid && paid.Date >= due.AddDays(-10))
            {
                notCounted.Add(new($"{d.Name} (debt payment)", $"You recorded a payment on {InvoiceExplainer.Day(paid)}.", null));
                continue;
            }

            var names = new[] { d.Lender, d.Name }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => InvoiceKeys.NormalizeProvider(n)).ToList();
            var bill = obligationLines.FirstOrDefault(o => o.ItemId != null && items.FirstOrDefault(i => i.Id == o.ItemId) is { } i
                && names.Contains(InvoiceKeys.NormalizeProvider(i.Counterparty)) && Math.Abs((i.DueDate!.Value.Date - due).TotalDays) <= 10);

            if (bill != null)
            {
                notCounted.Add(new($"{d.Name} (debt payment)", $"The bill \"{bill.Label}\" above is this payment, so it's counted once.", bill.ItemId));
                continue;
            }

            planned.Add(new CashLine($"{d.Name} (debt payment)", d.MinPayment.Value, AmountBasis.UserEntered, due, null, "The monthly payment and due day you entered on the Debts screen."));
        }

        if (settings.SavingsPerPeriod is { } save and > 0)
        {
            planned.Add(new CashLine("Savings you set aside", save, AmountBasis.UserEntered, null, null, "Your own amount. Money already in your goals isn't part of this."));
        }

        var plannedTotal = planned.Sum(l => l.Amount);
        var incomeTotal = incomeLines.Sum(l => l.Amount);
        var obligationsTotal = obligationLines.Sum(l => l.Amount);
        var recurringTotal = recurringLines.Sum(l => l.Amount);
        var everydayTotal = everyday.Sum(l => l.Amount);

        decimal? remainder = null;
        string explanation;
        string balanceNote;

        if (settings.Balance is { } balance)
        {
            remainder = balance + incomeTotal - obligationsTotal - recurringTotal - everydayTotal - plannedTotal;
            balanceNote = settings.BalanceAsOfUtc is { } asOf ? $"You entered this on {InvoiceExplainer.Day(asOf)}." : "You entered this.";
            explanation = "Your balance + income in this period − bills − usual monthly costs − everyday spending"
                + (planned.Count > 0 ? " − debt payments and savings you planned." : ".");

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
            incomeTotal, obligationsTotal, recurringTotal, everydayTotal, remainder, explanation) { Planned = planned };
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
