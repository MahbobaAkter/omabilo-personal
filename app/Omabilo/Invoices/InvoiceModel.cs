// Canonical invoices and the source records they are built from.
// Plain C# with no IKON types, so the test project compiles this file directly.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>Where a source record came from.</summary>
public enum InvoiceSourceType
{
    Demo,
    Manual,
    ForwardedEmail,
    Document,
    Gmail,
    Outlook,
    FinnishProvider,
}

public enum InvoicePaymentStatus { Unpaid, Paid, Cancelled }

public enum InvoiceVerification { Unverified, Verified, Rejected }

/// <summary>Where an invoice is in its pipeline.</summary>
public enum InvoiceProcessingState
{
    /// <summary>Fields extracted but a person has not confirmed them.</summary>
    NeedsReview,

    /// <summary>Confirmed by the owner and counted in totals.</summary>
    Ready,

    /// <summary>Rejected by the owner (not an invoice, wrong data); never counted.</summary>
    Rejected,

    /// <summary>Folded into another invoice as a duplicate; never counted.</summary>
    Merged,
}

/// <summary>Fields read from one source, each with how sure the reader was (0 to 1).</summary>
public record ExtractedInvoiceFields(
    string? Provider,
    string? InvoiceNumber,
    string? Reference,
    DateTime? InvoiceDate,
    DateTime? DueDate,
    decimal? Amount,
    string? Currency,
    IReadOnlyDictionary<string, double>? FieldConfidence = null)
{
    public double ConfidenceOf(string field) =>
        FieldConfidence != null && FieldConfidence.TryGetValue(field, out var c) ? c : 0;
}

/// <summary>What a source hands to the invoice book. SourceRef is the source's own id
/// (a message id, a file hash, a provider document id) and makes ingestion idempotent.</summary>
public record InvoiceSubmission(
    InvoiceSourceType Type,
    string SourceRef,
    bool IsSimulated,
    ExtractedInvoiceFields Fields,
    double Confidence,
    string? SourceLabel = null);

/// <summary>One arrival of an invoice from one source, kept separate from the canonical
/// invoice so the same bill arriving twice is linked rather than counted twice.</summary>
public record InvoiceSourceRecord(
    string Id,
    string OwnerId,
    InvoiceSourceType Type,
    string SourceRef,
    string IdempotencyKey,
    bool IsSimulated,
    ExtractedInvoiceFields Fields,
    double Confidence,
    DateTime ReceivedAt,
    string? CanonicalInvoiceId,
    string? SourceLabel = null);

/// <summary>The one record per real-world invoice that totals, bills and reports count.</summary>
public record CanonicalInvoice(
    string Id,
    string OwnerId,
    string Provider,
    string? InvoiceNumber,
    string? Reference,
    DateTime? InvoiceDate,
    DateTime DueDate,
    decimal Amount,
    string Currency,
    InvoicePaymentStatus PaymentStatus,
    InvoiceVerification Verification,
    InvoiceProcessingState State,
    double Confidence,
    InvoiceSourceType PrimarySource,
    IReadOnlyList<string> SourceRecordIds,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? PossibleDuplicateOfId = null,
    string? MergedIntoId = null,
    string? LinkedBillId = null,
    DateTime? PaidAt = null)
{
    /// <summary>Only confirmed, unmerged invoices count toward money owed.</summary>
    public bool CountsInTotals => State == InvoiceProcessingState.Ready && Verification == InvoiceVerification.Verified;
}

/// <summary>Who did what to an invoice. Details name fields and states, never amounts,
/// document text or credentials, so the trail is safe to keep longer than the content.</summary>
public record InvoiceAuditEvent(
    string Id,
    string OwnerId,
    string? InvoiceId,
    string Action,
    string Actor,
    DateTime At,
    string Detail);

public static class InvoiceKeys
{
    /// <summary>Same owner, same source type, same source id → same key, whenever it arrives.</summary>
    public static string Idempotency(string ownerId, InvoiceSourceType type, string sourceRef)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{ownerId}\n{type}\n{sourceRef.Trim()}"));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }

    public static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    public static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "";
        }

        var words = provider.ToLowerInvariant()
            .Split([' ', ',', '.', '-', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("oy" or "oyj" or "ab" or "ltd" or "inc" or "ky" or "demo" or "gmbh" or "the"));
        return string.Concat(words);
    }

    public static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()).TrimStart('0');
}

/// <summary>Finnish payment references: the national reference (viitenumero, 7-3-1 check
/// digit) and the international RF creditor reference (ISO 11649).</summary>
public static class PaymentReference
{
    public static bool IsValid(string? reference)
    {
        var r = InvoiceKeys.NormalizeToken(reference);
        return r.StartsWith("RF", StringComparison.Ordinal) ? IsValidRf(r) : IsValidFinnish(r);
    }

    public static bool IsValidFinnish(string digits)
    {
        if (digits.Length is < 4 or > 20 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        int[] weights = [7, 3, 1];
        var body = digits[..^1];
        var sum = 0;

        for (var i = 0; i < body.Length; i++)
        {
            sum += (body[body.Length - 1 - i] - '0') * weights[i % 3];
        }

        var check = (10 - sum % 10) % 10;
        return check == digits[^1] - '0';
    }

    public static bool IsValidRf(string rf)
    {
        if (rf.Length is < 5 or > 25 || !rf.StartsWith("RF", StringComparison.Ordinal) || !char.IsAsciiDigit(rf[2]) || !char.IsAsciiDigit(rf[3]))
        {
            return false;
        }

        var rearranged = rf[4..] + rf[..4];
        var remainder = 0;

        foreach (var ch in rearranged)
        {
            var value = char.IsAsciiDigit(ch) ? ch - '0' : char.IsAsciiLetterUpper(ch) ? ch - 'A' + 10 : -1;

            if (value < 0)
            {
                return false;
            }

            foreach (var digit in value.ToString(CultureInfo.InvariantCulture))
            {
                remainder = (remainder * 10 + (digit - '0')) % 97;
            }
        }

        return remainder == 1;
    }

    /// <summary>Appends the 7-3-1 check digit to a reference body, for synthetic test data.</summary>
    public static string WithFinnishCheckDigit(string body)
    {
        int[] weights = [7, 3, 1];
        var sum = 0;

        for (var i = 0; i < body.Length; i++)
        {
            sum += (body[body.Length - 1 - i] - '0') * weights[i % 3];
        }

        return body + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
    }
}
