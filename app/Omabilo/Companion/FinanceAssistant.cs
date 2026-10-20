// The Finland Tax & Finance Assistant: answers built only from the person's own records, each with
// the records it used and what is missing. VAT is the VAT printed on invoices, never a tax return
// figure; the tax card is what the person typed, compared with gross pay on payslips Omabilo holds.
// Omabilo isn't connected to Vero (the Finnish Tax Administration) or OmaVero, files nothing and
// sends nothing. Plain C# with no IKON types, so the screen and the tests agree.

public enum AssistantQuestion { ReviewThisMonth, DueBeforePayday, VatThisMonth, ForAccountant }

/// <summary>A record an answer used: an item or a debt, with a short line about it.</summary>
public sealed record AnswerSource(string Label, string Detail, string? ItemId = null, string? DebtId = null, decimal? Amount = null);

/// <param name="Period">The month an answer covers, when it covers one ("September 2026").</param>
public sealed record AssistantAnswer(string Question, string Summary, IReadOnlyList<AnswerSource> Sources, IReadOnlyList<string> Missing, string Basis, string? Period = null);

/// <param name="DateLabel">What Date is: "Invoice dated", or "Due" / "Dated" when no invoice date was read.</param>
public sealed record VatLine(string ItemId, string Title, DateTime Date, decimal Total, decimal Vat, decimal? RatePercent, string DateLabel = "Invoice dated");

public sealed record VatSummary(string PeriodLabel, IReadOnlyList<VatLine> Lines, IReadOnlyList<AnswerSource> WithoutVat)
{
    public decimal VatTotal => Lines.Sum(l => l.Vat);
    public decimal GrossTotal => Lines.Sum(l => l.Total);
}

public sealed record TaxCardStatus(
    int Year,
    decimal? RatePercent,
    decimal? AdditionalRatePercent,
    decimal? IncomeLimit,
    decimal GrossSoFar,
    int Payslips,
    int PayslipsWithoutGross,
    decimal? LeftUnderLimit,
    string Summary,
    IReadOnlyList<AnswerSource> Sources);

public sealed record GlossaryTerm(string Finnish, string English, string Meaning);

public static class FinanceAssistant
{
    public const string Disclaimer =
        "Built from your records in Omabilo. Omabilo isn't connected to Vero or OmaVero, doesn't file anything and isn't tax advice. " +
        "For your own tax situation, check OmaVero (vero.fi) or ask an accountant.";

    public static string QuestionText(AssistantQuestion q) => q switch
    {
        AssistantQuestion.ReviewThisMonth => "What do I need to review this month?",
        AssistantQuestion.DueBeforePayday => "What's due before my next payday?",
        AssistantQuestion.VatThisMonth => "What VAT is shown on this month's invoices?",
        _ => "What should I send my accountant?",
    };

    public static AssistantAnswer Answer(AssistantQuestion q, IReadOnlyList<FinancialItem> items, CashFlowPlan plan, DateTime today) => q switch
    {
        AssistantQuestion.ReviewThisMonth => ReviewThisMonth(items, today),
        AssistantQuestion.DueBeforePayday => DueBeforePayday(plan),
        AssistantQuestion.VatThisMonth => VatAnswer(Vat(items, today.Year, today.Month)),
        _ => ForAccountant(items, today.AddMonths(-1).Year, today.AddMonths(-1).Month),
    };

    private static bool Counts(FinancialItem x) =>
        x.PossibleDuplicateOf == null && x.UserState != UserState.NotApplicable && x.Kind != FinKind.NotFinancial;

    private static string Name(FinancialItem i) => string.IsNullOrWhiteSpace(i.Counterparty) ? i.Title : i.Counterparty!;

    private static string Money(decimal v) => InvoiceExplainer.Money(v);

    public static AssistantAnswer ReviewThisMonth(IReadOnlyList<FinancialItem> items, DateTime today)
    {
        today = today.Date;
        var sources = new List<AnswerSource>();

        foreach (var i in items.Where(Counts))
        {
            var s = ItemRules.StatusOf(i, today);
            var date = MonthlySummary.DateOf(i);
            var thisMonth = date is { } d && d.Year == today.Year && d.Month == today.Month;

            if (s.NeedsReview)
            {
                sources.Add(new(Name(i), "Check what was read: " + (s.ReviewReason ?? "Omabilo isn't sure it read it right."), i.Id, Amount: i.Amount));
            }
            else if (s.Status == DisplayStatus.Overdue)
            {
                sources.Add(new(Name(i), $"Overdue since {InvoiceExplainer.Day(i.DueDate!.Value)} and not marked paid.", i.Id, Amount: i.Amount));
            }
            else if (thisMonth && i.IsBill && i.UserState == UserState.None)
            {
                sources.Add(new(Name(i), i.DueDate is { } due ? $"Due {InvoiceExplainer.Day(due)}; not reviewed yet." : "Not reviewed yet.", i.Id, Amount: i.Amount));
            }
        }

        var summary = sources.Count == 0
            ? "Nothing to review: no record is unsure, overdue or unreviewed this month."
            : $"{sources.Count} record{(sources.Count == 1 ? "" : "s")} to look at. Open each one to confirm, correct or mark it paid.";
        return new(QuestionText(AssistantQuestion.ReviewThisMonth), summary, sources, [], "Records Omabilo isn't sure about, overdue bills, and this month's bills you haven't reviewed.");
    }

    public static AssistantAnswer DueBeforePayday(CashFlowPlan plan)
    {
        var sources = plan.Obligations.Select(o => new AnswerSource(o.Label, o.Date is { } d ? $"Due {InvoiceExplainer.Day(d)}. {o.Note}" : o.Note, o.ItemId, Amount: o.Amount))
            .Concat(plan.Planned.Where(p => p.Label.EndsWith("(debt payment)", StringComparison.Ordinal))
                .Select(p => new AnswerSource(p.Label, p.Date is { } d ? $"Due {InvoiceExplainer.Day(d)}. {p.Note}" : p.Note, Amount: p.Amount)))
            .ToList();
        var total = sources.Sum(s => s.Amount ?? 0m);
        var missing = plan.NotCounted.Where(n => n.Reason.Contains("isn't clear") || n.Reason.Contains("isn't known") || n.Reason.Contains("isn't entered"))
            .Select(n => $"{n.Label}: {n.Reason}").ToList();
        var summary = sources.Count == 0
            ? $"No open bills or debt payments are due {LowerFirst(plan.PeriodLabel)}."
            : $"{sources.Count} payment{(sources.Count == 1 ? "" : "s")} totalling {Money(total)} {LowerFirst(plan.PeriodLabel)}.";
        return new(QuestionText(AssistantQuestion.DueBeforePayday), summary, sources, missing, "Open bills from your records and the debt payments you entered. Bills marked paid, disputed or possibly duplicated aren't included.");
    }

    private static string LowerFirst(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    /// <summary>VAT printed on the bills of one month (or a whole year when month is null), by invoice date.</summary>
    public static VatSummary Vat(IReadOnlyList<FinancialItem> items, int year, int? month)
    {
        var en = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        var label = month is { } m ? new DateTime(year, m, 1).ToString("MMMM yyyy", en) : year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool InPeriod(DateTime? d) => d is { } x && x.Year == year && (month == null || x.Month == month);

        var bills = items.Where(i => Counts(i) && i.IsBill && i.Currency == "EUR" && InPeriod(i.InvoiceDate ?? MonthlySummary.DateOf(i))).ToList();
        var lines = bills.Where(i => i.Vat is > 0 && i.Amount is > 0 && i.Vat < i.Amount)
            .Select(i => new VatLine(i.Id, Name(i), (i.InvoiceDate ?? MonthlySummary.DateOf(i))!.Value.Date, i.Amount!.Value, i.Vat!.Value, RateOf(i.Amount.Value, i.Vat.Value), i.InvoiceDate != null ? "Invoice dated" : i.DueDate != null ? "Due" : "Dated"))
            .OrderBy(l => l.Date).ToList();
        var without = bills.Where(i => !(i.Vat is > 0 && i.Amount is > 0 && i.Vat < i.Amount))
            .Select(i => new AnswerSource(Name(i), i.Amount == null ? "The amount isn't known." : "No VAT amount was read from it.", i.Id, Amount: i.Amount)).ToList();
        return new VatSummary(label, lines, without);
    }

    /// <summary>The VAT rate the amounts work out to (VAT ÷ price without VAT), to one decimal.</summary>
    public static decimal? RateOf(decimal totalWithVat, decimal vat) =>
        totalWithVat - vat is var net && net > 0 ? Math.Round(vat / net * 100m, 1) : null;

    private static AssistantAnswer VatAnswer(VatSummary v)
    {
        var sources = v.Lines.Select(l => new AnswerSource(l.Title, $"{l.DateLabel} {InvoiceExplainer.Day(l.Date)}: VAT {Money(l.Vat)} of {Money(l.Total)}"
            + (l.RatePercent is { } r ? $" (works out to {r.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} %)" : ""), l.ItemId, Amount: l.Vat)).ToList();
        var summary = v.Lines.Count == 0
            ? $"No VAT amounts were read from your invoices for {v.PeriodLabel}."
            : $"VAT shown on {v.Lines.Count} invoice{(v.Lines.Count == 1 ? "" : "s")} for {v.PeriodLabel}: {Money(v.VatTotal)} (of {Money(v.GrossTotal)} in total).";
        return new(QuestionText(AssistantQuestion.VatThisMonth), summary, sources, v.WithoutVat.Select(w => $"{w.Label}: {w.Detail}").ToList(),
            "The VAT printed on each invoice, as Omabilo read it. Whether you can deduct it depends on your situation: ask an accountant.");
    }

    /// <summary>What to gather for an accountant for one month: the records, counts and what is unread.</summary>
    public static AssistantAnswer ForAccountant(IReadOnlyList<FinancialItem> items, int year, int month)
    {
        var en = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        var label = new DateTime(year, month, 1).ToString("MMMM yyyy", en);
        var inMonth = items.Where(i => Counts(i) && MonthlySummary.DateOf(i) is { } d && d.Year == year && d.Month == month).ToList();
        var sources = inMonth.Where(i => i.IsBill || i.IsIncome || i.IsExpense)
            .OrderBy(i => i.Kind).ThenBy(i => MonthlySummary.DateOf(i))
            .Select(i => new AnswerSource(Name(i), $"{KindWord(i.Kind)}{(MonthlySummary.DateOf(i) is { } d ? $", {InvoiceExplainer.Day(d)}" : "")}" + (i.Vat is > 0 ? $", VAT {Money(i.Vat.Value)}" : ""), i.Id, Amount: i.Amount ?? i.Net))
            .ToList();
        var missing = inMonth.Where(i => i.Attachments.Any(a => !a.WasRead && a.Status != AttachmentStatus.NotSupported))
            .Select(i => $"{Name(i)}: a document couldn't be read. Keep the original file for your accountant.").ToList();
        var summary = sources.Count == 0
            ? $"Omabilo holds no invoices, receipts or payslips for {label}."
            : $"For {label}: {inMonth.Count(i => i.IsBill)} bill{(inMonth.Count(i => i.IsBill) == 1 ? "" : "s")}, {inMonth.Count(i => i.Kind == FinKind.Receipt)} receipt{(inMonth.Count(i => i.Kind == FinKind.Receipt) == 1 ? "" : "s")}, " +
              $"{inMonth.Count(i => i.IsIncome)} payslip or salary message{(inMonth.Count(i => i.IsIncome) == 1 ? "" : "s")}. Send the original documents; the list below helps you check none is missing.";
        return new(QuestionText(AssistantQuestion.ForAccountant), summary, sources, missing,
            "Records dated in the month (bills by due date). Your accountant may also need bank statements and sales invoices, which Omabilo doesn't hold.", label);
    }

    private static string KindWord(FinKind k) => k switch
    {
        FinKind.Invoice => "Invoice",
        FinKind.PaymentReminder => "Payment reminder",
        FinKind.DebtCollection => "Collection notice",
        FinKind.Receipt => "Receipt",
        FinKind.Refund => "Refund",
        FinKind.Subscription => "Subscription",
        FinKind.Salary => "Salary message",
        FinKind.Payslip => "Payslip",
        FinKind.TaxNotice => "Tax notice",
        FinKind.Insurance => "Insurance",
        FinKind.LoanInstalment => "Loan instalment",
        _ => "Record",
    };

    /// <summary>A short email to an accountant for the person to review, edit and send themselves.</summary>
    /// <summary>The draft for the month the pack covers, so the two never name different months.</summary>
    public static string AccountantDraft(AssistantAnswer pack) => AccountantDraft(pack, pack.Period ?? "this month");

    public static string AccountantDraft(AssistantAnswer pack, string monthLabel)
    {
        var lines = new List<string>
        {
            "Hello,",
            "",
            pack.Sources.Count == 0
                ? $"I'm putting together my documents for {monthLabel}. Omabilo doesn't hold any invoices, receipts or payslips for that month, so I have nothing to list yet."
                : $"I'm putting together my documents for {monthLabel}. These are the ones I have:",
        };
        lines.AddRange(pack.Sources.Take(40).Select(s => $"- {s.Label}: {s.Detail}" + (s.Amount is { } a ? $", {Money(a)}" : "")));

        if (pack.Missing.Count > 0)
        {
            lines.Add("");
            lines.Add("Some documents I'll send as original files:");
            lines.AddRange(pack.Missing.Select(m => $"- {m}"));
        }

        lines.Add("");
        lines.Add("Could you let me know if anything else is needed, and whether any of these affect my VAT or taxes?");
        lines.Add("");
        lines.Add("Best regards,");
        return string.Join("\n", lines);
    }

    /// <summary>The tax card the person typed, against gross pay on the payslips Omabilo holds for that year.</summary>
    public static TaxCardStatus TaxCard(IReadOnlyList<FinancialItem> items, CompanionSettings s, DateTime today)
    {
        var year = s.TaxCardYear ?? today.Year;
        var payslips = items.Where(i => Counts(i) && i.IsIncome && i.EventDate is { } d && d.Year == year && d.Date <= today.Date).ToList();
        var withGross = new List<FinancialItem>();

        // A payslip and a "salary paid" email for the same pay count once
        foreach (var p in payslips.Where(p => p.Gross is > 0).OrderBy(p => p.Kind != FinKind.Payslip).ThenBy(p => p.EventDate))
        {
            if (!withGross.Any(w => w.Gross == p.Gross && Math.Abs((w.EventDate!.Value - p.EventDate!.Value).TotalDays) <= 3))
            {
                withGross.Add(p);
            }
        }

        var gross = withGross.Sum(p => p.Gross!.Value);
        var withoutGross = payslips.Count(p => p.Kind == FinKind.Payslip && p.Gross is not > 0);
        decimal? left = s.TaxIncomeLimit is { } limit ? limit - gross : null;
        var sources = withGross.Select(p => new AnswerSource(Name(p), $"Gross pay on {InvoiceExplainer.Day(p.EventDate!.Value)}", p.Id, Amount: p.Gross)).ToList();

        string summary;

        if (s.TaxRatePercent == null && s.TaxIncomeLimit == null)
        {
            summary = "Enter the numbers on your tax card (verokortti) to track your income limit. You find them in OmaVero or on the card your employer has.";
        }
        else if (left is { } l && l < 0)
        {
            summary = $"Gross pay on your payslips for {year} is {Money(gross)}, {Money(-l)} over your income limit. Above the limit the additional rate"
                + (s.TaxAdditionalRatePercent is { } add ? $" ({Fmt(add)} %)" : "") + " applies. Consider asking for a new tax card in OmaVero.";
        }
        else if (left is { } l2)
        {
            summary = $"Gross pay on your payslips for {year} is {Money(gross)}. About {Money(l2)} is left under your income limit of {Money(s.TaxIncomeLimit!.Value)}.";
        }
        else
        {
            summary = $"Gross pay on your payslips for {year} is {Money(gross)}. Enter your income limit (tuloraja) to see how much is left under it.";
        }

        if (withoutGross > 0)
        {
            summary += $" {withoutGross} payslip{(withoutGross == 1 ? " has" : "s have")} no gross pay read, so {(withoutGross == 1 ? "it isn't" : "they aren't")} counted.";
        }

        return new TaxCardStatus(year, s.TaxRatePercent, s.TaxAdditionalRatePercent, s.TaxIncomeLimit, gross, withGross.Count, withoutGross, left, summary, sources);
    }

    private static string Fmt(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public static readonly IReadOnlyList<GlossaryTerm> Glossary =
    [
        new("Lasku", "Invoice, bill", "A request to pay for goods or a service."),
        new("Eräpäivä", "Due date", "The last day to pay without a reminder fee or late interest."),
        new("Viitenumero", "Reference number", "Identifies your payment to the biller. Copy it exactly; a Finnish reference has a check digit."),
        new("Laskun päivämäärä", "Invoice date", "The day the invoice was written."),
        new("Saaja", "Payee", "Who the money goes to."),
        new("Tilinumero (IBAN)", "Account number", "The payee's bank account. Check it against details you already trust."),
        new("Yhteensä / Maksettava", "Total / Amount to pay", "What you pay, VAT included."),
        new("ALV (arvonlisävero)", "VAT, value added tax", "Tax included in the price of most goods and services."),
        new("Veroton / Verollinen", "Without VAT / With VAT", "The price before VAT, and the price including it."),
        new("Hyvityslasku", "Credit note", "Reduces or cancels an earlier invoice."),
        new("Maksumuistutus", "Payment reminder", "Sent when the due date passed; it may add a reminder fee."),
        new("Muistutusmaksu", "Reminder fee", "A fee added to a late bill's reminder."),
        new("Viivästyskorko", "Late-payment interest", "Interest charged on a late bill from the day after the due date."),
        new("Perintä / Perintätoimisto", "Debt collection / Collection agency", "A company collecting an unpaid bill for the biller. Contact them early if you can't pay."),
        new("Maksusuunnitelma", "Payment plan", "An agreement to pay a debt in parts."),
        new("Osamaksu", "Instalment payment", "Paying a purchase in parts, often with interest and fees."),
        new("E-lasku", "E-invoice", "An invoice delivered to your online bank, ready to approve."),
        new("Suoramaksu", "Direct payment", "Your bank pays the bill on the due date after notifying you."),
        new("Kuitti", "Receipt", "Proof of a purchase or payment."),
        new("Y-tunnus", "Business ID", "A Finnish company's identification number."),
        new("Verokortti", "Tax card", "Tells your employer how much tax to withhold from your pay."),
        new("Ennakonpidätys", "Tax withholding", "Tax your employer takes from your pay in advance."),
        new("Tuloraja", "Income limit", "The yearly income your tax card's basic rate covers."),
        new("Lisäprosentti", "Additional rate", "The withholding rate for income above the income limit."),
        new("Palkkalaskelma", "Payslip", "Shows gross pay, deductions and net pay. It's a calculation, not proof the money arrived."),
        new("Bruttopalkka / Nettopalkka", "Gross pay / Net pay", "Pay before deductions, and what is paid to your account."),
        new("Ennakkovero", "Prepayment of tax", "Tax paid in advance during the year, for example on business income."),
        new("Verotuspäätös", "Tax decision", "The yearly result of your taxation."),
        new("Veronpalautus / Jäännösvero", "Tax refund / Residual tax", "Money back when too much was withheld, or tax still to pay when too little was."),
        new("OmaVero", "OmaVero", "The online service of Vero, the Finnish Tax Administration, at vero.fi."),
        new("YEL", "Self-employed pension insurance", "Pension insurance self-employed people in Finland take out themselves."),
    ];
}
