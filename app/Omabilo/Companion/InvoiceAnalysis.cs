// Reading a bill with its documents: who issued it, where every value came from, what disagrees,
// and plain-English advice. Deterministic rules, so the same email always gives the same answer and
// the tests can pin it down. Nothing is invented: a value no source states stays empty.

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>One readable part of an email: an attachment's text, the body, or the subject.</summary>
public sealed record TextSegment(string Source, string Text, double Weight, bool IsDocument);

/// <summary>Who issued a bill, where that came from and how sure it is. Name is null when no source says.</summary>
public sealed record IssuerResult(string? Name, string Source, double Confidence, string? Note);

public static class InvoiceAnalysis
{
    /// <summary>Bumped when the reading rules change, so items read by older rules are read again once.</summary>
    public const int CurrentVersion = 3;

    // ── Segments ────────────────────────────────────────────────────────────

    /// <summary>The documents first (an attached invoice is the authority), then the email text and subject.</summary>
    public static List<TextSegment> Segments(MailMessage mail)
    {
        var list = new List<TextSegment>();

        foreach (var a in mail.Attachments.Where(a => !string.IsNullOrWhiteSpace(a.Text)))
        {
            var kind = PdfAttachmentReader.IsPlainText(a.FileName, a.MimeType) ? "Text file" : a.MimeType.StartsWith("image/") ? "Image" : "PDF";
            list.Add(a.Status == AttachmentStatus.ReadByOcr
                ? new TextSegment($"{kind} “{a.FileName}” (read by OCR)", Clean(a.Text!), 0.85, true)
                : new TextSegment($"{kind} “{a.FileName}”", Clean(a.Text!), 1.0, true));
        }

        var body = mail.BodyText.Length > 0 ? mail.BodyText : mail.Snippet;
        if (body.Trim().Length > 0) list.Add(new TextSegment("Email text", Clean(body), 0.95, false));
        if (mail.Subject.Trim().Length > 0) list.Add(new TextSegment("Email subject", mail.Subject, 0.8, false));
        return list;
    }

    private static readonly Regex LinkRx = new(@"(?i)\b(?:https?://|www\.)\S+", RegexOptions.Compiled);

    /// <summary>The text a bill is read from: documents first, so their labelled values win. A company's
    /// web address printed on its own invoice isn't a request to click, so links in documents are dropped.</summary>
    public static string BillText(MailMessage mail) =>
        string.Join("\n\n", Segments(mail).Where(s => s.Source != "Email subject")
            .Select(s => s.IsDocument ? $"--- {s.Source} ---\n{LinkRx.Replace(s.Text, "")}" : s.Text));

    private static string Clean(string text) => text.Replace("\r\n", "\n");

    // ── Issuer ──────────────────────────────────────────────────────────────

    /// <summary>Free webmail domains: a bill sent or forwarded from one of these is from a person, not the biller.</summary>
    private static readonly string[] PersonalDomains =
    [
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "hotmail.fi", "live.com", "live.fi", "msn.com", "yahoo.com", "yahoo.co.uk",
        "icloud.com", "me.com", "mac.com", "aol.com", "proton.me", "protonmail.com", "pm.me", "gmx.com", "gmx.net", "mail.com", "zoho.com",
        "luukku.com", "suomi24.fi", "kolumbus.fi", "elisanet.fi", "welho.com", "saunalahti.fi", "dnainternet.net", "pp.inet.fi", "yandex.com",
    ];

    // "Biller: Kuura Energia Oy", or the Finnish bank-transfer box "Saaja / Mottagare   Kuura Energia Oy"
    private static readonly Regex IssuerLabelRx = new(
        @"(?im)^\s*(?:(?:biller|seller|issuer|issued by|supplier|vendor|company|merchant|payee|beneficiary|invoice from|bill from|laskuttaja|laskun lähettäjä|myyjä|toimittaja)\s*:|(?:saajan nimi|saaja)\b(?:\s*/?\s*mottagare\b)?\s*:?)\s*(.{2,80})$",
        RegexOptions.Compiled);

    private static readonly Regex ForwardedRx = new(
        @"(?is)(?:-{3,}\s*(?:forwarded message|original message|välitetty viesti|alkuperäinen viesti|vidarebefordrat meddelande)\s*-{3,}|begin forwarded message:)(.{0,600})",
        RegexOptions.Compiled);

    private static readonly Regex ForwardFromRx = new(@"(?im)^\s*(?:from|lähettäjä|från|fra)\s*:\s*(.+)$", RegexOptions.Compiled);

    // Short suffixes ("AS", "SE", "ry") only in their usual case, so ordinary words don't count
    private const string CompanySuffix = @"(?i:Oyj|Oy|Abp|Ltd|Limited|GmbH|Inc|LLC|plc|Corp|Corporation)|Ab|AB|Ky|KY|Tmi|ry|ASA|AS|A/S|ApS|S\.A\.|B\.V\.|N\.V\.|SE";

    private static readonly Regex CompanyCellRx = new(
        $@"^([\p{{Lu}}][\p{{L}}\d&'.\- ]{{1,60}}?\s(?:{CompanySuffix}))\.?(?=\s|$|,)",
        RegexOptions.Compiled);

    private static readonly Regex DocumentTitleRx = new(
        @"^(?:(?:e-|verkko)?lasku|faktura|invoice|kuitti|receipt|kvitto|maksumuistutus|muistutus|payment reminder|reminder|betalningspåminnelse|tilisiirto|giro)\b(?:\s*/\s*(?:faktura|invoice|räkning))?[\s:\-–]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BusinessIdRx =new(@"(?i)\b(?:y-tunnus|business id|vat (?:no|number|reg)|alv-?(?:nro|numero|rek)|org\.?\s?nr)\b", RegexOptions.Compiled);

    public static bool IsPersonalAddress(string from, string? ownerEmail)
    {
        var address = Address(from);

        if (address == null)
        {
            return false;
        }

        if (ownerEmail != null && address.Equals(ownerEmail, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var domain = address[(address.IndexOf('@') + 1)..];
        return PersonalDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }

    public static string? Address(string from)
    {
        var m = Regex.Match(from, @"[\w.+'-]+@[\w-]+(?:\.[\w-]+)+");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Who issued it, in order of trust: a labelled biller (document first), the original sender
    /// of a forwarded email, a company name printed on the document, then the sender's name, but never
    /// the name of a person's own or webmail account, which is what a forwarded bill arrives from.</summary>
    public static IssuerResult FindIssuer(MailMessage mail, string? ownerEmail)
    {
        var segments = Segments(mail);

        foreach (var s in segments.Where(s => s.Source != "Email subject"))
        {
            foreach (Match m in IssuerLabelRx.Matches(s.Text))
            {
                if (CleanName(m.Groups[1].Value) is { } name)
                {
                    return new IssuerResult(name, s.Source, Math.Round((s.IsDocument ? 0.9 : 0.85) * s.Weight, 2), "Stated as the biller.");
                }
            }
        }

        var body = mail.BodyText.Length > 0 ? mail.BodyText : mail.Snippet;

        if (ForwardedRx.Match(body) is { Success: true } fwd && ForwardFromRx.Match(fwd.Groups[1].Value) is { Success: true } original
            && !IsPersonalAddress(original.Groups[1].Value, ownerEmail))
        {
            var name = RecordExtractor.SenderName(original.Groups[1].Value);

            if (name != "Unknown sender")
            {
                return new IssuerResult(name, "Forwarded email's original sender", 0.75, "The email was forwarded; this is who sent it first.");
            }
        }

        foreach (var s in segments.Where(s => s.IsDocument))
        {
            if (CompanyOnDocument(s.Text) is { } company)
            {
                return new IssuerResult(company, s.Source, Math.Round(0.75 * s.Weight, 2), "The company name printed on the document.");
            }
        }

        if (!IsPersonalAddress(mail.From, ownerEmail))
        {
            var name = RecordExtractor.SenderName(mail.From);

            if (name != "Unknown sender")
            {
                return new IssuerResult(name, "Sender's name", 0.55, "Taken from the email's sender. The message doesn't state the company separately.");
            }
        }

        var why = IsPersonalAddress(mail.From, ownerEmail)
            ? "The email came from a personal address (not the company's), and no document names the company."
            : "Neither the email nor a document names the company.";
        return new IssuerResult(null, "Not found", 0, why);
    }

    /// <summary>A company name near the top of the document, or next to its business id.</summary>
    private static string? CompanyOnDocument(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // The line before or on a business id ("Y-tunnus 1234567-8") names the issuer
        for (var i = 0; i < lines.Count; i++)
        {
            if (BusinessIdRx.IsMatch(lines[i]))
            {
                foreach (var candidate in new[] { lines[i], i > 0 ? lines[i - 1] : "", i > 1 ? lines[i - 2] : "" })
                {
                    if (CompanyIn(candidate) is { } n) return n;
                }
            }
        }

        foreach (var line in lines.Take(25))
        {
            if (line.Contains('@') || line.Contains("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (CompanyIn(line) is { } n) return n;
        }

        return null;
    }

    /// <summary>A company name in one of a line's cells (the PDF reader separates columns with wide gaps).</summary>
    private static string? CompanyIn(string line)
    {
        foreach (var cell in Regex.Split(line, @"\s{3,}|\t"))
        {
            if (CompanyCellRx.Match(cell.Trim()) is { Success: true } c && CleanName(c.Groups[1].Value) is { } n) return n;
        }

        return null;
    }

    private static string? CleanName(string raw)
    {
        // A cell after the name ("Kuura Energia Oy   Y-tunnus …") is cut at the wide gap
        var name = Regex.Split(raw, @"\s{3,}|\t")[0].Trim().Trim('"', '\'', ',', ';', ':').Trim();
        name = Regex.Replace(name, @"^(?:mottagare|recipient)\s*[:\-]?\s*", "", RegexOptions.IgnoreCase).Trim();

        // A document's heading on the same line ("LASKU Kuura Energia Oy") isn't part of the name,
        // unless nothing but a company suffix would be left ("Lasku Oy")
        var titled = DocumentTitleRx.Match(name);
        if (titled.Success && name[titled.Length..].Trim() is var rest && rest.Contains(' '))
        {
            name = rest;
        }

        if (name.Length < 2 || name.Length > 80 || name.Count(char.IsLetter) < 2 || name.Contains('@') || Regex.IsMatch(name, @"^[A-Z]{2}\d{2}\s?\d"))
        {
            return null;
        }

        return name;
    }

    // ── IBAN ────────────────────────────────────────────────────────────────

    private static readonly Regex IbanRx = new(@"\b([A-Z]{2}\d{2}(?:\s?[A-Z0-9]{4}){2,7}(?:\s?[A-Z0-9]{1,4})?)\b", RegexOptions.Compiled);

    /// <summary>Every IBAN-shaped value in the text, with whether its check digits add up (ISO 13616, mod 97).</summary>
    public static List<(string Iban, bool Valid)> Ibans(string text)
    {
        var list = new List<(string, bool)>();

        foreach (Match m in IbanRx.Matches(text))
        {
            var iban = m.Groups[1].Value.Trim();
            var compact = iban.Replace(" ", "");
            if (compact.Length < 15 || compact.Length > 34 || list.Any(x => x.Item1.Replace(" ", "") == compact)) continue;
            list.Add((iban, IbanIsValid(compact)));
        }

        return list;
    }

    public static bool IbanIsValid(string iban)
    {
        var s = iban.Replace(" ", "").ToUpperInvariant();
        if (s.Length < 15 || !s.All(char.IsLetterOrDigit)) return false;

        var rearranged = s[4..] + s[..4];
        var remainder = 0;

        foreach (var ch in rearranged)
        {
            var digits = char.IsDigit(ch) ? ch.ToString() : (ch - 'A' + 10).ToString(CultureInfo.InvariantCulture);
            foreach (var d in digits) remainder = (remainder * 10 + (d - '0')) % 97;
        }

        return remainder == 1;
    }

    // ── Where each value came from ──────────────────────────────────────────

    public static string Day(DateTime d) => InvoiceExplainer.Day(d);

    private static IEnumerable<string> AmountForms(decimal v)
    {
        var inv = CultureInfo.InvariantCulture;
        yield return v.ToString("0.00", inv).Replace('.', ',');
        yield return v.ToString("#,0.00", inv).Replace(",", " ").Replace('.', ',').Replace(" ", "");
        yield return v.ToString("#,0.00", inv).Replace(",", "#").Replace('.', ',').Replace('#', '.');
        yield return v.ToString("0.00", inv);
        yield return v.ToString("#,0.00", inv);
    }

    private static IEnumerable<string> DateForms(DateTime d)
    {
        var inv = CultureInfo.InvariantCulture;
        yield return d.ToString("d.M.yyyy", inv);
        yield return d.ToString("dd.MM.yyyy", inv);
        yield return d.ToString("yyyy-MM-dd", inv);
        yield return d.ToString("d/M/yyyy", inv);
        yield return d.ToString("dd/MM/yyyy", inv);
        yield return d.ToString("d MMMM yyyy", inv);
    }

    private static string Squash(string s) => Regex.Replace(s, @"[\s  ]+", "").ToLowerInvariant();

    /// <summary>The first segment (documents first) that states this value, or null.</summary>
    private static TextSegment? Where(List<TextSegment> segments, IEnumerable<string> forms)
    {
        var wanted = forms.Select(Squash).Where(f => f.Length > 0).Distinct().ToList();
        return segments.FirstOrDefault(s =>
        {
            var squashed = Squash(s.Text);
            return wanted.Any(w => Regex.IsMatch(squashed, $@"(?<![\d]){Regex.Escape(w)}(?![\d])"));
        });
    }

    /// <summary>Records where each extracted field came from. A value read as "labelled" (the document
    /// says "Due date: …") is trusted more than one guessed from loose text.</summary>
    public static List<FieldProvenance> Provenance(FinancialItem item, MailMessage mail, IssuerResult issuer, ISet<string> guessed)
    {
        var segments = Segments(mail);
        var list = new List<FieldProvenance>();

        void Add(string field, string? value, IEnumerable<string> forms, string? note = null)
        {
            if (value == null) return;
            var labelled = !guessed.Contains(field);
            var seg = Where(segments, forms);

            if (seg == null)
            {
                list.Add(new FieldProvenance(field, value, "Worked out by Omabilo", 0.5, note ?? "Not stated word for word in the email or its documents."));
                return;
            }

            var conf = (labelled ? 0.92 : 0.6) * seg.Weight;
            list.Add(new FieldProvenance(field, value, seg.Source, Math.Round(conf, 2), note ?? (labelled ? null : "Not clearly labelled, so it's a best guess.")));
        }

        if (issuer.Name != null && item.Counterparty == issuer.Name)
        {
            list.Add(new FieldProvenance("Company", issuer.Name, issuer.Source, issuer.Confidence, issuer.Note));
        }

        if (item.Amount is { } amount) Add("Amount", InvoiceExplainer.Money(amount), AmountForms(amount));
        if (item.DueDate is { } due) Add("Due date", Day(due), DateForms(due));
        if (item.InvoiceDate is { } issued) Add("Invoice date", Day(issued), DateForms(issued));
        if (item.InvoiceNumber is { } number) Add("Invoice number", number, [number]);
        if (item.Reference is { } reference) Add("Reference", reference, [reference]);
        if (item.Iban is { } iban) Add("IBAN", iban, [iban]);
        if (item.Vat is { } vat) Add("VAT", InvoiceExplainer.Money(vat), AmountForms(vat));
        if (item.Purpose is { } purpose) Add("What it's for", purpose, [purpose], guessed.Contains("What it's for") ? "Worked out from words in the message." : null);
        if (item.Gross is { } gross) Add("Gross salary", InvoiceExplainer.Money(gross), AmountForms(gross));
        if (item.Net is { } net) Add("Net salary", InvoiceExplainer.Money(net), AmountForms(net));
        if (item.EventDate is { } ev && !item.IsBill) Add(item.IsIncome ? "Pay date" : "Date", Day(ev), DateForms(ev));

        var currencySegment = segments.FirstOrDefault(s => RecordExtractor.DetectCurrency(s.Text) != null);
        list.Add(currencySegment != null
            ? new FieldProvenance("Currency", item.Currency, currencySegment.Source, Math.Round(0.95 * currencySegment.Weight, 2))
            : new FieldProvenance("Currency", item.Currency, "Assumed", 0.4, "No currency is stated, so euros are assumed."));

        return list;
    }

    /// <summary>Values that two parts of the email give differently: worth checking before paying.</summary>
    public static List<string> Conflicts(MailMessage mail)
    {
        var flags = new List<string>();
        var parts = Segments(mail).Where(s => s.Source != "Email subject").ToList();
        var docs = parts.Where(p => p.IsDocument).ToList();
        var body = parts.FirstOrDefault(p => !p.IsDocument);

        if (docs.Count == 0 || body == null)
        {
            return flags;
        }

        foreach (var doc in docs)
        {
            var d = TextInvoiceExtractor.Extract(doc.Text);
            var b = TextInvoiceExtractor.Extract(body.Text);

            if (d.Amount is { } da && b.Amount is { } ba && da != ba)
            {
                flags.Add($"The email text says {InvoiceExplainer.Money(ba)}, but the {doc.Source} says {InvoiceExplainer.Money(da)}. Omabilo uses the document's amount; check which is right.");
            }

            if (d.DueDate is { } dd && b.DueDate is { } bd && dd != bd)
            {
                flags.Add($"The email text gives the due date {Day(bd)}, but the {doc.Source} says {Day(dd)}. Omabilo uses the document's date; check which is right.");
            }
        }

        var accounts = parts.SelectMany(p => Ibans(p.Text).Where(i => i.Valid).Select(i => i.Iban.Replace(" ", ""))).Distinct().ToList();

        if (accounts.Count > 1)
        {
            flags.Add($"The email and its documents give {accounts.Count} different bank accounts. Pay only to an account you can confirm with the company.");
        }

        return flags;
    }

    // ── Advice ──────────────────────────────────────────────────────────────

    private static string KindPhrase(FinKind k) => k switch
    {
        FinKind.PaymentReminder => "a payment reminder",
        FinKind.DebtCollection => "a debt collection notice",
        FinKind.TaxNotice => "a tax notice",
        FinKind.Subscription => "a subscription charge",
        FinKind.Insurance => "an insurance bill",
        FinKind.LoanInstalment => "a loan instalment",
        _ => "an invoice",
    };

    private static string Relative(DateTime due, DateTime today)
    {
        var days = (int)(due.Date - today.Date).TotalDays;
        return days switch
        {
            0 => "today",
            1 => "tomorrow",
            -1 => "yesterday",
            > 0 => $"in {days} days",
            _ => $"{-days} days ago",
        };
    }

    /// <summary>What a bill is, how much and when, where it stands, what to do next and what to check.
    /// Rule-based: it says so, and an AI explanation (when on) is shown separately.</summary>
    public static ItemAdvice? Advise(FinancialItem item, DateTime today)
    {
        if (!item.IsBill)
        {
            return null;
        }

        today = today.Date;
        var status = ItemRules.StatusOf(item, today);
        var issuer = item.Counterparty;
        var kind = KindPhrase(item.Kind);
        var forWhat = item.Purpose is { Length: > 1 } p ? $" for {(char.IsUpper(p[0]) && !char.IsUpper(p[1]) ? char.ToLowerInvariant(p[0]) + p[1..] : p)}" : "";
        var read = item.Attachments.Where(a => a.WasRead).ToList();
        var unread = item.Attachments.Where(a => !a.WasRead && a.Status != AttachmentStatus.NotSupported).ToList();

        var who = issuer != null
            ? $"{issuer} sent you {kind}{forWhat}."
            : $"This is {kind}{forWhat}, but Omabilo couldn't tell which company issued it.";

        if (read.Count == 0 && unread.Count > 0 && item.Amount == null && issuer == null)
        {
            who = $"This may be a bill, but its document “{unread[0].FileName}” couldn't be read, so Omabilo doesn't know who sent it or what it's for.";
        }
        else if (item.Purpose == null)
        {
            who += " It doesn't clearly say what it's for.";
        }
        if (read.Count > 0) who += $" Omabilo read the attached {(read.Count == 1 ? $"document “{read[0].FileName}”" : $"{read.Count} documents")}.";

        var money = item.Amount is { } a ? (item.Currency == "EUR" ? InvoiceExplainer.Money(a) : $"{a:0.00} {item.Currency}") : null;
        var when = (money, item.DueDate) switch
        {
            ({ } m, { } d) => $"{m} is due on {Day(d)} ({Relative(d, today)}).",
            ({ } m, null) => $"{m} to pay, but no due date was found.",
            (null, { } d) => $"Due on {Day(d)} ({Relative(d, today)}), but the amount isn't clear.",
            _ => "Omabilo couldn't find the amount or the due date.",
        };

        var situation = status.Status switch
        {
            DisplayStatus.Overdue => "Overdue: the due date has passed and no payment is recorded. Omabilo can't see your bank, so check whether you already paid.",
            DisplayStatus.DueSoon => $"Due soon: {status.Why}",
            DisplayStatus.Upcoming => $"Upcoming: {status.Why}",
            DisplayStatus.ReportedPaid => "You marked this as paid.",
            DisplayStatus.ConfirmedPaid => $"Paid: {status.Why}",
            DisplayStatus.Disputed => "You marked this as disputed.",
            DisplayStatus.NotApplicable => "You marked this as not applicable.",
            _ => $"Uncertain: {status.Why}",
        };

        var steps = new List<string>();
        var payTo = string.Join(", ", new[]
        {
            item.Iban is { } iban ? $"to account {iban}" : null,
            item.Reference is { } r ? $"with reference {r}" : null,
        }.OfType<string>());

        var doubtful = item.Suspicious || item.Flags.Any(f => f.Contains("bank account", StringComparison.OrdinalIgnoreCase) || f.Contains("check digits", StringComparison.OrdinalIgnoreCase));

        if (status.Status is DisplayStatus.ReportedPaid or DisplayStatus.ConfirmedPaid)
        {
            steps.Add("Nothing to pay. Keep the document in case the company asks for proof.");
        }
        else if (status.Status is DisplayStatus.Disputed or DisplayStatus.NotApplicable)
        {
            steps.Add($"Keep any reply from {issuer ?? "the company"} until the matter is settled.");
        }
        else if (doubtful)
        {
            steps.Add($"Don't pay yet. Contact {issuer ?? "the company"} using contact details you already trust (not the ones in this email) and confirm the bill is real.");
        }
        else if (money == null || item.DueDate == null || issuer == null)
        {
            steps.Add("Open the original email or document and check the missing details before you pay.");
            if (money != null && item.DueDate != null) steps.Add($"If it's right, pay {money} by {Day(item.DueDate.Value)}{(payTo.Length > 0 ? " " + payTo : "")}.");
        }
        else if (status.Status == DisplayStatus.Overdue)
        {
            steps.Add($"If you haven't paid, pay {money} as soon as you can{(payTo.Length > 0 ? " " + payTo : "")}. Late payments can add reminder fees and interest.");
            steps.Add($"If you can't pay now, contact {issuer} before it goes to debt collection and ask for a new due date or a payment plan.");
        }
        else
        {
            steps.Add($"Pay {money} by {Day(item.DueDate!.Value)}{(payTo.Length > 0 ? " " + payTo : "")}. You can schedule it in your bank now.");
        }

        if (status.Status is not (DisplayStatus.ReportedPaid or DisplayStatus.ConfirmedPaid or DisplayStatus.Disputed or DisplayStatus.NotApplicable) && !doubtful && money != null)
        {
            steps.Add("After paying, press “I've paid this” so Omabilo stops counting it.");
        }

        if (item.Kind == FinKind.DebtCollection)
        {
            steps.Add("Compare it with the original bill. If the debt is wrong, dispute it in writing with the collection agency.");
        }

        var checks = new List<string>();
        foreach (var u in unread) checks.Add($"The attachment “{u.FileName}” couldn't be read ({u.Problem ?? StatusText(u.Status)}). Details come from the email text only.");
        checks.AddRange(item.Flags);
        checks.AddRange(item.Missing.Where(m => !m.Contains("couldn't be read")));
        if (item.FieldSources.FirstOrDefault(f => f.Field == "Company") is { Confidence: < 0.7 } weak) checks.Add($"The company name is a best guess ({weak.Source.ToLowerInvariant()}).");

        return new ItemAdvice(who, when, situation, steps, checks.Distinct().ToList(),
            "Omabilo's own rules, from the email and its documents. No AI was used for this advice.", DateTime.UtcNow);
    }

    public static string StatusText(AttachmentStatus s) => s switch
    {
        AttachmentStatus.Read => "read",
        AttachmentStatus.ReadByOcr => "read by OCR",
        AttachmentStatus.ScannedNotRead => "scanned image, not read",
        AttachmentStatus.PasswordProtected => "password-protected",
        AttachmentStatus.Damaged => "damaged or not a real PDF",
        AttachmentStatus.TooLarge => "too large",
        AttachmentStatus.NotSupported => "file type not read",
        AttachmentStatus.DownloadFailed => "download failed",
        AttachmentStatus.TimedOut => "took too long",
        _ => s.ToString(),
    };
}
