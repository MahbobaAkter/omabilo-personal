// Status, duplicate and recurrence rules for financial items. Plain C# and deterministic, so every
// screen and the tests agree. A status always comes with the evidence behind it.

public sealed record ItemStatus(DisplayStatus Status, string Why, bool NeedsReview, string? ReviewReason);

public sealed record RecurringPattern(string Counterparty, string Category, FinKind Kind, int Occurrences, decimal LastAmount, DateTime LastDate, DateTime NextExpected, string Basis);

public sealed record IncomeOutlook(
    DateTime? NextPayday,
    string PaydayBasis,
    IncomeEvidence PaydayEvidence,
    decimal? ExpectedNet,
    string NetBasis,
    IncomeEvidence NetEvidence,
    IReadOnlyList<FinancialItem> History);

public static class ItemRules
{
    public const double ReviewConfidence = 0.7;

    /// <summary>The inbox views, as (key, label). "recurring" lists usual monthly costs, not items.</summary>
    public static readonly (string Key, string Label)[] Views =
    [
        ("attention", "Needs attention"), ("review", "Needs review"), ("bills", "Bills"), ("tax", "Tax"), ("debt", "Debt"),
        ("income", "Income"), ("spending", "Spending"), ("recurring", "Recurring"), ("all", "All"),
    ];

    /// <summary>A tax decision or anything filed under Taxes.</summary>
    public static bool IsTax(FinancialItem i) => i.Kind == FinKind.TaxNotice || i.Category == Categories.Taxes;

    /// <summary>Debt collection, a loan or credit instalment, or anything filed under debt repayments.</summary>
    public static bool IsDebt(FinancialItem i) => i.Kind is FinKind.DebtCollection or FinKind.LoanInstalment || i.Category == Categories.Debt;

    /// <summary>Whether an item shows in an inbox view. An unknown view shows everything, so a filter never hides records.</summary>
    public static bool InView(FinancialItem i, ItemStatus status, string view) => view switch
    {
        "attention" => status.NeedsReview || status.Status is DisplayStatus.Overdue or DisplayStatus.DueSoon or DisplayStatus.NeedsReview,
        "review" => status.NeedsReview || status.Status == DisplayStatus.NeedsReview,
        "bills" => i.IsBill,
        "tax" => IsTax(i),
        "debt" => IsDebt(i),
        "income" => i.IsIncome,
        "spending" => i.IsExpense,
        "recurring" => false,
        _ => true,
    };

    public static ItemStatus StatusOf(FinancialItem i, DateTime today)
    {
        today = today.Date;
        var (needsReview, reason) = ReviewNeed(i);

        if (i.UserState == UserState.Disputed) return new(DisplayStatus.Disputed, "You marked this as disputed.", false, null);
        if (i.UserState == UserState.NotApplicable) return new(DisplayStatus.NotApplicable, "You marked this as cancelled or no longer applicable.", false, null);

        if (i.IsIncome)
        {
            return i.IncomeEvidence == IncomeEvidence.ReportedReceived
                ? new(DisplayStatus.IncomeReported, "The sender says this salary was paid. Check your bank to confirm it arrived.", needsReview, reason)
                : new(DisplayStatus.ExpectedIncome, i.EventDate is { } d
                    ? $"Expected on {InvoiceExplainer.Day(d)} according to the {(i.Kind == FinKind.Payslip ? "payslip" : "message")}. Not confirmed as received."
                    : "Salary information without a payment date.", needsReview, reason);
        }

        if (i.IsExpense)
        {
            return new(DisplayStatus.Recorded, i.Kind == FinKind.Refund ? "A refund the sender says is on its way." : "Spending that has already happened.", needsReview, reason);
        }

        if (i.UserState == UserState.ConfirmedPaid)
        {
            return new(DisplayStatus.ConfirmedPaid, i.Evidence.LastOrDefault(e => e.Contains("confirm", StringComparison.OrdinalIgnoreCase) || e.Contains("received", StringComparison.OrdinalIgnoreCase))
                ?? "A message confirms the payment.", false, null);
        }

        if (i.UserState == UserState.ReportedPaid)
        {
            return new(DisplayStatus.ReportedPaid, "You reported paying this. It isn't confirmed by your bank.", false, null);
        }

        if (!i.IsObligation)
        {
            if (i.Kind is FinKind.Invoice or FinKind.PaymentReminder or FinKind.DebtCollection or FinKind.TaxNotice)
            {
                return new(DisplayStatus.NeedsReview, i.Amount == null ? "The amount to pay isn't clear." : "There's no due date.", true, reason ?? "Details are missing");
            }

            return new(DisplayStatus.Information, "Financial information with nothing to pay.", needsReview, reason);
        }

        // A bill with warning signs is never presented as simply "due": verifying it comes first
        if (i.Suspicious && i.UserState == UserState.None)
        {
            return new(DisplayStatus.NeedsReview, $"It has warning signs. Verify it with the company, using contact details you already trust, before paying. The message gives {InvoiceExplainer.Day(i.DueDate!.Value)} as the due date.", true, reason);
        }

        var due = i.DueDate!.Value.Date;
        var days = (int)(due - today).TotalDays;

        if (days < 0)
        {
            return new(DisplayStatus.Overdue,
                $"The due date ({InvoiceExplainer.Day(due)}) passed {-days} day{(days == -1 ? "" : "s")} ago and no payment is reported or confirmed. Omabilo can't see your bank, so this doesn't prove it's unpaid.",
                needsReview, reason);
        }

        return days <= 7
            ? new(DisplayStatus.DueSoon, days == 0 ? "Due today." : $"Due in {days} day{(days == 1 ? "" : "s")}.", needsReview, reason)
            : new(DisplayStatus.Upcoming, $"Due on {InvoiceExplainer.Day(due)}.", needsReview, reason);
    }

    private static (bool, string?) ReviewNeed(FinancialItem i)
    {
        if (i.UserState != UserState.None) return (false, null);
        if (i.Suspicious) return (true, "Has warning signs: verify before paying");
        if (i.PossibleDuplicateOf != null) return (true, "May be a duplicate");
        if (i.Flags.Count > 0) return (true, "Has conflicting or unusual details");
        if (i.Confidence < ReviewConfidence) return (true, "Omabilo isn't sure it read this correctly");
        if (i.IsObligation is false && i.Kind is FinKind.Invoice or FinKind.PaymentReminder) return (true, "Details are missing");

        // An uploaded document stays unconfirmed until the person says what Omabilo read is right
        if (i.Sources.Count > 0 && BillUpload.IsUploadId(i.Sources[0].GmailId)) return (true, "You uploaded this and haven't checked what Omabilo read yet");
        return (false, null);
    }

    /// <summary>Counted as money still to pay: open, readable, in euros, not doubtful.</summary>
    public static bool CountsAsOpenObligation(FinancialItem i) =>
        i.IsObligation
        && i.UserState is UserState.None or UserState.Reviewed
        && !i.Suspicious
        && i.PossibleDuplicateOf == null
        && i.Currency == "EUR";

    // ── Same obligation, possible duplicates ────────────────────────────────

    private static bool IsBillKind(FinKind k) => k is FinKind.Invoice or FinKind.PaymentReminder or FinKind.DebtCollection
        or FinKind.Subscription or FinKind.Insurance or FinKind.LoanInstalment or FinKind.TaxNotice;

    private static string Norm(string? s) => InvoiceKeys.NormalizeProvider(s);

    private static bool SameParty(FinancialItem a, FinancialItem b)
    {
        var x = Norm(a.Counterparty);
        var y = Norm(b.Counterparty);

        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;

        // "Revontuli Energy" and "Revontuli Energy Services" are the same biller (company suffixes are already gone)
        return Math.Min(x.Length, y.Length) >= 6 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal));
    }

    /// <summary>The existing bill this message is about, when it is the same one: the same invoice
    /// number, or the same reference with a due date close by (recurring Finnish bills reuse one
    /// reference, so the reference alone doesn't prove it), or a reminder whose original due date is it.</summary>
    public static FinancialItem? FindSameObligation(IEnumerable<FinancialItem> existing, FinancialItem incoming)
    {
        if (!IsBillKind(incoming.Kind)) return null;

        foreach (var e in existing.Where(e => IsBillKind(e.Kind) && e.Id != incoming.Id && SameParty(e, incoming)))
        {
            if (incoming.InvoiceNumber != null && InvoiceKeys.NormalizeToken(incoming.InvoiceNumber) == InvoiceKeys.NormalizeToken(e.InvoiceNumber))
            {
                return e;
            }

            var sameRef = incoming.Reference != null && InvoiceKeys.NormalizeToken(incoming.Reference) == InvoiceKeys.NormalizeToken(e.Reference);
            var dueClose = incoming.DueDate is { } a && e.DueDate is { } b && Math.Abs((a - b).TotalDays) <= 20;
            var reminderOf = incoming.OriginalDueDate is { } o && e.DueDate is { } d && o.Date == d.Date;

            if (sameRef && (dueClose || reminderOf))
            {
                return e;
            }

            if (reminderOf && incoming.InvoiceNumber == null && incoming.Reference == null && e.Amount is { } ea && incoming.Amount is { } ia && ia >= ea && ia - ea <= 25)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>Folds a later message about the same bill into it, keeping the person's own edits.</summary>
    public static FinancialItem MergeInto(FinancialItem existing, FinancialItem incoming, DateTime now)
    {
        var edited = existing.EditedFields.ToHashSet();
        var isReminder = incoming.Kind is FinKind.PaymentReminder or FinKind.DebtCollection;
        var evidence = existing.Evidence.Concat(incoming.Evidence).Distinct().ToList();
        var flags = existing.Flags.Concat(incoming.Flags).Distinct().ToList();

        if (isReminder && existing.UserState == UserState.ReportedPaid)
        {
            flags.Add("A reminder arrived after you reported paying. The payment and the reminder may have crossed, so check your bank.");
        }

        var role = isReminder ? "reminder" : "follow-up";
        var source = incoming.Sources.FirstOrDefault() is { } s ? s with { Role = role } : null;

        return existing with
        {
            Kind = isReminder ? incoming.Kind : existing.Kind,
            Title = isReminder ? incoming.Title : existing.Title,
            Amount = edited.Contains(nameof(FinancialItem.Amount)) ? existing.Amount : incoming.Amount ?? existing.Amount,
            DueDate = edited.Contains(nameof(FinancialItem.DueDate)) ? existing.DueDate : incoming.DueDate ?? existing.DueDate,
            OriginalDueDate = existing.OriginalDueDate ?? incoming.OriginalDueDate ?? (isReminder ? existing.DueDate : null),
            Reference = existing.Reference ?? incoming.Reference,
            InvoiceNumber = existing.InvoiceNumber ?? incoming.InvoiceNumber,
            PaymentInstructions = existing.PaymentInstructions ?? incoming.PaymentInstructions,
            Suspicious = existing.Suspicious || incoming.Suspicious,
            Confidence = Math.Max(existing.Confidence, incoming.Confidence),
            Flags = flags.Distinct().ToList(),
            Evidence = evidence,
            Missing = incoming.Missing.Where(m => existing.Missing.Contains(m)).ToList(),
            Sources = source == null || existing.Sources.Any(x => x.GmailId == source.GmailId) ? existing.Sources : [.. existing.Sources, source],
            Iban = existing.Iban ?? incoming.Iban,
            Vat = existing.Vat ?? incoming.Vat,
            Attachments = MergeAttachments(existing.Attachments, incoming.Attachments),
            FieldSources = MergeProvenance(existing.FieldSources, incoming.FieldSources),
            UpdatedUtc = now,
        };
    }

    /// <summary>Both items' attachments, the newer outcome winning for the same file of the same email.</summary>
    public static List<AttachmentRecord> MergeAttachments(List<AttachmentRecord> existing, List<AttachmentRecord> incoming) =>
        existing.Where(e => !incoming.Any(i => i.GmailId == e.GmailId && i.FileName == e.FileName)).Concat(incoming).ToList();

    /// <summary>Per field, the newer source when the newer email states it (a reminder's new amount), else the earlier one.</summary>
    private static List<FieldProvenance> MergeProvenance(List<FieldProvenance> existing, List<FieldProvenance> incoming) =>
        existing.Select(e => e.Field).Union(incoming.Select(i => i.Field))
            .Select(f => incoming.FirstOrDefault(i => i.Field == f) ?? existing.First(e => e.Field == f))
            .ToList();

    /// <summary>An item this one may duplicate without proof: flagged for the person, never merged.</summary>
    public static FinancialItem? FindProbableDuplicate(IEnumerable<FinancialItem> existing, FinancialItem incoming)
    {
        foreach (var e in existing.Where(e => e.Id != incoming.Id && e.Amount != null && e.Amount == incoming.Amount))
        {
            if (IsBillKind(incoming.Kind) && IsBillKind(e.Kind) && SameParty(e, incoming)
                && incoming.DueDate is { } a && e.DueDate is { } b && Math.Abs((a - b).TotalDays) <= 3)
            {
                return e;
            }

            if (incoming.IsExpense && e.IsExpense && incoming.Kind == e.Kind
                && incoming.EventDate is { } x && e.EventDate is { } y && Math.Abs((x - y).TotalDays) <= 2
                && (SameParty(e, incoming) || incoming.Flags.Concat(e.Flags).Any(f => f.Contains("card payment notice"))))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>Two receipts with one order number are the same purchase (order and shipping emails).</summary>
    public static FinancialItem? FindSamePurchase(IEnumerable<FinancialItem> existing, FinancialItem incoming) =>
        incoming.IsExpense && incoming.OrderRef != null
            ? existing.FirstOrDefault(e => e.IsExpense && e.Kind == incoming.Kind && e.OrderRef != null
                && InvoiceKeys.NormalizeToken(e.OrderRef) == InvoiceKeys.NormalizeToken(incoming.OrderRef) && SameParty(e, incoming))
            : null;

    /// <summary>The open bill a payment confirmation is about: same biller and the same invoice
    /// number or reference, or failing those the same amount due within 45 days.</summary>
    public static FinancialItem? MatchPaymentConfirmation(IEnumerable<FinancialItem> existing, FinancialItem confirmation)
    {
        var open = existing.Where(e => IsBillKind(e.Kind) && e.UserState is UserState.None or UserState.Reviewed or UserState.ReportedPaid && SameParty(e, confirmation)).ToList();

        return open.FirstOrDefault(e => confirmation.InvoiceNumber != null && InvoiceKeys.NormalizeToken(e.InvoiceNumber) == InvoiceKeys.NormalizeToken(confirmation.InvoiceNumber))
            ?? open.FirstOrDefault(e => confirmation.Reference != null && InvoiceKeys.NormalizeToken(e.Reference) == InvoiceKeys.NormalizeToken(confirmation.Reference))
            ?? open.FirstOrDefault(e => confirmation.Amount != null && e.Amount == confirmation.Amount && e.DueDate is { } d
                && confirmation.EventDate is { } c && Math.Abs((d - c).TotalDays) <= 45);
    }

    // ── Recurring costs ─────────────────────────────────────────────────────

    /// <summary>A cost seen at least twice about a month apart. The next one is an estimate.</summary>
    public static List<RecurringPattern> Recurring(IEnumerable<FinancialItem> items, DateTime today)
    {
        var result = new List<RecurringPattern>();
        var candidates = items.Where(i => (IsBillKind(i.Kind) || i.Kind == FinKind.Receipt) && i.Kind != FinKind.DebtCollection && i.Kind != FinKind.TaxNotice
            && i.Amount is > 0 && (i.DueDate ?? i.EventDate) != null && i.UserState != UserState.NotApplicable && i.PossibleDuplicateOf == null);

        foreach (var group in candidates.GroupBy(i => Norm(i.Counterparty)).Where(g => g.Key.Length > 0))
        {
            var dated = group.Select(i => (Item: i, Date: (i.DueDate ?? i.EventDate)!.Value.Date)).OrderBy(x => x.Date).ToList();

            if (dated.Count < 2) continue;

            var gaps = dated.Zip(dated.Skip(1), (a, b) => (b.Date - a.Date).TotalDays).ToList();

            if (!gaps.All(g => g is >= 25 and <= 35)) continue;

            var last = dated[^1];
            var next = last.Date.AddMonths(1);
            while (next < today.Date) next = next.AddMonths(1);

            result.Add(new RecurringPattern(last.Item.Counterparty ?? group.Key, last.Item.Category, last.Item.Kind, dated.Count, last.Item.Amount!.Value, last.Date, next,
                $"Seen {dated.Count} times about a month apart; the next one is an estimate based on the last amount."));
        }

        return result;
    }

    // ── Income ──────────────────────────────────────────────────────────────

    public static IncomeOutlook Income(IEnumerable<FinancialItem> items, CompanionSettings settings, DateTime today)
    {
        today = today.Date;
        var history = items.Where(i => i.IsIncome && i.UserState != UserState.NotApplicable).OrderByDescending(i => i.EventDate ?? i.CreatedUtc).ToList();
        var dated = history.Where(i => i.EventDate != null).Select(i => i.EventDate!.Value.Date).Distinct().OrderBy(d => d).ToList();

        DateTime? payday = null;
        var paydayBasis = "Not known yet. Add your payday, or it will be estimated once two payslips show a pattern.";
        var paydayEvidence = IncomeEvidence.None;

        if (settings.PaydayDay is { } day and >= 1 and <= 31)
        {
            payday = NextDayOfMonth(today, day);
            paydayBasis = $"You entered the {Ordinal(day)} of each month.{WeekendNote(today, day)}";
            paydayEvidence = IncomeEvidence.UserEntered;
        }
        else if (dated.FirstOrDefault(d => d >= today) is { } scheduled && scheduled != default)
        {
            payday = scheduled;
            paydayBasis = $"The payment date on your latest payslip or salary message ({InvoiceExplainer.Day(scheduled)}).";
            paydayEvidence = IncomeEvidence.Scheduled;
        }
        else if (dated.Count >= 2)
        {
            var recent = dated.TakeLast(3).ToList();
            var days = recent.Select(d => d.Day).ToList();

            if (days.Max() - days.Min() <= 3 && recent.Zip(recent.Skip(1), (a, b) => (b - a).TotalDays).All(g => g is >= 25 and <= 35))
            {
                var typical = (int)Math.Round(days.Average());
                payday = NextDayOfMonth(today, typical);
                paydayBasis = $"Estimated: your last {recent.Count} salaries came around the {Ordinal(typical)}.{WeekendNote(today, typical)} Public holidays can also move it.";
                paydayEvidence = IncomeEvidence.Estimated;
            }
            else
            {
                paydayBasis = "Your salary dates don't follow a clear monthly pattern, so Omabilo doesn't guess. Add your payday.";
            }
        }
        else if (dated.Count == 1)
        {
            paydayBasis = "Only one salary date found, which isn't enough to predict the next one. Add your payday.";
        }

        decimal? net = null;
        var netBasis = "Not known yet.";
        var netEvidence = IncomeEvidence.None;

        if (settings.NetSalary is { } userNet)
        {
            net = userNet;
            netBasis = "You entered this.";
            netEvidence = IncomeEvidence.UserEntered;
        }
        else if (history.FirstOrDefault(i => i.Net != null) is { } latest)
        {
            net = latest.Net;
            netBasis = latest.IncomeEvidence == IncomeEvidence.Payslip
                ? $"From your latest payslip ({latest.Counterparty}). Your next salary may differ."
                : $"From your latest salary message ({latest.Counterparty}). Your next salary may differ.";
            netEvidence = IncomeEvidence.Estimated;
        }

        return new IncomeOutlook(payday, paydayBasis, paydayEvidence, net, netBasis, netEvidence, history);
    }

    /// <summary>The next payday on that day of the month. In Finland a payday on a weekend is paid
    /// on the Friday before, so that is the date returned.</summary>
    public static DateTime NextDayOfMonth(DateTime today, int day)
    {
        var candidate = BankingDay(OnDay(today.Year, today.Month, day));

        if (candidate < today)
        {
            var next = today.AddMonths(1);
            candidate = BankingDay(OnDay(next.Year, next.Month, day));
        }

        return candidate;
    }

    private static DateTime OnDay(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Utc);

    public static DateTime BankingDay(DateTime d) => d.DayOfWeek switch
    {
        DayOfWeek.Saturday => d.AddDays(-1),
        DayOfWeek.Sunday => d.AddDays(-2),
        _ => d,
    };

    private static string WeekendNote(DateTime today, int day)
    {
        var next = NextDayOfMonth(today, day);
        return next.Day != Math.Min(day, DateTime.DaysInMonth(next.Year, next.Month)) ? $" This month it falls on a weekend, so it's paid on Friday {InvoiceExplainer.Day(next)}." : "";
    }

    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}
