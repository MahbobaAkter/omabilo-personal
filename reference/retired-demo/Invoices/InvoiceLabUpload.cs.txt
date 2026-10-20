// Financial Inbox user test, "Upload a PDF": anyone using the app can try how Omabilo reads a bill,
// receipt or payslip PDF without connecting Gmail. The file goes through the same PDF reader and
// rules as Gmail sync (PdfAttachmentReader, MessageClassifier, RecordExtractor, InvoiceAnalysis).
//
// Nothing from the file is stored: the upload's temp file is deleted as soon as it is read, the text
// lives only in this browser tab's state, and a feedback row keeps only which fields were found.

public partial class OmabiloApp
{
    public const long LabMaxUploadBytes = 10 * 1024 * 1024;

    private readonly ClientReactive<MailMessage?> _labUploadMail = new((MailMessage?)null);
    private readonly ClientReactive<bool> _labUploadReading = new(false);
    private readonly ClientReactive<string?> _labUploadError = new((string?)null);
    private readonly ClientReactive<LabUploadResult?> _labUploadResult = new((LabUploadResult?)null);

    private sealed record LabUploadResult(FinancialItem? Item, Classification Classification, MailAttachment Document);

    private static readonly PdfAttachmentReader LabPdfReader = new();

    private void RenderLabUpload(UIView view)
    {
        view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-3"], content: v =>
        {
            v.FileUpload(["rounded-xl border-2 border-dashed border-secondary bg-background hover:border-brand p-6 flex flex-col items-center gap-2 cursor-pointer text-center"],
                accept: [".pdf", "application/pdf"], maxFileSize: LabMaxUploadBytes, disabled: _labUploadReading.Value,
                ariaLabel: "Choose a PDF invoice, receipt or payslip",
                onUploadComplete: OnLabUploadCompleteAsync,
                onUploadError: async _ =>
                {
                    _labUploadReading.Value = false;
                    _labUploadError.Value = $"The file didn't upload. Use a PDF under {LabMaxUploadBytes / (1024 * 1024)} MB and try again.";
                },
                content: zone =>
                {
                    zone.Icon([Icon.Lg, "text-brand-secondary"], name: _labUploadReading.Value ? "loader-circle" : "file-up");
                    zone.Text([Text.BodyStrong, "text-foreground"], _labUploadReading.Value ? "Reading the PDF…" : "Choose a PDF, or drop it here");
                    zone.Text([Text.Caption, "text-muted-foreground"], $"An invoice, reminder, receipt or payslip. PDF only, up to {LabMaxUploadBytes / (1024 * 1024)} MB.");
                });

            if (_labUploadError.Value is { } error)
            {
                v.Alert("Couldn't use this file", tone: SemanticTone.Error, description: error, key: "upload-error");
            }

            if (_labUploadMail.Value is { } mail && mail.Attachments.FirstOrDefault() is { } doc)
            {
                v.Row(["items-center justify-between gap-2 flex-wrap rounded-xl border border-secondary p-3"], content: r =>
                {
                    r.Row(["items-center gap-2 min-w-0"], content: n =>
                    {
                        n.Icon([Icon.Sm, "text-muted-foreground"], name: doc.Text != null ? "file-check" : "file-x");
                        n.Text([Text.BodySm, "text-foreground font-semibold truncate"], doc.FileName);
                    });
                    var status = InvoiceAnalysis.StatusText(doc.Status);
                    r.Badge(char.ToUpperInvariant(status[0]) + status[1..], tone: doc.Text != null ? SemanticTone.Success : SemanticTone.Warning, size: BadgeSize.Sm);
                });

                var facts = new List<string> { $"{Math.Max(1, doc.Size / 1024)} KB" };
                if (doc.Pages > 0) facts.Add($"{doc.Pages} page{(doc.Pages == 1 ? "" : "s")}");
                if (doc.Text != null) facts.Add($"{doc.Text.Length:N0} characters of text read");
                v.Text([Text.Caption, "text-muted-foreground"], string.Join(" · ", facts));
                if (doc.Problem != null) v.Text([Text.BodySm, "text-foreground"], doc.Problem);
            }

            v.Row(["items-start gap-2 rounded-xl bg-muted p-3"], content: r =>
            {
                r.Icon([Icon.Sm, "text-muted-foreground mt-0.5 shrink-0"], name: "lock");
                r.Text([Text.Caption, "text-muted-foreground"],
                    "Please use a sample or fictional PDF. The file is read on Omabilo's server and deleted straight away; nothing from it is saved. Scanned PDFs (photos of paper) can't be read here, because this test uses no OCR or AI.");
            });
        });
    }

    private async Task OnLabUploadCompleteAsync(FileUploadCompleteArgs args)
    {
        _labUploadError.Value = null;
        _labUploadMail.Value = null;
        _labUploadResult.Value = null;
        ResetFeedback();

        if (args.LocalTempFilePath is not { } path)
        {
            _labUploadError.Value = "The file didn't arrive. Try uploading it again.";
            return;
        }

        _labUploadReading.Value = true;

        try
        {
            byte[] data;

            try
            {
                data = await File.ReadAllBytesAsync(path, args.CancellationToken);
            }
            finally
            {
                File.Delete(path);
            }

            var read = data.LongLength > LabMaxUploadBytes
                ? new AttachmentReadResult(AttachmentStatus.TooLarge, null, 0, "The file is too large to read.")
                : await LabPdfReader.ReadAsync(args.FileName, "application/pdf", data, args.CancellationToken);

            var doc = new MailAttachment(args.FileName, "application/pdf", data.LongLength, read.Text, read.Problem, read.Status, read.Pages);
            _labUploadMail.Value = new MailMessage("lab-upload", "lab-upload", "", Path.GetFileNameWithoutExtension(args.FileName),
                DateTime.UtcNow, "", "", [doc]);

            Log.Instance.Info($"Invoice lab upload: {read.Status}, {read.Pages} page(s)");
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Invoice lab upload failed: {ex.GetType().Name}");
            _labUploadError.Value = "Something went wrong while reading this file. Try again, or try another PDF.";
        }
        finally
        {
            _labUploadReading.Value = false;
        }
    }

    private void AnalyzeUpload()
    {
        _labResult.Value = null;
        _labUploadResult.Value = null;
        ResetFeedback();

        if (_labUploadMail.Value is not { } mail || mail.Attachments.FirstOrDefault() is not { } doc)
        {
            _labError.Value = "Upload a PDF first.";
            return;
        }

        if (doc.Text == null)
        {
            _labError.Value = $"Omabilo couldn't read text from “{doc.FileName}”. {doc.Problem ?? "Try another PDF."}";
            return;
        }

        try
        {
            _labError.Value = null;
            _labWarnings.Value = InvoiceExplainer.SensitiveWarnings(doc.Text);
            _labResultFrom.Value = "Uploaded PDF";

            var cls = MessageClassifier.Classify(mail.From, mail.Subject, RecordExtractor.FullText(mail));
            var item = cls.IsFinancial ? RecordExtractor.Extract("invoice-lab", mail, cls, Today) : null;
            _labUploadResult.Value = new LabUploadResult(item, cls, doc);
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Invoice lab analysis failed: {ex.GetType().Name}");
            _labError.Value = "Something went wrong while reading this document. Try again, or try one of the demo examples.";
        }
    }

    /// <summary>A one-line summary for the feedback table: the kind and which fields were found, never
    /// the values or the company, since an uploaded file may be a real document.</summary>
    private static string UploadSummary(LabUploadResult r)
    {
        if (r.Item is not { } i)
        {
            return "Uploaded PDF: not recognised as financial";
        }

        var found = i.FieldSources.Where(f => f.Field != "Currency").Select(f => f.Field.ToLowerInvariant()).ToList();
        return $"Uploaded PDF: {KindName(i.Kind).ToLowerInvariant()}; found {(found.Count == 0 ? "no fields" : string.Join(", ", found))}; {i.Advice?.Checks.Count ?? 0} check(s)";
    }

    private void RenderUploadResult(UIView view, LabUploadResult r)
    {
        view.Column(["gap-4 motion-[0:opacity-0_translate-y-[6px],100:opacity-100_translate-y-0] motion-duration-300ms"], key: "lab-upload-result", content: view =>
        {
            StepTitle(view, 3, "What Omabilo understood");

            if (_labWarnings.Value.Count > 0)
            {
                view.Alert("Please use fictional or sample documents", tone: SemanticTone.Warning,
                    description: $"{string.Join(" ", _labWarnings.Value)} Nothing from the file is saved, but please test with made-up details.");
            }

            if (r.Item is not { } item)
            {
                view.Alert("This doesn't look like a bill, receipt or payslip", tone: SemanticTone.Info, icon: "file-question", content: c =>
                    c.Column(["gap-1.5"], content: col =>
                    {
                        col.Text([Text.BodySm], $"Omabilo read {r.Document.Text?.Length ?? 0:N0} characters from “{r.Document.FileName}” but found too little to treat it as financial:");
                        foreach (var reason in r.Classification.Reasons) col.Text([Text.BodySm], $"• {reason}");
                        col.Text([Text.Caption, "text-muted-foreground pt-1"], "If it is a bill, tell us in the feedback below what Omabilo missed.");
                    }));
                return;
            }

            view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-3"], content: card =>
            {
                card.Row(["items-center gap-2 flex-wrap"], content: row =>
                {
                    row.Badge(KindName(item.Kind), tone: SemanticTone.Info);
                    row.Badge($"Read from the PDF · {SureWord(item.Confidence)}", tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                    row.Badge("No AI", tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                });
                card.Text([Text.Caption, "text-muted-foreground"], $"Why Omabilo thinks so: {string.Join("; ", item.ClassificationReasons)}.");
            });

            RenderAdvice(view, item);

            view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-1"], content: card =>
            {
                card.Heading("Details found in the document", style: [Text.H6, "m-0 text-foreground"], level: 4);
                card.Text([Text.Caption, "text-muted-foreground pb-2"],
                    "Each value says where it came from and how sure Omabilo is. A value the document doesn't state stays empty; nothing is made up.");
                RenderItemFields(card, item);
            });

            if (item.Missing.Count > 0)
            {
                view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-2"], content: card =>
                {
                    card.Heading("Missing or uncertain", style: [Text.H6, "m-0 text-foreground"], level: 4);
                    foreach (var m in item.Missing)
                    {
                        card.Row(["items-start gap-2"], content: row =>
                        {
                            row.Icon([Icon.Xs, "text-warning-600 dark:text-warning-400 mt-1 shrink-0"], name: "circle-dashed");
                            row.Text([Text.BodySm, "text-foreground/90"], m);
                        });
                    }
                });
            }

            view.Text([Text.Caption, "text-muted-foreground"],
                "Omabilo can't see your bank account, so it never assumes a bill is paid. Suggestions are not financial advice.");
        });
    }
}
