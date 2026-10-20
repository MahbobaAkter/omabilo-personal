// Reads text from PDF and plain-text attachments on the server, with PdfPig. The file is parsed as
// data only: nothing in it runs, and a broken, locked or scanned file returns a status and a plain
// reason instead of failing the whole message. Scanned PDFs need OCR, which the sync engine adds
// when the person allowed it (IDocumentOcr).
//
// Invoices are laid out in columns and boxes, so words are put back into reading order line by line,
// and a row of labels above a row of values ("Due date   Reference" / "15.11.2026   12345")
// is also written out as "Due date: 15.11.2026" lines that the field readers understand.

using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;

public sealed class PdfAttachmentReader : IAttachmentReader
{
    public const int MaxPages = 10;
    public const int MaxChars = 20_000;

    /// <summary>Fewer letters than this on every page means the PDF is an image (a scan or a photo).</summary>
    public const int MinTextLetters = 20;

    public Task<AttachmentReadResult> ReadAsync(string fileName, string mimeType, byte[] data, CancellationToken ct)
    {
        if (IsPlainText(fileName, mimeType))
        {
            var text = Encoding.UTF8.GetString(data).Replace("\r\n", "\n");
            return Task.FromResult(text.Trim().Length == 0
                ? new AttachmentReadResult(AttachmentStatus.ScannedNotRead, null, 0, "The text file is empty.")
                : new AttachmentReadResult(AttachmentStatus.Read, Limit(text), 1, null));
        }

        if (!IsPdf(data))
        {
            return Task.FromResult(new AttachmentReadResult(AttachmentStatus.Damaged, null, 0, "The file isn't a valid PDF."));
        }

        return Task.FromResult(ReadPdf(data, ct));
    }

    public static bool IsPlainText(string fileName, string mimeType) =>
        mimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>A real PDF starts with "%PDF" (some writers put a few bytes first); the name alone isn't trusted.</summary>
    public static bool IsPdf(byte[] data)
    {
        var head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 1024));
        return data.Length >= 5 && head.Contains("%PDF", StringComparison.Ordinal);
    }

    private static AttachmentReadResult ReadPdf(byte[] data, CancellationToken ct)
    {
        try
        {
            using var doc = PdfDocument.Open(data);
            var pages = doc.NumberOfPages;
            var sb = new StringBuilder();

            for (var n = 1; n <= Math.Min(pages, MaxPages) && sb.Length < MaxChars; n++)
            {
                ct.ThrowIfCancellationRequested();
                sb.Append(PageText(doc.GetPage(n))).Append('\n');
            }

            var result = sb.ToString().Trim();

            if (result.Count(char.IsLetter) < MinTextLetters)
            {
                return new AttachmentReadResult(AttachmentStatus.ScannedNotRead, null, pages,
                    "This PDF has no text layer. It looks like a scanned image, so it needs OCR to be read.");
            }

            var note = pages > MaxPages ? $"Only the first {MaxPages} of {pages} pages were read." : null;
            return new AttachmentReadResult(AttachmentStatus.Read, Limit(result), pages, note);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new AttachmentReadResult(AttachmentStatus.PasswordProtected, null, 0,
                "The PDF is password-protected, so Omabilo can't read it. Open it yourself from Gmail.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AttachmentReadResult(AttachmentStatus.Damaged, null, 0, "The PDF couldn't be read. It may be damaged.");
        }
    }

    private sealed record Cell(double Left, double Right, string Text);

    /// <summary>One page as lines in reading order: top to bottom, left to right, wide gaps kept as
    /// cell breaks, and label rows paired with the value row under them.</summary>
    public static string PageText(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var rows = new List<List<Word>>();

        // Words whose baselines are within 3 points share a line
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var row = rows.LastOrDefault();

            if (row != null && Math.Abs(row[0].BoundingBox.Bottom - w.BoundingBox.Bottom) <= 3)
            {
                row.Add(w);
            }
            else
            {
                rows.Add([w]);
            }
        }

        var lines = rows.Select(ToCells).ToList();
        var sb = new StringBuilder();

        foreach (var cells in lines)
        {
            sb.Append(string.Join("   ", cells.Select(c => c.Text))).Append('\n');
        }

        var pairs = PairLabelRows(lines);

        if (pairs.Count > 0)
        {
            sb.Append('\n');
            foreach (var p in pairs) sb.Append(p).Append('\n');
        }

        return sb.ToString();
    }

    private static List<Cell> ToCells(List<Word> row)
    {
        var cells = new List<Cell>();
        Word? prev = null;
        var text = new StringBuilder();
        double left = 0, right = 0;

        foreach (var w in row.OrderBy(w => w.BoundingBox.Left))
        {
            if (prev != null)
            {
                var charWidth = prev.BoundingBox.Width / Math.Max(1, prev.Text.Length);
                var gap = w.BoundingBox.Left - prev.BoundingBox.Right;

                if (gap > Math.Max(8, charWidth * 2.5))
                {
                    cells.Add(new Cell(left, right, text.ToString()));
                    text.Clear();
                    prev = null;
                }
            }

            if (prev == null)
            {
                left = w.BoundingBox.Left;
            }
            else
            {
                text.Append(' ');
            }

            text.Append(w.Text);
            right = w.BoundingBox.Right;
            prev = w;
        }

        if (text.Length > 0)
        {
            cells.Add(new Cell(left, right, text.ToString()));
        }

        return cells;
    }

    /// <summary>"Label   Label" over "value   value", aligned by column: written as "Label: value".</summary>
    private static List<string> PairLabelRows(List<List<Cell>> lines)
    {
        var pairs = new List<string>();

        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var labels = lines[i];
            var values = lines[i + 1];

            if (labels.Count == 0 || labels.Count != values.Count
                || labels.Any(c => c.Text.Any(char.IsDigit) || c.Text.Length > 40)
                || values.Any(c => !c.Text.Any(char.IsDigit)))
            {
                continue;
            }

            var aligned = labels.Zip(values).All(p => Math.Abs(p.First.Left - p.Second.Left) < 25
                || (p.Second.Left < p.First.Right && p.Second.Right > p.First.Left));

            if (aligned)
            {
                pairs.AddRange(labels.Zip(values).Select(p => $"{p.First.Text.TrimEnd(':')}: {p.Second.Text}"));
            }
        }

        return pairs;
    }

    private static string Limit(string text) => text.Length > MaxChars ? text[..MaxChars] : text;
}
