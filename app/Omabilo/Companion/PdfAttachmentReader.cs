// Reads text from PDF and plain-text attachments on the server, with PdfPig. The file is parsed as
// data only: nothing in it runs, and a broken or scanned file returns a plain reason instead of
// failing the whole message. Scanned PDFs need OCR (an optional second reader in the IKON layer).

using System.Text;
using UglyToad.PdfPig;

public sealed class PdfAttachmentReader : IAttachmentReader
{
    public const int MaxPages = 20;
    public const int MaxChars = 20_000;

    public Task<(string? Text, string? Problem)> ReadAsync(string fileName, string mimeType, byte[] data, CancellationToken ct)
    {
        if (mimeType == "text/plain" || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var text = Encoding.UTF8.GetString(data);
            return Task.FromResult<(string?, string?)>((Limit(text), null));
        }

        // A real PDF starts with "%PDF"; anything else named .pdf isn't trusted as one
        if (data.Length < 5 || Encoding.ASCII.GetString(data, 0, 4) != "%PDF")
        {
            return Task.FromResult<(string?, string?)>((null, "The file isn't a valid PDF."));
        }

        try
        {
            using var doc = PdfDocument.Open(data);

            if (doc.NumberOfPages > MaxPages)
            {
                return Task.FromResult<(string?, string?)>((null, $"The PDF has more than {MaxPages} pages, so it isn't read."));
            }

            var sb = new StringBuilder();

            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                // Words in reading order, a new line where the vertical position changes
                double? lastY = null;

                foreach (var word in page.GetWords())
                {
                    var y = Math.Round(word.BoundingBox.Bottom);

                    if (lastY is { } ly && Math.Abs(ly - y) > 2)
                    {
                        sb.Append('\n');
                    }
                    else if (lastY != null)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(word.Text);
                    lastY = y;
                }

                sb.Append('\n');

                if (sb.Length > MaxChars)
                {
                    break;
                }
            }

            var result = sb.ToString().Trim();
            return Task.FromResult<(string?, string?)>(result.Length < 20
                ? (null, "No readable text in this PDF. It may be a scanned image.")
                : (Limit(result), null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult<(string?, string?)>((null, "The PDF couldn't be read. It may be damaged or password-protected."));
        }
    }

    private static string Limit(string text) => text.Length > MaxChars ? text[..MaxChars] : text;
}
