// "Upload a bill": a signed-in person adds a PDF bill, payslip or receipt straight to their own
// records. It goes through the same reader, classifier, extractor and filing as Gmail and forwarded
// mail, so the result waits for review like any other read document. Needs no mailbox access at all.
// Plain C# (no IKON types): who it belongs to is the signed-in user the caller passes in.

using System.Security.Cryptography;

public enum UploadOutcome { Filed, AlreadyFiled, NotFinancial, NotAPdf, TooLarge, Empty, Unreadable, Failed }

/// <summary>What happened to an upload; ItemId is the record it became (or already was), when there is one.
/// NewItems is 0 when the file only added to a record the person already had. Unread says why an
/// Unreadable file's text couldn't be read.</summary>
public sealed record UploadResult(UploadOutcome Outcome, string? ItemId = null, int NewItems = 0, AttachmentStatus? Unread = null);

public static class BillUpload
{
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>Marks an uploaded document's id, so it is never looked up in Gmail.</summary>
    public const string IdPrefix = "upload:";

    public static bool IsUploadId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>The same file always gets the same id, so uploading it twice files it once.</summary>
    public static string IdFor(byte[] data) => IdPrefix + Convert.ToHexString(SHA256.HashData(data))[..32].ToLowerInvariant();

    /// <summary>Checks the file itself, not just its name: a PDF starts with "%PDF-".</summary>
    public static UploadOutcome? Reject(string fileName, byte[] data)
    {
        if (data.Length == 0)
        {
            return UploadOutcome.Empty;
        }

        if (data.LongLength > MaxBytes)
        {
            return UploadOutcome.TooLarge;
        }

        var named = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        var header = data.Length >= 5 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F' && data[4] == '-';
        return named && header ? null : UploadOutcome.NotAPdf;
    }

    /// <summary>A file name safe to show and store: no folders, no control characters, a sensible length.</summary>
    public static string CleanFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();

        if (name.Length == 0)
        {
            return "document.pdf";
        }

        return name.Length <= 120 ? name : name[..116] + ".pdf";
    }

    /// <summary>True for documents that live outside Gmail (forwarded or uploaded): they can't be fetched again.</summary>
    public static bool NotInGmail(string id) => BillForwarding.IsForwardedId(id) || IsUploadId(id);
}
