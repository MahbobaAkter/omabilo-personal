// Turns one classified email into a FinancialItem: the fields the message states, what is missing
// or conflicting, and warning flags. Bills reuse InvoiceExplainer (and through it the invoice text
// reader and the Finnish reference check); payslips and receipts have their own readers here.
// Nothing is invented: a field the message doesn't state stays null and is listed as missing.

using System.Globalization;
using System.Text.RegularExpressions;

public static class RecordExtractor
{
    public const int ExcerptLength = 800;

    public static FinancialItem Extract(string userId, MailMessage mail, Classification c, DateTime today)
    {
        var text = FullText(mail);
        var sender = SenderName(mail.From);
        var now = DateTime.UtcNow;
        var source = new SourceRef(mail.Id, mail.From, mail.Subject, mail.ReceivedUtc, Excerpt(text), "original");

        var item = new FinancialItem
        {
            Id = "it_" + Guid.NewGuid().ToString("N")[..16],
            UserId = userId,
            Kind = c.Kind,
            Confidence = c.Confidence,
            ClassifiedBy = c.Method,
            ClassificationReasons = c.Reasons.ToList(),
            Sources = [source],
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        item = c.Kind switch
        {
            FinKind.Payslip or FinKind.Salary => ExtractSalary(item, text, sender, mail),
            FinKind.Receipt or FinKind.Refund => ExtractReceipt(item, text, sender, mail),
            FinKind.PaymentConfirmation => ExtractConfirmation(item, text, sender, mail),
            FinKind.OtherFinancial => item with { Counterparty = sender, Title = mail.Subject.Length > 0 ? mail.Subject : $"Message from {sender}", EventDate = mail.ReceivedUtc.Date },
            _ => ExtractBill(item, text, mail, today),
        };

        var currency = DetectCurrency(text);
        if (currency == null && (item.Amount ?? item.Net) != null)
        {
            item = item with { Missing = [.. item.Missing, "The currency isn't stated, so euros are assumed"] };
        }

        return item with
        {
            Currency = currency ?? "EUR",
            Category = Categorize(item.Kind, $"{mail.From} {mail.Subject} {text}", item.Counterparty),
        };
    }

    // ── Bills ───────────────────────────────────────────────────────────────

    private static FinancialItem ExtractBill(FinancialItem item, string text, MailMessage mail, DateTime today)
    {
        var e = InvoiceExplainer.Explain($"From: {mail.From}\nSubject: {mail.Subject}\n\n{text}", today);
        var fields = TextInvoiceExtractor.Extract(text);
        var counterparty = e.Field("Sender or company").Value ?? SenderName(mail.From);
        var flags = e.CheckBeforePaying.ToList();
        var missing = e.MissingOrUncertain.ToList();

        var originalDue = LabeledDate(text, ["original due date", "alkuperäinen eräpäivä"]);
        var period = Period(text);
        var iban = Regex.Match(text, @"\bFI\d{2}(?:\s?\d{4}){3}\s?\d{2}\b", RegexOptions.IgnoreCase);
        var instructions = iban.Success || e.Field("Reference number").Value != null
            ? string.Join(" · ", new[] { iban.Success ? $"Account {iban.Value}" : null, e.Field("Reference number").Value is { } r ? $"reference {r}" : null }.OfType<string>())
            : null;

        // Two different labelled totals is a conflict, not a choice to make silently
        string[] totalLabels = ["amount due", "total to pay", "total", "maksettava", "yhteensä", "summa"];
        var labelled = totalLabels.SelectMany(l => LabeledAmounts(text, [l])).ToList();
        if (labelled.Distinct().Count() > 1 && !e.IsReminder)
        {
            flags.Add($"The message gives different totals ({string.Join(", ", labelled.Distinct().Select(InvoiceExplainer.Money))}).");
        }

        if (e.DueDate is { } due && fields.InvoiceDate is { } issued && due < issued)
        {
            flags.Add("The due date is before the invoice date.");
        }

        var title = item.Kind switch
        {
            FinKind.PaymentReminder => $"Payment reminder from {counterparty}",
            FinKind.DebtCollection => $"Collection notice from {counterparty}",
            FinKind.TaxNotice => $"Tax notice from {counterparty}",
            FinKind.Subscription => $"Subscription charge from {counterparty}",
            FinKind.Insurance => $"Insurance premium from {counterparty}",
            FinKind.LoanInstalment => $"Loan instalment from {counterparty}",
            _ => $"Invoice from {counterparty}",
        };

        if (item.Kind == FinKind.DebtCollection)
        {
            flags.Add("A collection notice doesn't prove the debt is valid. Check it against the original bill before paying, and dispute it if it's wrong.");
        }

        var evidence = new List<string>();
        if (e.IsReminder) evidence.Add($"The sender says an earlier bill hasn't been paid ({InvoiceExplainer.Day(mail.ReceivedUtc)}).");
        if (e.SaysPaid) evidence.Add("The message says the payment was received.");

        return item with
        {
            Title = title,
            Counterparty = counterparty,
            Purpose = e.Field("What it's for").Value,
            InvoiceNumber = fields.InvoiceNumber,
            Reference = fields.Reference,
            Amount = e.Amount,
            InvoiceDate = fields.InvoiceDate,
            DueDate = e.DueDate,
            OriginalDueDate = originalDue,
            Period = period,
            PaymentInstructions = instructions,
            Flags = flags.Distinct().ToList(),
            Missing = missing,
            Evidence = evidence,
            Suspicious = e.Urgency == Urgency.CheckFirst,
            UserState = e.SaysPaid ? UserState.ConfirmedPaid : UserState.None,
        };
    }

    // ── Salary ──────────────────────────────────────────────────────────────

    private static FinancialItem ExtractSalary(FinancialItem item, string text, string sender, MailMessage mail)
    {
        var employer = LabeledText(text, ["employer", "työnantaja", "company"]) ?? sender;
        var gross = LabeledAmount(text, ["gross salary", "gross pay", "gross", "bruttopalkka", "brutto", "palkka yhteensä"]);
        var net = LabeledAmount(text, ["net salary", "net pay", "nettopalkka", "netto", "to be paid", "maksetaan", "paid to your account", "tilille maksetaan"]);
        var tax = LabeledAmount(text, ["tax withholding", "withholding tax", "income tax", "ennakonpidätys", "vero"]);
        var pension = LabeledAmount(text, ["pension contribution", "työeläkemaksu", "tyel"]) ?? 0;
        var unemployment = LabeledAmount(text, ["unemployment insurance", "työttömyysvakuutusmaksu"]) ?? 0;
        var other = pension + unemployment;
        var payDate = LabeledDate(text, ["pay date", "payment date", "payday", "palkkapäivä", "maksupäivä", "paid on"]);
        var period = Period(text);

        var missing = new List<string>();
        var flags = new List<string>();
        if (net == null) missing.Add("Net salary (what reaches your account)");
        if (payDate == null) missing.Add("Payment date");
        if (gross == null) missing.Add("Gross salary");

        if (gross is { } g && net is { } n)
        {
            if (n > g)
            {
                flags.Add("Net salary is higher than gross salary, which is unusual. Check the payslip.");
            }
            else if (tax is { } t && Math.Abs(g - t - other - n) > 1m && other + t > 0)
            {
                flags.Add($"Gross minus the listed deductions ({InvoiceExplainer.Money(g - t - other)}) doesn't match net salary ({InvoiceExplainer.Money(n)}). Some deductions may not be listed.");
            }
        }

        var saysPaid = item.Kind == FinKind.Salary || Regex.IsMatch(text, @"(?i)(has been paid|palkka maksettu|on maksettu tilillesi|maksettu tilille)");
        var evidence = saysPaid
            ? IncomeEvidence.ReportedReceived
            : item.Kind == FinKind.Payslip ? IncomeEvidence.Payslip : IncomeEvidence.Scheduled;

        var notes = new List<string>
        {
            evidence switch
            {
                IncomeEvidence.ReportedReceived => "The sender says the salary was paid. Omabilo can't see your bank, so check that it arrived.",
                IncomeEvidence.Payslip => "A payslip shows how the salary was calculated. It doesn't prove the money has reached your account.",
                _ => "Salary is expected but not confirmed.",
            },
        };

        return item with
        {
            Title = item.Kind == FinKind.Payslip ? $"Payslip from {employer}" : $"Salary from {employer}",
            Counterparty = employer,
            Gross = gross,
            Net = net,
            Tax = tax,
            OtherDeductions = other > 0 ? other : null,
            Amount = net,
            EventDate = payDate,
            Period = period,
            IncomeEvidence = evidence,
            Missing = missing,
            Flags = flags,
            Evidence = notes,
        };
    }

    // ── Receipts ────────────────────────────────────────────────────────────

    private static FinancialItem ExtractReceipt(FinancialItem item, string text, string sender, MailMessage mail)
    {
        var merchant = LabeledText(text, ["merchant", "seller", "store", "myyjä", "kauppa"]) ?? sender;
        var total = LabeledAmount(text, ["total paid", "amount paid", "total", "yhteensä", "summa", "veloitettu", "charged", "refund amount", "hyvitys"]);
        var vat = LabeledAmount(text, ["vat", "alv"]);
        var date = LabeledDate(text, ["purchase date", "order date", "date", "päivämäärä", "ostopäivä"]) ?? mail.ReceivedUtc.Date;
        var order = Regex.Match(text, @"(?im)\b(?:order(?: number| no\.?| #)?|tilausnumero|tilaus|receipt (?:no|number)|kuittinumero)\s*[:#]?\s*([A-Z0-9][A-Z0-9-]{3,})");

        var missing = new List<string>();
        var flags = new List<string>();
        if (total == null) missing.Add("The total amount");
        if (vat is { } v && total is { } tt && v >= tt) flags.Add("The VAT amount is not smaller than the total. Check the receipt.");

        var isCardNotice = Regex.IsMatch(text, @"(?i)(korttiosto|card payment|card purchase|katevaraus)");
        if (isCardNotice) flags.Add("This looks like a card payment notice. If you also have a receipt for it, it's counted once.");
        if (Regex.IsMatch(text, @"(?i)(cancelled|canceled|peruttu|peruutettu)")) flags.Add("The message mentions a cancellation. Check whether this purchase still stands.");

        var refund = item.Kind == FinKind.Refund;
        return item with
        {
            Title = refund ? $"Refund from {merchant}" : $"Purchase at {merchant}",
            Counterparty = merchant,
            Amount = total,
            Vat = vat,
            EventDate = date,
            OrderRef = order.Success ? order.Groups[1].Value : null,
            Missing = missing,
            Flags = flags,
            Evidence = [refund ? "A refund notice says money is coming back. It counts once it's confirmed in your bank." : "A receipt shows spending that has already happened."],
        };
    }

    private static FinancialItem ExtractConfirmation(FinancialItem item, string text, string sender, MailMessage mail)
    {
        var fields = TextInvoiceExtractor.Extract(text);
        var amount = fields.Amount ?? LabeledAmount(text, ["amount", "summa", "paid", "maksettu"]);
        return item with
        {
            Title = $"Payment confirmation from {sender}",
            Counterparty = sender,
            Amount = amount,
            InvoiceNumber = fields.InvoiceNumber,
            Reference = fields.Reference,
            EventDate = mail.ReceivedUtc.Date,
            Evidence = ["The sender confirms receiving a payment."],
        };
    }

    // ── Text helpers ────────────────────────────────────────────────────────

    /// <summary>The body plus any readable attachment, each attachment marked so excerpts can say where a line came from.</summary>
    public static string FullText(MailMessage mail)
    {
        var parts = new List<string> { (mail.BodyText.Length > 0 ? mail.BodyText : mail.Snippet).Replace("\r\n", "\n") };

        foreach (var a in mail.Attachments.Where(a => !string.IsNullOrWhiteSpace(a.Text)))
        {
            parts.Add($"--- Attachment: {a.FileName} ---\n{a.Text!.Replace("\r\n", "\n")}");
        }

        return string.Join("\n\n", parts);
    }

    private static readonly Regex CardRx = new(@"\b(?:\d{4}[ -]?){3}\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex HetuRx = new(@"\b\d{6}[-+A-FYXWVU]\d{3}[0-9A-Y]\b", RegexOptions.Compiled);
    private static readonly Regex UsefulLineRx = new(@"(?i)(\d|€|eur|due|eräp|viite|reference|invoice|lasku|total|yhteensä|salary|palkka|netto|brutto|refund|order)", RegexOptions.Compiled);

    /// <summary>The lines that matter (amounts, dates, references), at most 800 characters, with card numbers and identity codes masked.</summary>
    public static string Excerpt(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && UsefulLineRx.IsMatch(l) && !l.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        var sb = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > ExcerptLength) break;
            sb.AppendLine(line);
        }

        var excerpt = sb.Length > 0 ? sb.ToString().TrimEnd() : text[..Math.Min(text.Length, ExcerptLength)];
        return HetuRx.Replace(CardRx.Replace(excerpt, "[card number hidden]"), "[identity code hidden]");
    }

    public static string SenderName(string from)
    {
        var name = from.Split('<')[0].Trim().Trim('"').Trim();

        if (name.Length > 0 && !name.Contains('@'))
        {
            return name;
        }

        var addr = Regex.Match(from, @"[\w.+-]+@([\w-]+)\.");
        return addr.Success ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(addr.Groups[1].Value.Replace('-', ' ')) : "Unknown sender";
    }

    // Grouped thousands ("3 200,00", "1.234,56"), plain Finnish ("3200,00"), English ("1,234.56", "120.00"), or whole numbers
    private const string AmountPattern = @"(-?\d{1,3}(?:[  .]\d{3})+(?:,\d{1,2})?|-?\d+,\d{1,2}|-?\d{1,3}(?:,\d{3})+\.\d{2}|-?\d+\.\d{2}|-?\d+)";

    public static decimal? LabeledAmount(string text, string[] labels) => LabeledAmounts(text, labels).Cast<decimal?>().FirstOrDefault();

    public static List<decimal> LabeledAmounts(string text, string[] labels)
    {
        var result = new List<decimal>();

        foreach (var label in labels)
        {
            var rx = new Regex($@"(?im)(?<![\p{{L}}]){Regex.Escape(label)}\b[^\d\n-]{{0,30}}(?:\d{{1,2}}(?:[,.]\d{{1,2}})?\s*%[^\d\n-]{{0,10}})?{AmountPattern}(?!\d|,\d|\.\d|\s*%)");

            foreach (Match m in rx.Matches(text))
            {
                if (TextInvoiceExtractor.ParseAmount(m.Groups[1].Value.TrimStart('-')) is { } v)
                {
                    result.Add(v);
                }
            }

            if (result.Count > 0)
            {
                break;
            }
        }

        return result;
    }

    private static readonly string[] MonthNames = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    public static DateTime? LabeledDate(string text, string[] labels)
    {
        foreach (var label in labels)
        {
            var m = Regex.Match(text, $@"(?im)(?<![\p{{L}}]){Regex.Escape(label)}\b[^\d\n]{{0,20}}(\d{{1,2}}[./]\d{{1,2}}[./]\d{{4}}|\d{{4}}-\d{{2}}-\d{{2}}|\d{{1,2}}\.?\s+(?:{string.Join("|", MonthNames)})\s+\d{{4}})");

            if (m.Success && ParseDate(m.Groups[1].Value) is { } d)
            {
                return d;
            }
        }

        return null;
    }

    public static DateTime? ParseDate(string text)
    {
        if (TextInvoiceExtractor.ParseDate(text) is { } numeric)
        {
            return numeric;
        }

        var cleaned = text.Replace(".", "").Trim();
        return DateTime.TryParseExact(cleaned, ["d MMMM yyyy", "dd MMMM yyyy"], CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)
            : null;
    }

    private static string? LabeledText(string text, string[] labels)
    {
        foreach (var label in labels)
        {
            var m = Regex.Match(text, $@"(?im)^\s*{Regex.Escape(label)}\s*:\s*(.{{2,80}})$");

            if (m.Success)
            {
                return m.Groups[1].Value.Trim();
            }
        }

        return null;
    }

    private static string? Period(string text)
    {
        var m = Regex.Match(text, @"(?im)\b(?:billing period|pay period|period|palkkakausi|laskutuskausi|ajalta|ajanjakso)\s*:?\s*([^\n]{3,40})");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public static string? DetectCurrency(string text)
    {
        if (Regex.IsMatch(text, @"€|\bEUR\b|\beuroa?\b", RegexOptions.IgnoreCase)) return "EUR";
        if (Regex.IsMatch(text, @"\$|\bUSD\b")) return "USD";
        if (Regex.IsMatch(text, @"£|\bGBP\b")) return "GBP";
        if (Regex.IsMatch(text, @"\bSEK\b")) return "SEK";
        return null;
    }

    // ── Categories ──────────────────────────────────────────────────────────

    private static readonly (string Category, string[] Terms)[] CategoryTerms =
    [
        (Categories.Housing, ["rent", "vuokra", "vastike", "housing", "asunto", "isännöinti"]),
        (Categories.Utilities, ["electricity", "sähkö", "kwh", "water", "vesi", "kaukolämpö", "district heating", "energy", "energia", "jäte", "waste"]),
        (Categories.PhoneInternet, ["broadband", "laajakaista", "internet", "mobile", "puhelin", "liittymä", "elisa", "telia", "dna"]),
        (Categories.Insurance, ["insurance", "vakuutus"]),
        (Categories.Health, ["pharmacy", "apteekki", "dentist", "hammas", "clinic", "terveys", "lääkäri", "doctor", "mehiläinen", "terveystalo"]),
        (Categories.Subscriptions, ["subscription", "netflix", "spotify", "hbo", "disney+", "youtube premium", "icloud", "membership", "jäsenyys", "kuntosali", "gym"]),
        (Categories.Debt, ["loan", "laina", "lyhennys", "credit card", "luottokortti", "instalment", "osamaksu", "perintä", "collection"]),
        (Categories.Groceries, ["k-market", "k-citymarket", "s-market", "prisma", "lidl", "alepa", "grocery", "ruokakauppa", "foodie"]),
        (Categories.Transport, ["hsl", "vr.fi", "train", "juna", "taxi", "taksi", "uber", "bolt", "fuel", "polttoaine", "parking", "pysäköinti", "nysse", "föli"]),
        (Categories.Shopping, ["zalando", "amazon", "verkkokauppa", "gigantti", "ikea", "power.fi", "order", "tilaus"]),
    ];

    public static string Categorize(FinKind kind, string text, string? counterparty)
    {
        if (kind is FinKind.Salary or FinKind.Payslip) return Categories.Income;
        if (kind == FinKind.TaxNotice) return Categories.Taxes;
        if (kind is FinKind.DebtCollection or FinKind.LoanInstalment) return Categories.Debt;
        if (kind == FinKind.Insurance) return Categories.Insurance;

        var lower = $"{counterparty} {text}".ToLowerInvariant();

        foreach (var (category, terms) in CategoryTerms)
        {
            if (terms.Any(t => MessageClassifier.HasTerm(lower, t)))
            {
                return category;
            }
        }

        return kind == FinKind.Subscription ? Categories.Subscriptions : Categories.Other;
    }
}
