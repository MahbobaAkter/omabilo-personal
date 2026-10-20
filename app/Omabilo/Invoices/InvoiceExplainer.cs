// Invoice explainer (Financial Inbox user test): reads one fictional invoice email and explains it
// in plain English. Deterministic rules on top of TextInvoiceExtractor: no AI and no network, so
// testers and the tests always get the same answer. It keeps what the message says (facts) apart
// from what Omabilo suggests, and it never calls a message a scam or a bill paid without evidence.
// Plain C# so the test project can compile it in.

using System.Globalization;
using System.Text.RegularExpressions;

public enum FieldSource { Found, Guessed, Missing }

public enum Urgency { Overdue, ActNow, Soon, NotUrgent, CheckFirst, Unclear }

/// <summary>One line of the analysis: its value, and whether the message stated it.</summary>
public record ExplainedField(string Label, string? Value, FieldSource Source, string? Note = null);

public record InvoiceExplanation(
    bool LooksLikeInvoice,
    IReadOnlyList<ExplainedField> Fields,
    decimal? Amount,
    DateTime? DueDate,
    bool IsReminder,
    bool SaysPaid,
    Urgency Urgency,
    string UrgencyLabel,
    string WhatIsThis,
    string WhyItWasSent,
    string Attention,
    string NextStep,
    string WhenToAct,
    IReadOnlyList<string> MissingOrUncertain,
    IReadOnlyList<string> CheckBeforePaying,
    IReadOnlyList<string> PayAttentionTo)
{
    public ExplainedField Field(string label) => Fields.First(f => f.Label == label);
}

public static class InvoiceExplainer
{
    public const int MinLength = 20;
    public const int MaxLength = 8000;

    private static readonly CultureInfo Fi = CultureInfo.GetCultureInfo("fi-FI");

    /// <summary>Why a pasted text can't be analysed, or null when it can.</summary>
    public static string? Validate(string? text)
    {
        var t = text?.Trim() ?? "";

        if (t.Length == 0)
        {
            return "Paste a fictional invoice email first, or pick one of the demo examples.";
        }

        if (t.Length < MinLength)
        {
            return "That's too short to be an invoice email. Paste the whole message, including the sender and amount.";
        }

        if (t.Length > MaxLength)
        {
            return $"That's longer than {MaxLength.ToString("N0", CultureInfo.InvariantCulture)} characters. Paste just one invoice email.";
        }

        return null;
    }

    private static readonly Regex HetuRx = new(@"\b\d{6}[-+A-FYXWVU]\d{3}[0-9A-Y]\b", RegexOptions.Compiled);
    private static readonly Regex IbanRx = new(@"\bFI\d{2}(?:\s?\d{4}){3}\s?\d{2}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CardRx = new(@"\b(?:\d{4}[ -]?){3}\d{4}\b", RegexOptions.Compiled);

    /// <summary>Reminders shown when pasted text looks like it holds real personal data. The text is
    /// never stored; this only asks the tester to use fictional content.</summary>
    public static IReadOnlyList<string> SensitiveWarnings(string text)
    {
        var list = new List<string>();

        if (HetuRx.IsMatch(text))
        {
            list.Add("It looks like it contains a Finnish personal identity code (henkilötunnus).");
        }

        if (CardRx.IsMatch(IbanRx.Replace(text, "")))
        {
            list.Add("It looks like it contains a payment card number.");
        }

        return list;
    }

    // ── Patterns ────────────────────────────────────────────────────────────

    private static readonly Regex BillerRx = new(@"(?im)^(?:biller|seller|company|laskuttaja|myyjä)\b[:\s]*(.+)$", RegexOptions.Compiled);
    private static readonly Regex FromRx = new(@"(?im)^from\s*:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex SubjectRx = new(@"(?im)^subject\s*:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex PurposeRx = new(@"(?im)^(?:for|description|regarding|product|tuote|selite)\s*:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex OriginalDueRx = new(@"(?im)^(?:original due date|alkuperäinen eräpäivä)\b[^\d\n]*(\d{1,2}[./]\d{1,2}[./]\d{4}|\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
    private static readonly Regex FeeRx = new(@"(?im)^(?:reminder fee|late fee|muistutusmaksu)\b[^\d\n]*([\d\s .]+[,.]\d{2})", RegexOptions.Compiled);
    private static readonly Regex MoneyRx = new(@"(?i)(?:€\s*(\d{1,3}(?:[ .]\d{3})*(?:[,.]\d{1,2})?|\d+(?:[,.]\d{1,2})?)|(\d{1,3}(?:[ .]\d{3})*(?:[,.]\d{1,2})?|\d+(?:[,.]\d{1,2})?)\s*(?:€|eur\b|euros?\b))", RegexOptions.Compiled);

    // Copying an email can join its lines; the field readers expect each label at a line start
    private static readonly Regex LabelsOnOwnLines = new(@"(?<!\b(?:original|new|previous|invoice|payment))[ \t]+(?=(?:amount due|total to pay|total|due date|reference|invoice number|biller|for|original due date|reminder fee|iban)\s*:)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LinkRx = new(@"(?i)\b(?:https?://|www\.)\S+", RegexOptions.Compiled);

    private static readonly string[] InvoiceWords = ["invoice", "bill", "lasku", "payment", "reminder", "maksu", "amount due", "due date", "eräpäivä"];
    private static readonly string[] ReminderWords = ["payment reminder", "reminder", "maksumuistutus", "muistutus", "overdue", "past due", "not received your payment", "have not received", "erääntynyt"];
    private static readonly string[] PaidWords = ["payment received", "thank you for your payment", "has been paid", "paid in full", "maksu vastaanotettu", "kiitos maksustasi"];
    private static readonly string[] CredentialWords = ["password", "salasana", "pin code", "banking codes", "online banking code", "verkkopankkitunnu", "card number", "cvv", "login details", "bank credentials"];
    private static readonly string[] PressureWords = ["within 24 hours", "immediately", "account will be closed", "account closure", "will be suspended", "final warning", "legal action today"];
    private static readonly HashSet<string> GenericSenderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "customer", "customers", "service", "services", "billing", "support", "no-reply", "noreply", "info", "team",
        "admin", "notification", "notifications", "payment", "payments", "department", "invoice", "invoices", "account", "accounts",
    };

    private record PurposeRule(string[] Words, string Purpose, string Why);

    private static readonly PurposeRule[] PurposeRules =
    [
        new(["electricity", "sähkö", "kwh"], "Electricity", "Electricity companies bill you regularly for the power you used in the previous period."),
        new(["broadband", "internet", "laajakaista"], "Internet connection", "Internet providers usually bill a fixed monthly fee for your connection."),
        new(["phone", "mobile", "puhelin", "liittymä"], "Phone plan", "Phone operators bill your plan every month."),
        new(["rent", "vuokra"], "Rent", "Rent is usually billed every month before the month starts."),
        new(["water", "vesi"], "Water", "Water is often billed a few times a year based on an estimate or a meter reading."),
        new(["insurance", "vakuutus"], "Insurance", "Insurance companies bill the premium for the coming period, often in instalments."),
        new(["gym", "fitness", "kuntosali"], "Gym membership", "Memberships are usually billed every month or for a fixed period in advance."),
        new(["dentist", "dental", "hammaslääkäri", "clinic", "doctor"], "Health care visit", "Clinics bill you after a visit for the part of the cost you pay yourself."),
        new(["parking", "pysäköinti"], "Parking", "Parking companies bill for a parking space or a parking event."),
        new(["library", "kirjasto"], "Library fees", "Libraries bill late-return or lost-item fees."),
        new(["tv licence", "streaming", "subscription", "tilaus"], "Subscription", "Subscriptions are billed for each period you keep them."),
    ];

    // ── Analysis ────────────────────────────────────────────────────────────

    public static InvoiceExplanation Explain(string text, DateTime today)
    {
        today = today.Date;
        text = LabelsOnOwnLines.Replace(text.Replace("\r\n", "\n"), "\n");
        var lower = text.ToLowerInvariant();
        var extracted = TextInvoiceExtractor.Extract(text);
        bool Has(IEnumerable<string> words) => words.Any(lower.Contains);

        var isReminder = Has(ReminderWords);
        var saysPaid = Has(PaidWords);
        var allAmounts = MoneyRx.Matches(text)
            .Select(m => TextInvoiceExtractor.ParseAmount(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value))
            .OfType<decimal>()
            .Distinct()
            .ToList();
        var looksLikeInvoice = allAmounts.Count > 0 || Has(InvoiceWords);

        // Sender: a stated biller first; the email's display name is only a guess
        ExplainedField sender;
        string? senderName = null;

        if (BillerRx.Match(text) is { Success: true } biller)
        {
            senderName = biller.Groups[1].Value.Trim();
            sender = new("Sender or company", senderName, FieldSource.Found);
        }
        else if (FromRx.Match(text) is { Success: true } from && DisplayName(from.Groups[1].Value) is { } display)
        {
            if (IsGenericName(display))
            {
                sender = new("Sender or company", null, FieldSource.Missing, $"The sender only calls itself “{display}”, which doesn't name a company.");
            }
            else
            {
                senderName = display;
                sender = new("Sender or company", display, FieldSource.Guessed, "Taken from the email's sender name. The message doesn't state the company separately.");
            }
        }
        else
        {
            sender = new("Sender or company", null, FieldSource.Missing, "The message doesn't say who sent it.");
        }

        // Purpose: a stated line first, then keywords in the subject and body
        ExplainedField purpose;
        string? purposeText = null;
        var rule = PurposeRules.FirstOrDefault(r => r.Words.Any(w => HasWord(lower, w)));

        if (PurposeRx.Match(text) is { Success: true } p)
        {
            purposeText = p.Groups[1].Value.Trim();
            purpose = new("What it's for", purposeText, FieldSource.Found);
        }
        else if (rule != null)
        {
            purposeText = rule.Purpose;
            purpose = new("What it's for", rule.Purpose, FieldSource.Guessed, "Worked out from words in the message.");
        }
        else
        {
            purpose = new("What it's for", null, FieldSource.Missing, "The message doesn't say what the payment is for.");
        }

        // Reference: Finnish references carry a check digit, so a wrong one is worth flagging
        var reference = extracted.Reference;
        var referenceValid = reference != null && PaymentReference.IsValid(reference);
        var referenceField = reference == null
            ? new ExplainedField("Reference number", null, FieldSource.Missing, "No payment reference (viitenumero). Without it the payment may not be matched to your bill.")
            : new ExplainedField("Reference number", reference, FieldSource.Found, referenceValid ? "The check digit is correct." : "The check digit doesn't add up, so it may be mistyped. Check it against the original bill.");

        var numberField = extracted.InvoiceNumber == null
            ? new ExplainedField("Invoice number", null, FieldSource.Missing)
            : new ExplainedField("Invoice number", extracted.InvoiceNumber, FieldSource.Found);

        // Amount: a labelled total is a fact; a single loose amount is a guess; several loose ones are unclear
        decimal? amount = null;
        ExplainedField amountField;

        if (extracted.Amount is { } labelled)
        {
            amount = labelled;
            amountField = new("Amount", Money(labelled), FieldSource.Found);
        }
        else if (allAmounts.Count == 1)
        {
            amount = allAmounts[0];
            amountField = new("Amount", Money(allAmounts[0]), FieldSource.Guessed, "The only amount in the message, but it isn't labelled as the amount to pay.");
        }
        else if (allAmounts.Count > 1)
        {
            amountField = new("Amount", null, FieldSource.Missing,
                $"The message mentions several amounts ({string.Join(", ", allAmounts.Select(Money))}) and doesn't say which one to pay.");
        }
        else
        {
            amountField = new("Amount", null, FieldSource.Missing, "No amount found.");
        }

        var dueDate = extracted.DueDate;
        var originalDue = OriginalDueRx.Match(text) is { Success: true } od ? TextInvoiceExtractor.ParseDate(od.Groups[1].Value) : null;
        var dueField = dueDate is { } due
            ? new ExplainedField("Due date", $"{Day(due)} ({Relative(due, today)})", FieldSource.Found,
                originalDue is { } o ? $"The original due date was {Day(o)} ({Relative(o, today)})." : null)
            : new ExplainedField("Due date", null, FieldSource.Missing, "No due date found.");

        var statusField = saysPaid
            ? new ExplainedField("Payment status", "Paid, according to the sender", FieldSource.Found, "The message says the payment was received.")
            : isReminder
                ? new ExplainedField("Payment status", "Not paid yet, according to the sender", FieldSource.Found,
                    "The sender says they haven't received the payment. If you paid very recently, the payment and this reminder may have crossed.")
                : new ExplainedField("Payment status", "Unknown", FieldSource.Missing,
                    "Omabilo can't see your bank account, so it doesn't know whether this is paid.");

        // What to double-check: signals worth caution, never a verdict on their own
        var caution = new List<string>();

        if (Has(CredentialWords))
        {
            caution.Add("It asks for passwords, banking codes or card details. Real billers never ask for these by email.");
        }

        if (LinkRx.IsMatch(text))
        {
            caution.Add("It asks you to use a link. It's safer to open your bank or the company's website yourself than to click.");
        }

        if (Has(PressureWords))
        {
            caution.Add("It pushes you to act very fast. Pressure like this is a common trick, so slow down and check first.");
        }

        if (reference != null && !referenceValid)
        {
            caution.Add("The reference number's check digit doesn't add up.");
        }

        var hasAccount = IbanRx.IsMatch(text) || lower.Contains("iban") || lower.Contains("tilinumero") || lower.Contains("bank account");

        var missing = new List<string>();
        if (senderName == null) missing.Add("Who sent it (company name)");
        if (purposeText == null) missing.Add("What the payment is for");
        if (amount == null) missing.Add(amountField.Note ?? "The amount to pay");
        else if (amountField.Source == FieldSource.Guessed) missing.Add("Which amount to pay is not clearly labelled");
        if (dueDate == null) missing.Add("A due date");
        if (reference == null) missing.Add("A payment reference number");
        if (!hasAccount && looksLikeInvoice && !saysPaid) missing.Add("A bank account (IBAN) to pay to");
        if (sender.Source == FieldSource.Guessed) missing.Add("The company name is guessed from the sender");

        var payAttention = new List<string>();

        if (FeeRx.Match(text) is { Success: true } fee && TextInvoiceExtractor.ParseAmount(fee.Groups[1].Value) is { } feeAmount)
        {
            payAttention.Add($"The total includes a reminder fee of {Money(feeAmount)}.");
        }

        if (isReminder && !saysPaid)
        {
            payAttention.Add("Paying late again can lead to more fees or, later, debt collection (perintä).");
        }

        if (amount != null && amountField.Source == FieldSource.Found && caution.Count == 0)
        {
            payAttention.Add("Check that the amount matches what you expected to pay.");
        }

        // Urgency
        var days = dueDate is { } dd ? (int)(dd - today).TotalDays : (int?)null;
        var (urgency, urgencyLabel) =
            !looksLikeInvoice ? (Urgency.Unclear, "Not an invoice")
            : caution.Count > 0 ? (Urgency.CheckFirst, "Check before you do anything")
            : saysPaid ? (Urgency.NotUrgent, "No action needed")
            : days < 0 ? (Urgency.Overdue, $"Overdue by {-days} day{(days == -1 ? "" : "s")}")
            : days <= 3 ? (Urgency.ActNow, days == 0 ? "Due today" : $"Due in {days} day{(days == 1 ? "" : "s")}")
            : days <= 14 ? (Urgency.Soon, $"Due in {days} days")
            : days != null ? (Urgency.NotUrgent, $"Due in {days} days")
            : (Urgency.Unclear, "Timing unclear");

        var who = senderName ?? "an unnamed sender";
        var forWhat = purposeText == null ? "something the message doesn't name"
            : purpose.Source == FieldSource.Found ? purposeText : purposeText.ToLowerInvariant();
        var amountText = amount is { } a ? Money(a) : "an unclear amount";

        var whatIsThis = !looksLikeInvoice
            ? "This doesn't look like an invoice. Omabilo found no amount to pay and no words like “invoice” or “bill”."
            : saysPaid
                ? $"A payment confirmation from {who}. They say your payment for {forWhat} was received."
                : isReminder
                    ? $"A payment reminder from {who} about {forWhat}. They say an earlier bill hasn't been paid, and now ask for {amountText}."
                    : senderName == null && purposeText == null
                        ? $"A message asking for {amountText}, but it doesn't clearly say who it's from or what it's for."
                        : $"An invoice from {who} for {forWhat}: {amountText}.";

        var why = !looksLikeInvoice
            ? "It doesn't appear to ask you for money."
            : isReminder && !saysPaid
                ? "Reminders are sent when a payment hasn't reached the sender by the due date. In Finland a reminder can add a small fee, and unpaid bills can later go to debt collection."
                : rule?.Why ?? (purposeText != null ? $"It's a bill for {forWhat}." : "The message doesn't explain why it was sent, so Omabilo can't tell.");

        var attention = caution.Count > 0
            ? caution[0]
            : urgency == Urgency.Overdue
                ? $"It's {urgencyLabel.ToLowerInvariant()}. Late payments can bring extra fees."
                : amount == null && looksLikeInvoice
                    ? "The amount to pay isn't clear."
                    : dueDate == null && looksLikeInvoice && !saysPaid
                        ? "There's no due date, so it's unclear when to pay."
                        : urgency == Urgency.ActNow
                            ? $"It's {urgencyLabel.ToLowerInvariant()}."
                            : "Nothing unusual found.";

        var refText = reference != null ? $" with reference {reference}" : "";
        var byText = dueDate is { } byDate ? $" by {Day(byDate)}" : "";

        var nextStep = !looksLikeInvoice
            ? "Nothing to pay based on this message."
            : caution.Count > 0
                ? $"Don't click links, reply or share any codes. If you think you might owe {(senderName ?? "someone")} money, contact them using details you already trust, like their official website or an earlier bill, not the ones in this message."
                : saysPaid
                    ? "Nothing to pay. You can keep the message as a receipt."
                    : amount == null || dueDate == null
                        ? $"Before paying, get the missing details from {who}, for example from an earlier bill or their customer service."
                        : isReminder || urgency == Urgency.Overdue
                            ? $"If you haven't paid yet, pay {amountText}{refText}{byText}. If you already paid, compare dates with your bank statement. If you can't pay in time, contact {who} and ask about a payment plan."
                            : $"Pay {amountText}{refText}{byText}, for example in your online bank. Omabilo doesn't pay bills for you.";

        var whenToAct = saysPaid
            ? "No action needed."
            : caution.Count > 0
                ? "Don't rush. Check first, then decide."
                : dueDate is { } w
                    ? $"By {Day(w)} ({Relative(w, today)})."
                    : "The message doesn't say.";

        var fields = new List<ExplainedField> { sender, purpose, referenceField, numberField, amountField, dueField, statusField };

        return new InvoiceExplanation(looksLikeInvoice, fields, amount, dueDate, isReminder, saysPaid, urgency, urgencyLabel,
            whatIsThis, why, attention, nextStep, whenToAct, missing, caution, payAttention);
    }

    // "Customer Service" or "Billing Team" says nothing about which company is asking
    private static bool IsGenericName(string name) =>
        name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).All(GenericSenderWords.Contains);

    // Word starts only, so "rent" finds "rental" but not "current"
    private static bool HasWord(string lower, string word) =>
        Regex.IsMatch(lower, $@"(?<![\p{{L}}\d]){Regex.Escape(word)}");

    private static string? DisplayName(string fromValue)
    {
        var name = fromValue.Split('<')[0].Trim().Trim('"').Trim();
        return name.Length == 0 || name.Contains('@') ? null : name;
    }

    public static string Money(decimal v) => $"{v.ToString("N2", Fi)} €";

    public static string Day(DateTime d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Relative(DateTime d, DateTime today)
    {
        var days = (int)(d.Date - today.Date).TotalDays;
        return days switch
        {
            < -1 => $"{-days} days ago",
            -1 => "yesterday",
            0 => "today",
            1 => "tomorrow",
            _ => $"in {days} days",
        };
    }
}

/// <summary>Fictional invoice emails for testers. Dates move with today so each one always shows its case.</summary>
public static class InvoiceTestScenarios
{
    public record Scenario(string Key, string Title, string Summary, string Text);

    private static string D(DateTime d) => d.ToString("d.M.yyyy", CultureInfo.InvariantCulture);

    public static IReadOnlyList<Scenario> All(DateTime today)
    {
        today = today.Date;
        var energyRef = PaymentReference.WithFinnishCheckDigit("2024118");
        var netRef = PaymentReference.WithFinnishCheckDigit("5531907");

        return
        [
            new("normal", "Electricity bill", "A clear, ordinary invoice",
                $"""
                From: Revontuli Energy <billing@revontuli-energy.example>
                Subject: Your electricity invoice for {today.AddMonths(-1):MMMM}

                Hello Maija,

                Thank you for being our customer. Here is your invoice for electricity used in {today.AddMonths(-1):MMMM}.

                Biller: Revontuli Energy Oy
                For: Electricity, {today.AddMonths(-1):MMMM} (182 kWh)
                Invoice number: RE-2026-48213
                Amount due: 64,20 €
                Due date: {D(today.AddDays(12))}
                Reference: {energyRef}
                IBAN: FI21 1234 5600 0007 85

                You can also pay with e-invoice in your online bank.
                Revontuli Energy customer service, Kaarnatie 4, 90100 Oulu
                """),
            new("reminder", "Payment reminder", "An unpaid bill that is now overdue",
                $"""
                From: Tähtiverkko Broadband <laskutus@tahtiverkko.example>
                Subject: Payment reminder: invoice TV-77120

                Hello,

                We have not received your payment for the invoice below. If you have already paid, please ignore this reminder.

                Biller: Tähtiverkko Oy
                For: Home broadband, 100 Mbit/s
                Invoice number: TV-77120
                Original due date: {D(today.AddDays(-12))}
                Original amount: 39,90 €
                Reminder fee: 5,00 €
                Total to pay: 44,90 €
                Due date: {D(today.AddDays(2))}
                Reference: {netRef}
                IBAN: FI49 5000 1520 0001 23

                If you can't pay by the due date, contact us to agree on a payment plan.
                Tähtiverkko Oy, Satamakatu 11, 33200 Tampere
                """),
            new("unclear", "Unclear payment request", "Missing details and unusual requests",
                """
                From: Customer Service <notice@secure-pay-update.example>
                Subject: Unpaid invoice – action required

                Dear customer,

                Our records show an unpaid invoice on your account. Please pay 89 € or 189 € immediately
                to avoid account closure within 24 hours.

                Pay now: http://pay-now-secure.example/invoice
                Have your online banking codes ready to confirm the payment.

                Thank you.
                """),
        ];
    }
}

/// <summary>One tester's answers. Only these answers and a short summary of the fictional result
/// are kept; the pasted message itself never is.</summary>
public record TesterFeedback(
    string Id,
    DateTime CreatedUtc,
    string TesterTag,
    string Scenario,
    string ResultSummary,
    int Usefulness,
    string ExplainedClearly,
    string? MissingOrIncorrect,
    string NextStepMadeSense,
    string? Confusing,
    string? WishedFeature,
    string WouldUse,
    string? Comments)
{
    public const int MaxTextLength = 2000;

    /// <summary>What still needs answering before the feedback can be sent, or empty when ready.</summary>
    public static IReadOnlyList<string> MissingAnswers(int usefulness, string clear, string nextStep, string wouldUse)
    {
        var list = new List<string>();
        if (usefulness is < 1 or > 5) list.Add("how useful it was (1 to 5)");
        if (string.IsNullOrEmpty(clear)) list.Add("whether it was explained clearly");
        if (string.IsNullOrEmpty(nextStep)) list.Add("whether the next step made sense");
        if (string.IsNullOrEmpty(wouldUse)) list.Add("whether you would use Omabilo");
        return list;
    }

    public static string? Clean(string? text)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > MaxTextLength ? t[..MaxTextLength] : t;
    }

    public string ToText() =>
        $"""
        Omabilo test feedback {Id} ({CreatedUtc:yyyy-MM-dd HH:mm} UTC)
        Example tested: {Scenario}
        Result shown: {ResultSummary}
        1. How useful (1-5): {Usefulness}
        2. Explained clearly: {ExplainedClearly}
        3. Missing or incorrect: {MissingOrIncorrect ?? "-"}
        4. Next step made sense: {NextStepMadeSense}
        5. Confusing: {Confusing ?? "-"}
        6. Most wanted feature: {WishedFeature ?? "-"}
        7. Would use in everyday life: {WouldUse}
        Other comments: {Comments ?? "-"}
        """;
}
