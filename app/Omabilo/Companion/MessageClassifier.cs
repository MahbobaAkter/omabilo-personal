// Rule-based classification of one email: financial or not, and which kind. Deterministic and
// explainable: every decision lists the words or patterns that led to it. AI, when the person turns
// it on, only explains; it never decides on its own what is counted.

using System.Text.RegularExpressions;

public static class MessageClassifier
{
    private record Rule(FinKind Kind, double Weight, string[] Terms);

    // English and Finnish. Terms match at word starts, so "lasku" also finds "laskusi"
    private static readonly Rule[] Rules =
    [
        new(FinKind.DebtCollection, 3, ["debt collection", "collection agency", "perintä", "perintätoimisto", "inkasso", "maksuhäiriö"]),
        new(FinKind.PaymentReminder, 3, ["payment reminder", "maksumuistutus", "muistutus laskusta", "overdue", "past due", "erääntynyt", "we have not received your payment", "have not received your payment"]),
        new(FinKind.Payslip, 3, ["palkkalaskelma", "palkkaerittely", "payslip", "pay slip", "salary statement", "wage statement", "earnings statement"]),
        new(FinKind.Salary, 2.5, ["salary has been paid", "salary payment", "palkka maksettu", "palkanmaksu", "palkkasi on maksettu", "your wages"]),
        new(FinKind.TaxNotice, 3, ["verohallinto", "vero.fi", "verotuspäätös", "tax decision", "veronpalautus", "jäännösvero", "tax return", "veroilmoitus", "residual tax", "tax refund"]),
        new(FinKind.Refund, 2.5, ["refund", "hyvitys", "palautus on käsitelty", "money back", "credit note", "hyvityslasku"]),
        new(FinKind.PaymentConfirmation, 2.5, ["payment received", "thank you for your payment", "kiitos maksustasi", "maksu vastaanotettu", "maksusi on vastaanotettu", "has been paid", "paid in full"]),
        new(FinKind.Receipt, 2, ["receipt", "kuitti", "order confirmation", "tilausvahvistus", "thank you for your order", "kiitos tilauksestasi", "your purchase", "ostoksesi", "purchase confirmation", "korttiosto", "card payment"]),
        new(FinKind.Subscription, 2, ["subscription", "tilauksesi uusitaan", "your plan renews", "renewal", "membership fee", "jäsenmaksu", "will renew"]),
        new(FinKind.Insurance, 2, ["vakuutusmaksu", "insurance premium", "vakuutuslasku"]),
        new(FinKind.LoanInstalment, 2, ["loan instalment", "loan installment", "lainan lyhennys", "lyhennyserä", "instalment", "osamaksu"]),
        new(FinKind.Invoice, 2, ["invoice", "lasku", "e-lasku", "bill", "amount due", "eräpäivä", "due date", "viitenumero", "maksettava", "to be paid"]),
        new(FinKind.OtherFinancial, 1, ["tiliote", "bank statement", "account statement", "credit card statement", "luottokortti", "pankki", "bank"]),
    ];

    private static readonly string[] MarketingTerms =
        ["% off", "sale ends", "shop now", "limited time", "newsletter", "uutiskirje", "tarjous", "ale ", "discount code", "black friday", "webinar", "free shipping"];

    private static readonly Regex MoneyRx = new(@"(?i)(?:€\s*\d|\d[\d\s.,]*\s*(?:€|eur\b|euroa?\b))", RegexOptions.Compiled);
    private static readonly Regex StrongInvoiceRx = new(@"(?im)^\s*(?:reference|viite|viitenumero|invoice number|laskun numero|due date|eräpäivä|amount due|maksettava|iban)\b", RegexOptions.Compiled);

    public static Classification Classify(string from, string subject, string text)
    {
        var all = $"{subject}\n{text}";
        var lower = all.ToLowerInvariant();
        var scores = new Dictionary<FinKind, double>();
        var reasons = new List<string>();

        foreach (var rule in Rules)
        {
            foreach (var term in rule.Terms)
            {
                if (HasTerm(lower, term))
                {
                    // Words in the subject count double: they say what the message is about
                    var weight = HasTerm(subject.ToLowerInvariant(), term) ? rule.Weight * 2 : rule.Weight;
                    scores[rule.Kind] = scores.GetValueOrDefault(rule.Kind) + weight;
                    reasons.Add($"mentions “{term}”");
                    break;
                }
            }
        }

        var hasMoney = MoneyRx.IsMatch(all);
        var strongFields = StrongInvoiceRx.Matches(text).Count;
        var marketing = MarketingTerms.Count(t => lower.Contains(t));

        if (hasMoney) reasons.Add("contains an amount of money");
        if (strongFields > 0) reasons.Add($"has {strongFields} invoice field{(strongFields == 1 ? "" : "s")} (reference, due date…)");

        // Advertising mentions prices too; without invoice fields it isn't a bill
        if (marketing >= 2 && strongFields == 0 && !scores.ContainsKey(FinKind.Payslip) && !scores.ContainsKey(FinKind.Receipt))
        {
            return new Classification(false, FinKind.NotFinancial, 0.9, ["looks like marketing (offers, discounts or a newsletter)"], "rules");
        }

        if (scores.Count == 0)
        {
            return new Classification(false, FinKind.NotFinancial, hasMoney ? 0.6 : 0.9,
                [hasMoney ? "mentions money but no bill, receipt, salary or tax words" : "no financial words found"], "rules");
        }

        var kind = PickKind(scores);
        var top = scores[kind];

        // A lone weak word ("bank", "renewal") with no money and no fields is not enough
        if (top < 2 && !hasMoney && strongFields == 0)
        {
            return new Classification(false, FinKind.NotFinancial, 0.6, [.. reasons, "too little evidence that it is financial"], "rules");
        }

        var confidence = Math.Min(0.97, 0.45 + top * 0.07 + (hasMoney ? 0.15 : 0) + Math.Min(strongFields, 3) * 0.06);
        return new Classification(true, kind, Math.Round(confidence, 2), reasons.Distinct().ToList(), "rules");
    }

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

    internal static bool HasTerm(string lower, string term) =>
        Regex.IsMatch(lower, $@"(?<![\p{{L}}\d]){Regex.Escape(term)}");
}
