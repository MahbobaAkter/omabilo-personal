// "Upload a bill": a signed-in person adds a PDF to their own records, with no mailbox connection.
// The upload's temp file is read once and deleted; the sync engine files what it found for the
// signed-in user only, waiting for review. Logs carry ids and outcomes only.

public partial class OmabiloApp
{
    private readonly ClientReactive<bool> _upReading = new(false);
    private readonly ClientReactive<string?> _upError = new((string?)null);

    private void UploadBillCard(UIView view, bool compact)
    {
        var maxMb = BillUpload.MaxBytes / (1024 * 1024);

        view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-3"], content: v =>
        {
            v.Row(["items-center gap-2"], content: r =>
            {
                r.Icon([Icon.Sm, "text-brand-secondary"], name: "file-up");
                r.Text([Text.BodyStrong, "text-foreground"], "Upload a bill");
            });

            if (!compact)
            {
                v.Text([Text.BodySm, "text-foreground/90"],
                    "Add a PDF bill, payment reminder, payslip or receipt. Omabilo reads it, saves what it found to your inbox for you to check, and doesn't keep the file.");
            }

            v.FileUpload(["rounded-xl border-2 border-dashed border-secondary bg-background hover:border-brand p-5 flex flex-col items-center gap-2 cursor-pointer text-center"],
                accept: [".pdf", "application/pdf"], disabled: _upReading.Value || _myBusy.Value,
                ariaLabel: "Choose a PDF bill to save",
                // Checked on the server before any byte is sent, with no browser limit: the browser's limit drops
                // a large file without a message, and could be bypassed anyway. The platform fails an upload whose
                // bytes don't match the size claimed here
                onUploadPreStart: async args =>
                {
                    if (args.Size is > 0 and <= BillUpload.MaxBytes)
                    {
                        return true;
                    }

                    _upError.Value = args.Size > BillUpload.MaxBytes ? $"The file is larger than {maxMb} MB." : "The file is empty.";
                    return false;
                },
                onUploadComplete: OnBillUploadCompleteAsync,
                onUploadError: async _ =>
                {
                    _upReading.Value = false;
                    _upError.Value = $"The file didn't upload. Use a PDF under {maxMb} MB and try again.";
                },
                content: zone =>
                {
                    zone.Icon([Icon.Lg, "text-brand-secondary"], name: _upReading.Value ? "loader-circle" : "file-up");
                    zone.Text([Text.BodyStrong, "text-foreground"], _upReading.Value ? "Reading and saving…" : "Choose a PDF, or drop it here");
                    zone.Text([Text.Caption, "text-muted-foreground"], $"PDF only, up to {maxMb} MB. Scanned paper (photos) can't be read yet.");
                });

            if (_upError.Value is { } error)
            {
                v.Alert("Nothing was saved", tone: SemanticTone.Warning, description: error, key: "bill-upload-error", onDismiss: async () => _upError.Value = null);
            }
        });
    }

    private async Task OnBillUploadCompleteAsync(FileUploadCompleteArgs args)
    {
        _upError.Value = null;

        if (args.LocalTempFilePath is not { } path)
        {
            _upError.Value = "The file didn't arrive. Try uploading it again.";
            return;
        }

        if (MyUserId is not { } uid)
        {
            File.Delete(path);
            _upError.Value = "Sign in to save documents to your own account.";
            return;
        }

        _upReading.Value = true;

        try
        {
            byte[]? data = null;

            try
            {
                // Checked before reading, so an oversized file never fills memory
                if (new FileInfo(path).Length <= BillUpload.MaxBytes)
                {
                    data = await File.ReadAllBytesAsync(path, args.CancellationToken);
                }
            }
            finally
            {
                File.Delete(path);
            }

            // The engine logs the outcome with ids only
            var result = data == null ? new UploadResult(UploadOutcome.TooLarge) : await Engine.ImportUploadAsync(uid, args.FileName, data, CancellationToken.None);

            if (result.Outcome is UploadOutcome.Filed or UploadOutcome.AlreadyFiled)
            {
                await LoadMyAsync();
            }

            var saved = result.ItemId is { } itemId ? _my.Value?.Items.FirstOrDefault(i => i.Id == itemId) : null;
            var opened = saved != null;

            switch (result.Outcome)
            {
                case UploadOutcome.Filed when result.NewItems == 0:
                    _toasts.Success("This matches a record you already have, so Omabilo added the file to it. Check that record's details.");
                    break;
                case UploadOutcome.Filed:
                    _toasts.Success("Saved to your inbox, not yet checked by you. Look over what Omabilo read, then press Looks right or Correct details.");
                    break;
                case UploadOutcome.Unreadable:
                    _upError.Value = result.Unread switch
                    {
                        AttachmentStatus.PasswordProtected => "The PDF is password-protected, so Omabilo can't read it. Save a copy without a password and upload that.",
                        AttachmentStatus.Damaged => "The PDF couldn't be opened. It may be damaged. Download or save it again from the original and upload the new copy.",
                        AttachmentStatus.ScannedNotRead => "Omabilo found no text in this PDF. It may be a scan (a photo of paper), which Omabilo can't read yet.",
                        _ => "Omabilo couldn't read this PDF this time. Please try again.",
                    };
                    break;
                case UploadOutcome.AlreadyFiled:
                    _toasts.Success(opened ? "You've already uploaded this file. Here is what Omabilo saved from it." : "This file is already being saved.");
                    break;
                case UploadOutcome.NotFinancial:
                    _upError.Value = "Omabilo read the PDF but found no bill, reminder, payslip or receipt details in it. If it's a scan (a photo of paper), Omabilo can't read it yet.";
                    break;
                case UploadOutcome.NotAPdf:
                    _upError.Value = "That file isn't a PDF. Save the bill as a PDF and upload it again.";
                    break;
                case UploadOutcome.TooLarge:
                    _upError.Value = $"The file is larger than {BillUpload.MaxBytes / (1024 * 1024)} MB.";
                    break;
                case UploadOutcome.Empty:
                    _upError.Value = "The file is empty.";
                    break;
                default:
                    _upError.Value = "Something went wrong while reading this file. Please try again.";
                    break;
            }

            if (saved != null)
            {
                Go("my-inbox");
                OpenItem(saved);
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"bill upload failed: {ex.GetType().Name}");
            _upError.Value = "Something went wrong while reading this file. Please try again.";
        }
        finally
        {
            _upReading.Value = false;
        }
    }
}
