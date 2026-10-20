// Rule-based classification of one email: financial or not, and which kind. Deterministic and
// explainable: every decision lists the words or patterns that led to it. AI, when the person turns
// it on, only explains; it never decides on its own what is counted.
//
// A word alone never makes an email financial. Each kind needs its own evidence (an amount, a due
// date, a reference, a payslip's figures), words in a newsletter's footer ("manage your
// subscription", "refund policy") are ignored, and mail Gmail files under Promotions or Social only
// counts when it carries real invoice fields.

using System.Text.RegularExpressions;

public static class MessageClassifier
{
    private record Rule(FinKind Kind, double Weight, string[] Terms);

    // English and Finnish. A term ending in * matches word starts ("lasku*" finds "laskusi");
    // any other term matches the whole word or phrase, plural included ("invoice" finds "invoices"
    // but never "invoiced", and "bill" is not "billing" or a person called Bill)
    private static readonly Rule[] Rules =
    [
        new(FinKind.DebtCollection, 3, ["debt collection", "collection agency", "perintä*", "perintätoimisto*", "inkasso*", "maksuhäiriö*"]),
        new(FinKind.PaymentReminder, 3, ["payment reminder", "maksumuistutu*", "muistutus laskusta", "overdue", "past due", "erääntyn*", "we have not received your payment", "have not received your payment", "emme ole saaneet maksua*"]),
        new(FinKind.Payslip, 3, ["palkkalaskelma*", "palkkaerittely*", "palkkakuitti*", "palkkatosite*", "lönespecifikation*", "payslip", "pay slip", "e-payslip", "salary slip", "salary statement", "wage statement", "earnings statement", "pay stub", "paystub", "pay advice"]),
        new(FinKind.Salary, 2.5, ["salary has been paid", "salary payment", "salary paid", "salary credited", "salary deposit", "palkka maksettu", "palkanmaksu*", "palkkasi on maksettu", "palkka on maksettu", "your wages", "wages paid", "payroll", "palkka", "salary", "lön"]),
        new(FinKind.TaxNotice, 3, ["verohallinto", "vero.fi", "verotuspäätö*", "tax decision", "veronpalautu*", "jäännösvero*", "tax return", "veroilmoitu*", "residual tax", "tax refund"]),
        new(FinKind.Refund, 2.5, ["refund", "refunded", "hyvitys*", "palautus on käsitelty", "money back", "credit note", "hyvityslasku*"]),
        new(FinKind.PaymentConfirmation, 2.5, ["payment received", "thank you for your payment", "kiitos maksusta*", "maksu vastaanotettu", "maksusi on vastaanotettu", "has been paid", "paid in full"]),
        new(FinKind.Receipt, 2, ["receipt", "kuitti*", "order confirmation", "tilausvahvistu*", "thank you for your order", "kiitos tilauksesta*", "your purchase", "ostoksesi", "purchase confirmation", "korttiosto*", "card payment"]),
        new(FinKind.Subscription, 2, ["subscription", "tilauksesi uusitaan", "your plan renews", "renewal", "membership fee", "jäsenmaksu*", "will renew", "automatically renew"]),
        new(FinKind.Insurance, 2, ["vakuutusmaksu*", "insurance premium", "vakuutuslasku*"]),
        new(FinKind.LoanInstalment, 2, ["loan instalment", "loan installment", "lainan lyhennys", "lyhennyserä*", "instalment", "installment", "osamaksu*"]),
        new(FinKind.Invoice, 2, ["invoice", "lasku*", "e-lasku*", "your bill", "new bill", "the bill", "bill is", "bill for", "amount due", "eräpäivä*", "due date", "viitenumero*", "maksettava*", "to be paid", "faktura", "rechnung"]),
        new(FinKind.OtherFinancial, 1, ["tiliote*", "bank statement", "account statement", "credit card statement", "luottokorttilasku*"]),
    ];

    private static readonly string[] MarketingTerms =
        ["% off", "sale ends", "shop now", "limited time", "newsletter", "uutiskirje", "tarjous", "ale ", "discount code", "black friday", "webinar", "free shipping",
         "unsubscribe", "peruuta tilaus", "view in browser", "näytä selaimessa", "special offer", "exclusive offer", "coupon", "promo code", "alennuskoodi"];

    // Job adverts mention salaries with euro amounts; they aren't income
    private static readonly string[] JobAdTerms = ["job alert", "jobs for you", "apply now", "hae nyt", "työpaikka", "avoin työ", "rekry", "recruit", "vacancy", "hiring", "salary range", "palkkatoive"];

    // Footer and link lines name finance words that say nothing about the email itself
    private static readonly Regex BoilerplateRx = new(
        @"(?i)(unsubscribe|manage (your )?(subscription|preferences|email)|email preferences|update your preferences|privacy (policy|notice)|refund policy|return policy|returns policy|terms (and|&) conditions|terms of (service|use)|view (it )?in (your )?browser|you('| a)re receiving this|you received this|this email was sent|copyright|©|all rights reserved|tietosuoja|peruuta tilaus|lopeta tilaus|uutiskirjeen tilaus|^\s*https?://\S+\s*$)",
        RegexOptions.Compiled);

    private static readonly Regex MoneyRx = new(@"(?i)(?:€\s*\d|\d[\d\s.,]*\s*(?:€|eur\b|euroa?\b)|\b(?:eur|usd|gbp|sek)\s*\d|[$£]\s*\d)", RegexOptions.Compiled);
    private static readonly Regex StrongInvoiceRx = new(@"(?i)\b(?:reference(?: number)?|viite(?:numero)?|invoice (?:number|no\.?)|laskun numero|laskunumero|due date|eräpäivä|amount due|total due|maksettava|iban|virtuaaliviivakoodi|barcode)\b\s*[:#]?\s*\S", RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"\b\d{1,2}[./]\d{1,2}[./]\d{2,4}\b|\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}\.?\s+(?:january|february|march|april|may|june|july|august|september|october|november|december)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PaySlipFigureRx = new(@"(?i)\b(?:net (?:pay|salary)|gross (?:pay|salary)|nettopalkka|bruttopalkka|netto|brutto|maksetaan tilille|ennakonpidätys|withholding)\b[^\n\d]{0,30}\d", RegexOptions.Compiled);

    /// <param name="labels">Gmail's labels for the message (CATEGORY_PROMOTIONS…), when it came from Gmail.</param>
    /// <param name="attachmentNames">File names of the attachments: a payslip often arrives as a locked PDF named like one.</param>
    public static Classification Classify(string from, string subject, string text, IReadOnlyCollection<string>? labels = null, IReadOnlyCollection<string>? attachmentNames = null)
    {
        labels ??= [];
        attachmentNames ??= [];

        if (labels.Contains("SPAM") || labels.Contains("TRASH"))
        {
            return new Classification(false, FinKind.NotFinancial, 0.95, ["Gmail filed it as spam or trash"], "rules");
        }

        var subjectLower = subject.ToLowerInvariant();
        var body = StripBoilerplate(text);
        var lower = $"{subject}\n{body}".ToLowerInvariant();
        var fullLower = $"{subject}\n{text}".ToLowerInvariant();
        var files = string.Join("\n", attachmentNames).ToLowerInvariant();
        var scores = new Dictionary<FinKind, double>();
        var inSubject = new HashSet<FinKind>();
        var reasons = new List<string>();

        foreach (var rule in Rules)
        {
            foreach (var term in rule.Terms)
            {
                var subjectHit = MatchesTerm(subjectLower, term);

                if (subjectHit || MatchesTerm(lower, term) || MatchesTerm(files, term))
                {
                    // Words in the subject count double: they say what the message is about
                    scores[rule.Kind] = scores.GetValueOrDefault(rule.Kind) + (subjectHit ? rule.Weight * 2 : rule.Weight);
                    if (subjectHit) inSubject.Add(rule.Kind);
                    reasons.Add($"mentions “{term.TrimEnd('*')}”");
                    break;
                }
            }
        }

        var all = $"{subject}\n{body}";
        var hasMoney = MoneyRx.IsMatch(all);
        var strongFields = StrongInvoiceRx.Matches(body).Count;
        var hasDate = DateRx.IsMatch(all);
        var marketing = MarketingTerms.Count(t => fullLower.Contains(t));
        var bulk = labels.Contains("CATEGORY_PROMOTIONS") || labels.Contains("CATEGORY_SOCIAL");

        if (hasMoney) reasons.Add("contains an amount of money");
        if (strongFields > 0) reasons.Add($"has {strongFields} invoice field{(strongFields == 1 ? "" : "s")} (reference, due date…)");

        // Gmail's own sorting: promotions and social mail is a bill only with real invoice fields
        if (bulk && !(hasMoney && strongFields >= 2))
        {
            return new Classification(false, FinKind.NotFinancial, 0.9, ["Gmail sorted it under Promotions or Social, and it has no invoice details"], "rules");
        }

        // Advertising mentions prices too; without invoice fields it isn't a bill
        if (marketing >= 2 && strongFields == 0 && !inSubject.Contains(FinKind.Payslip) && !inSubject.Contains(FinKind.Receipt) && !inSubject.Contains(FinKind.Refund))
        {
            return new Classification(false, FinKind.NotFinancial, 0.9, ["looks like marketing (offers, discounts or a newsletter)"], "rules");
        }

        if (JobAdTerms.Any(t => fullLower.Contains(t)))
        {
            scores.Remove(FinKind.Salary);
        }

        // Each kind needs its own evidence, not just a word
        foreach (var kind in scores.Keys.ToList())
        {
            if (!HasEvidence(kind, inSubject.Contains(kind), hasMoney, strongFields, hasDate, all, files, from))
            {
                scores.Remove(kind);
            }
        }

        if (scores.Count == 0)
        {
            return new Classification(false, FinKind.NotFinancial, hasMoney ? 0.6 : 0.9,
                [hasMoney ? "mentions money but no bill, receipt, salary or tax details" : "no financial details found"], "rules");
        }

        var kindFound = PickKind(scores);
        var top = scores[kindFound];
        var confidence = Math.Min(0.97, 0.45 + top * 0.07 + (hasMoney ? 0.15 : 0) + Math.Min(strongFields, 3) * 0.06);
        return new Classification(true, kindFound, Math.Round(confidence, 2), reasons.Distinct().ToList(), "rules");
    }

    /// <summary>From the headers and Gmail's short snippet alone: whether the whole email is worth
    /// downloading. Generous on purpose: anything that might be financial is read in full and judged then.</summary>
    public static bool WorthReading(string subject, string snippet, IReadOnlyCollection<string>? labels = null)
    {
        labels ??= [];

        if (labels.Contains("SPAM") || labels.Contains("TRASH"))
        {
            return false;
        }

        var subjectLower = subject.ToLowerInvariant();
        var lower = $"{subjectLower}\n{snippet.ToLowerInvariant()}";

        var billInSubject = Rules.Where(r => r.Kind is FinKind.Invoice or FinKind.PaymentReminder or FinKind.DebtCollection)
            .Any(r => r.Terms.Any(t => MatchesTerm(subjectLower, t)));

        // Promotions, social mail and plain advertising are read only when the subject names a bill
        if (labels.Contains("CATEGORY_PROMOTIONS") || labels.Contains("CATEGORY_SOCIAL") || MarketingTerms.Count(t => lower.Contains(t)) >= 2)
        {
            return billInSubject;
        }

        return MoneyRx.IsMatch(lower) || Rules.Any(r => r.Terms.Any(t => MatchesTerm(lower, t)));
    }

    private static bool HasEvidence(FinKind kind, bool namedInSubject, bool hasMoney, int strongFields, bool hasDate, string all, string files, string from) => kind switch
    {
        // A bill to pay states an amount and how or when to pay it, or says what it is in its subject
        // ("Your new e-invoice has arrived" names no amount but is still a bill to check)
        FinKind.Invoice or FinKind.Insurance or FinKind.LoanInstalment => namedInSubject || hasMoney && strongFields >= 1 || strongFields >= 2,
        FinKind.Subscription => hasMoney && (namedInSubject || strongFields >= 1 || hasDate),
        FinKind.PaymentReminder or FinKind.DebtCollection => hasMoney || strongFields >= 1,
        FinKind.TaxNotice => hasMoney || namedInSubject || from.Contains("vero.fi", StringComparison.OrdinalIgnoreCase),
        FinKind.Refund => hasMoney && (namedInSubject || RefundLineRx.IsMatch(all)),
        FinKind.Receipt => hasMoney && (namedInSubject || TotalLineRx.IsMatch(all)),
        FinKind.PaymentConfirmation => hasMoney || strongFields >= 1,

        // A payslip shows its figures, or arrives as a document named like one (often a locked PDF)
        FinKind.Payslip => PaySlipFigureRx.IsMatch(all) || namedInSubject || files.Contains("palkka") || files.Contains("payslip") || files.Contains("pay slip") || files.Contains("salary"),
        FinKind.Salary => hasMoney && (namedInSubject || PaySlipFigureRx.IsMatch(all) || SalaryLineRx.IsMatch(all)),
        FinKind.OtherFinancial => namedInSubject,
        _ => false,
    };

    private static readonly Regex RefundLineRx = new(@"(?im)(refund|hyvitys|palautamme|money back)[^\n]{0,60}(€\s*\d|\d[\d\s.,]*\s*(€|eur\b|euroa?\b))|(€\s*\d|\d[\d\s.,]*\s*(€|eur\b))[^\n]{0,40}(refund|hyvitys)", RegexOptions.Compiled);
    private static readonly Regex TotalLineRx = new(@"(?im)\b(total|yhteensä|summa|amount paid|total paid|veloitettu|charged|loppusumma)\b[^\n\d]{0,30}\d", RegexOptions.Compiled);
    private static readonly Regex SalaryLineRx = new(@"(?im)(salary|palkka|palkkasi|wages|lön)[^\n]{0,60}(€\s*\d|\d[\d\s.,]*\s*(€|eur\b|euroa?\b))", RegexOptions.Compiled);

    /// <summary>The email without its footer and bare link lines.</summary>
    public static string StripBoilerplate(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Where(l => !BoilerplateRx.IsMatch(l)));

    // Specific kinds beat the generic ones when both appear: a reminder is also an "invoice"
    private static readonly FinKind[] Priority =
    [
        FinKind.DebtCollection, FinKind.PaymentReminder, FinKind.Payslip, FinKind.Salary, FinKind.TaxNotice, FinKind.Refund,
        FinKind.PaymentConfirmation, FinKind.Insurance, FinKind.LoanInstalment, FinKind.Subscription, FinKind.Receipt, FinKind.Invoice, FinKind.OtherFinancial,
    ];

    private static FinKind PickKind(Dictionary<FinKind, double> scores)
    {
        var best = scores.Max(s => s.Value);

        // Among kinds within reach of the best score, the most specific wins
        foreach (var k in Priority)
        {
            if (k != FinKind.OtherFinancial && scores.TryGetValue(k, out var s) && s >= best * 0.6)
            {
                return k;
            }
        }

        return FinKind.OtherFinancial;
    }

    private static bool MatchesTerm(string lower, string term) =>
        term.EndsWith('*')
            ? HasTerm(lower, term[..^1])
            : Regex.IsMatch(lower, $@"(?<![\p{{L}}\d]){Regex.Escape(term)}(?:s|es|'s)?(?![\p{{L}}\d])");

    /// <summary>The term at the start of a word: "lasku" finds "laskusi".</summary>
    internal static bool HasTerm(string lower, string term) =>
        Regex.IsMatch(lower, $@"(?<![\p{{L}}\d]){Regex.Escape(term)}");
}
