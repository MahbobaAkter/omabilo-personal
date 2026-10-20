// "Add a bill": a signed-in person adds a PDF, a photo or pasted text to their own records, with no
// mailbox connection. Photos are read with OCR, which runs only when the person has turned on AI reading.
// The upload's temp file is read once and deleted; the sync engine files what it found for the
// signed-in user only, waiting for review. Logs carry ids and outcomes only.

public partial class OmabiloApp
{
    private readonly ClientReactive<bool> _upReading = new(false);
    private readonly ClientReactive<string?> _upError = new((string?)null);
    private readonly ClientReactive<bool> _upPasting = new(false);
    private readonly UserReactive<string> _upPasted = new("");

    /// <param name="allowPaste">False on Home: pasting text stays on Financial Inbox and Invoices.</param>
    private void UploadBillCard(UIView view, bool compact, bool allowPaste = true)
    {
        var maxMb = BillUpload.MaxBytes / (1024 * 1024);

        view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-3"], content: v =>
        {
            v.Row(["items-center gap-2"], content: r =>
            {
                r.Icon([Icon.Sm, "text-brand-secondary"], name: "file-up");
                r.Text([Text.BodyStrong, "text-foreground"], "Add a bill");
            });

            if (!compact)
            {
                v.Text([Text.BodySm, "text-foreground/90"],
                    "Add a bill, payment reminder, payslip or receipt as a PDF, a photo or pasted text. Omabilo reads it, saves what it found to your inbox for you to check, and doesn't keep the file.");
            }

            v.FileUpload(["rounded-xl border-2 border-dashed border-secondary bg-background hover:border-brand p-5 flex flex-col items-center gap-2 cursor-pointer text-center"],
                accept: [".pdf", "application/pdf", ".jpg", ".jpeg", ".png", "image/jpeg", "image/png"], disabled: _upReading.Value || _myBusy.Value,
                ariaLabel: "Choose a PDF or photo of a bill to save",
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
                    _upError.Value = $"The file didn't upload. Use a PDF or photo under {maxMb} MB and try again.";
                },
                content: zone =>
                {
                    zone.Icon([Icon.Lg, "text-brand-secondary"], name: _upReading.Value ? "loader-circle" : "file-up");
                    zone.Text([Text.BodyStrong, "text-foreground"], _upReading.Value ? "Reading and saving…" : IsPremium() ? "Choose a PDF or photo, or drop it here" : "Choose a PDF, or drop it here");
                    zone.Text([Text.Caption, "text-muted-foreground"], AiReadingOn()
                        ? $"PDF, JPG or PNG, up to {maxMb} MB. Photos and scans are read with OCR."
                        : IsPremium()
                            ? $"PDF, JPG or PNG, up to {maxMb} MB. Photos and scans need AI reading, which you can turn on in Plan & privacy."
                            : allowPaste ? $"PDF up to {maxMb} MB, or paste the bill's text. Reading photos and scans is part of Premium." : $"PDF up to {maxMb} MB. Reading photos and scans is part of Premium.");
                });

            if (!allowPaste)
            {
                v.Text([Text.Caption, "text-muted-foreground"], "To paste a bill's text instead, use Financial Inbox or Invoices.");
            }
            else if (_upPasting.Value)
            {
                v.TextArea(["default", "min-h-36"], bind: _upPasted, label: "Bill text", placeholder: "Paste the text of a bill or payment reminder, for example from an email or an e-invoice",
                    disabled: _upReading.Value);
                v.Row(["gap-2 flex-wrap"], content: r =>
                {
                    r.Button([Button.PrimaryMd], text: _upReading.Value ? "Reading…" : "Read and save", icon: "file-check",
                        disabled: _upReading.Value || _myBusy.Value || string.IsNullOrWhiteSpace(_upPasted.Value), onClick: OnBillPastedAsync);
                    r.Button([Button.GhostMd], text: "Cancel", disabled: _upReading.Value, onClick: () => _upPasting.Value = false);
                });
                v.Text([Text.Caption, "text-muted-foreground"], $"Up to {BillUpload.MaxPastedChars:N0} characters. The text is kept with the record so you can check what was read; deleting the record removes it.");
            }
            else
            {
                v.Button([Button.OutlineSm, "self-start"], text: "Paste bill text instead", icon: "clipboard-paste", disabled: _upReading.Value || _myBusy.Value,
                    onClick: () => { _upError.Value = null; _upPasting.Value = true; });
            }

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
            var result = data == null ? new UploadResult(UploadOutcome.TooLarge) : await Engine.ImportUploadAsync(uid, args.FileName, data, CancellationToken.None, allowAi: AiReadingOn());
            await ShowAddedAsync(result, pasted: false);
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

    // The free plan never sends a bill to OCR or AI, whatever an older saved setting says
    private bool AiReadingOn() => _my.Value?.Settings.AiEnabled == true && IsPremium();

    private async Task OnBillPastedAsync()
    {
        _upError.Value = null;

        if (MyUserId is not { } uid)
        {
            _upError.Value = "Sign in to save documents to your own account.";
            return;
        }

        _upReading.Value = true;

        try
        {
            var result = await Engine.ImportPastedTextAsync(uid, _upPasted.Value, CancellationToken.None, allowAi: AiReadingOn());

            if (result.Outcome is UploadOutcome.Filed or UploadOutcome.AlreadyFiled)
            {
                _upPasted.Value = "";
                _upPasting.Value = false;
            }

            await ShowAddedAsync(result, pasted: true);
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"bill paste failed: {ex.GetType().Name}");
            _upError.Value = "Something went wrong while reading this text. Please try again.";
        }
        finally
        {
            _upReading.Value = false;
        }
    }

    /// <summary>Says what happened to an added bill and opens the record it became, if any.</summary>
    private async Task ShowAddedAsync(UploadResult result, bool pasted)
    {
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
                    AttachmentStatus.ScannedNotRead or AttachmentStatus.NotSupported when AiReadingOn() => "Omabilo found no readable text in this file, even with OCR. Try a sharper, well-lit photo or the original PDF.",
                    AttachmentStatus.ScannedNotRead or AttachmentStatus.NotSupported when IsPremium() => "This is a scan or photo, which is read with OCR. OCR runs only when AI reading is on: turn it on in Plan & privacy, then add the file again.",
                    AttachmentStatus.ScannedNotRead or AttachmentStatus.NotSupported => "This is a scan or photo. Reading those is part of Premium. On the free plan, upload the original PDF or paste the bill's text instead.",
                    AttachmentStatus.TimedOut => "Reading this file took too long or OCR was busy. Please try again.",
                    _ => "Omabilo couldn't read this file this time. Please try again.",
                };
                break;
            case UploadOutcome.AlreadyFiled:
                _toasts.Success(opened
                    ? pasted ? "You've already added this text. Here is what Omabilo saved from it." : "You've already uploaded this file. Here is what Omabilo saved from it."
                    : "This is already being saved.");
                break;
            case UploadOutcome.NotFinancial:
                _upError.Value = pasted
                    ? "Omabilo read the text but found no bill, reminder, payslip or receipt details in it. Check that you pasted the whole bill, including the amount and due date."
                    : "Omabilo read the file but found no bill, reminder, payslip or receipt details in it.";
                break;
            case UploadOutcome.NotAPdf:
                _upError.Value = "That file isn't a PDF, JPG or PNG (or its name doesn't match its content). Save the bill in one of those formats and upload it again.";
                break;
            case UploadOutcome.TooLarge:
                _upError.Value = pasted ? $"The text is longer than {BillUpload.MaxPastedChars:N0} characters. Paste just the bill." : $"The file is larger than {BillUpload.MaxBytes / (1024 * 1024)} MB.";
                break;
            case UploadOutcome.Empty:
                _upError.Value = pasted ? "There's no text to read." : "The file is empty.";
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
}
