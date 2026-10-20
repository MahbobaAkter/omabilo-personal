// Invoice sources: one interface per way invoices arrive. Plain C# so they can be tested.
//
// Phase 1 implements only what runs without outside credentials: manual entry, demo invoices,
// and SIMULATED forwarded emails and scanned documents built from fictional synthetic text.
// Gmail, Outlook and Finnish provider sources are declared with their contracts but are not
// connected; they report NotConnected with a reason the UI shows.

using System.Globalization;
using System.Text.RegularExpressions;

public enum SourceAvailability
{
    /// <summary>Works for real now.</summary>
    Available,

    /// <summary>Works now on fictional synthetic input only, clearly labelled.</summary>
    Simulated,

    /// <summary>Declared but not connected; needs a later phase or setup.</summary>
    NotConnected,
}

public interface IInvoiceSource
{
    InvoiceSourceType Type { get; }
    string Name { get; }
    SourceAvailability Availability { get; }

    /// <summary>One sentence for the UI explaining the availability.</summary>
    string Status { get; }
}

/// <summary>An email as a mailbox source sees it. Attachments carry text already extracted
/// from the document (Phase 1) or the stored file's location (later phases).</summary>
public record InboundEmail(
    string MessageId,
    string From,
    string Subject,
    DateTime ReceivedAt,
    string Body,
    IReadOnlyList<InboundAttachment> Attachments);

public record InboundAttachment(string FileName, string MimeType, string? ExtractedText, string? StoredAssetUri = null);

/// <summary>Reads invoice fields out of a document's text or image.</summary>
public interface IDocumentExtractor
{
    bool IsSimulated { get; }
    Task<ExtractedInvoiceFields> ExtractAsync(InboundAttachment document, CancellationToken ct = default);
}

/// <summary>A mailbox the owner connects (Gmail, Outlook). Contract only in Phase 1:
/// FetchSinceAsync returns messages that look like invoices, newest first, and must be
/// safe to call repeatedly because ingestion is idempotent on MessageId.</summary>
public interface IMailboxInvoiceSource : IInvoiceSource
{
    Task<IReadOnlyList<InboundEmail>> FetchSinceAsync(string ownerId, DateTime since, CancellationToken ct = default);
}

/// <summary>A Finnish e-invoice or biller integration (for example a Finvoice e-invoice feed).
/// Contract only in Phase 1.</summary>
public interface IProviderInvoiceSource : IInvoiceSource
{
    Task<IReadOnlyList<InvoiceSubmission>> FetchSinceAsync(string ownerId, DateTime since, CancellationToken ct = default);
}

// ── Available / simulated sources ───────────────────────────────────────────

public sealed class ManualInvoiceSource : IInvoiceSource
{
    public InvoiceSourceType Type => InvoiceSourceType.Manual;
    public string Name => "Manual entry";
    public SourceAvailability Availability => SourceAvailability.Available;
    public string Status => "Type in an invoice yourself.";

    /// <summary>Manual entries are what the owner typed, so they are fully confident.</summary>
    public static InvoiceSubmission Submit(ExtractedInvoiceFields fields, string entryId) =>
        new(InvoiceSourceType.Manual, $"manual:{entryId}", false, fields with
        {
            FieldConfidence = new Dictionary<string, double>
            {
                ["provider"] = 1, ["amount"] = 1, ["dueDate"] = 1, ["invoiceNumber"] = 1, ["reference"] = 1,
            },
        }, 1.0, "Typed in by you");
}

public sealed class SimulatedEmailSource : IInvoiceSource
{
    public InvoiceSourceType Type => InvoiceSourceType.ForwardedEmail;
    public string Name => "Forwarded email";
    public SourceAvailability Availability => SourceAvailability.Simulated;
    public string Status => "Simulated with fictional emails. A real forwarding address comes in Phase 2.";
}

public sealed class SimulatedDocumentSource : IInvoiceSource
{
    public InvoiceSourceType Type => InvoiceSourceType.Document;
    public string Name => "PDF or photo";
    public SourceAvailability Availability => SourceAvailability.Simulated;
    public string Status => "Simulated with fictional scanned invoices. Real uploads and AI reading come in Phase 2.";
}

public sealed class NotConnectedSource(InvoiceSourceType type, string name, string status) : IInvoiceSource
{
    public InvoiceSourceType Type => type;
    public string Name => name;
    public SourceAvailability Availability => SourceAvailability.NotConnected;
    public string Status => status;
}

public static class InvoiceSourceCatalog
{
    public static readonly IReadOnlyList<IInvoiceSource> All =
    [
        new ManualInvoiceSource(),
        new SimulatedEmailSource(),
        new SimulatedDocumentSource(),
        new NotConnectedSource(InvoiceSourceType.Gmail, "Gmail", "Not connected. Reading invoices from Gmail is planned for Phase 3 and needs your consent."),
        new NotConnectedSource(InvoiceSourceType.Outlook, "Microsoft Outlook", "Not connected. Reading invoices from Outlook is planned for Phase 3 and needs your consent."),
        new NotConnectedSource(InvoiceSourceType.FinnishProvider, "Finnish e-invoices", "Not connected. Finnish e-invoice and biller integrations are planned for Phase 4."),
    ];
}

// ── Text extraction (real logic, run on synthetic text in Phase 1) ──────────

/// <summary>Reads the common fields of a Finnish or English invoice from plain text.
/// Deterministic, so the simulated document source and the tests give the same answer.</summary>
public sealed class TextInvoiceExtractor : IDocumentExtractor
{
    public bool IsSimulated => true;

    public Task<ExtractedInvoiceFields> ExtractAsync(InboundAttachment document, CancellationToken ct = default) =>
        Task.FromResult(Extract(document.ExtractedText ?? ""));

    private static readonly Regex AmountRx = new(@"(?im)^(?:total|amount due|amount|yhteensä|maksettava|summa)\b[^\d\n]*([\d\s .]+[,.]\d{2})\s*(€|eur)?", RegexOptions.Compiled);
    private static readonly Regex DueRx = new(@"(?im)^(?:due date|due|eräpäivä|erapaiva)\b[^\d\n]*(\d{1,2}[./]\d{1,2}[./]\d{4}|\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"(?im)^(?:invoice date|date|päiväys|laskun päiväys)\b[^\d\n]*(\d{1,2}[./]\d{1,2}[./]\d{4}|\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
    private static readonly Regex NumberRx = new(@"(?im)^(?:invoice (?:no|number|#)|laskun numero|lasku nro)\b\.?[:\s#]*(?=[A-Z0-9-]*\d)([A-Z0-9][A-Z0-9-]{2,})", RegexOptions.Compiled);
    private static readonly Regex RefRx = new(@"(?im)^(?:reference|ref|viite|viitenumero)\b\.?[:\s]*((?:RF\d{2}[\s0-9A-Z]{1,30})|[\d ]{4,25})\s*$", RegexOptions.Compiled);
    private static readonly Regex ProviderRx = new(@"(?im)^(?:from|biller|seller|laskuttaja|myyjä)\b[:\s]*(.+)$", RegexOptions.Compiled);

    public static ExtractedInvoiceFields Extract(string text)
    {
        var confidence = new Dictionary<string, double>();

        string? Capture(Regex rx, string field, double conf)
        {
            var m = rx.Match(text);

            if (!m.Success)
            {
                confidence[field] = 0;
                return null;
            }

            confidence[field] = conf;
            return m.Groups[1].Value.Trim();
        }

        var provider = Capture(ProviderRx, "provider", 0.9);
        var number = Capture(NumberRx, "invoiceNumber", 0.9);
        var reference = Capture(RefRx, "reference", 0.8);
        var amountText = Capture(AmountRx, "amount", 0.85);
        var dueText = Capture(DueRx, "dueDate", 0.85);
        var dateText = Capture(DateRx, "invoiceDate", 0.8);

        // A reference whose check digit is right is very likely read correctly
        if (reference != null)
        {
            confidence["reference"] = PaymentReference.IsValid(reference) ? 0.99 : 0.4;
        }

        var amount = ParseAmount(amountText);
        if (amountText != null && amount == null) confidence["amount"] = 0;

        var due = ParseDate(dueText);
        if (dueText != null && due == null) confidence["dueDate"] = 0;

        return new ExtractedInvoiceFields(provider, number, reference, ParseDate(dateText), due, amount,
            text.Contains("€") || text.Contains("EUR", StringComparison.OrdinalIgnoreCase) ? "EUR" : null, confidence);
    }

    /// <summary>Overall confidence: the weakest of the fields an invoice cannot do without.</summary>
    public static double Overall(ExtractedInvoiceFields f) =>
        new[] { f.ConfidenceOf("provider"), f.ConfidenceOf("amount"), f.ConfidenceOf("dueDate") }.Min();

    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Replace(" ", "").Replace(" ", "");

        // "1.234,56" and "1234,56" are Finnish; "1,234.56" and "1234.56" are English
        if (t.Contains(',') && (!t.Contains('.') || t.LastIndexOf(',') > t.LastIndexOf('.')))
        {
            t = t.Replace(".", "").Replace(',', '.');
        }
        else
        {
            t = t.Replace(",", "");
        }

        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] formats = ["d.M.yyyy", "dd.MM.yyyy", "d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd"];
        return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)
            : null;
    }
}

/// <summary>Turns an email into submissions: one per readable attachment, or the body when
/// there is none. The message id plus the attachment name is the idempotency source ref.</summary>
public static class EmailInvoiceReader
{
    public static async Task<IReadOnlyList<InvoiceSubmission>> ReadAsync(InboundEmail email, IDocumentExtractor extractor, bool isSimulated, CancellationToken ct = default)
    {
        var parts = email.Attachments.Count > 0
            ? email.Attachments
            : [new InboundAttachment("body.txt", "text/plain", email.Body)];
        var result = new List<InvoiceSubmission>();

        foreach (var part in parts)
        {
            var fields = await extractor.ExtractAsync(part, ct);

            // The sender's display name fills in a provider the document didn't state
            if (string.IsNullOrWhiteSpace(fields.Provider))
            {
                var display = email.From.Split('<')[0].Trim().Trim('"');

                if (display.Length > 0)
                {
                    var conf = new Dictionary<string, double>(fields.FieldConfidence ?? new Dictionary<string, double>()) { ["provider"] = 0.6 };
                    fields = fields with { Provider = display, FieldConfidence = conf };
                }
            }

            result.Add(new InvoiceSubmission(InvoiceSourceType.ForwardedEmail, $"email:{email.MessageId}#{part.FileName}",
                isSimulated, fields, TextInvoiceExtractor.Overall(fields), $"Email “{email.Subject}”"));
        }

        return result;
    }
}
