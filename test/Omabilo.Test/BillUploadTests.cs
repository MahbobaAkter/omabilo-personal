using Xunit;

/// <summary>"Upload a bill": a PDF saved for the signed-in uploader only, unconfirmed until reviewed,
/// filed once however often it is uploaded, and nothing else accepted. All data is fictional.</summary>
public class BillUploadTests
{
    [Fact]
    public async Task An_uploaded_pdf_bill_is_filed_for_the_uploader_only_and_waits_for_review()
    {
        var rig = new CompanionRig();
        var today = rig.Now.Date;

        var result = await rig.Engine.ImportUploadAsync("alice", "lasku-55120.pdf", PdfInvoiceTests.KuuraInvoice(today), default);

        Assert.Equal(UploadOutcome.Filed, result.Outcome);
        Assert.Equal(1, result.NewItems);

        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Equal(item.Id, result.ItemId);
        Assert.Equal("alice", item.UserId);
        Assert.Equal(FinKind.Invoice, item.Kind);
        Assert.Equal("Kuura Energia Oy", item.Counterparty);
        Assert.Equal(58.40m, item.Amount);
        Assert.Equal(today.AddDays(28), item.DueDate);

        // Read values say where they came from, and nothing is confirmed until the person reviews it
        Assert.Contains(item.FieldSources, f => f.Field == "Amount" && f.Source == "PDF “lasku-55120.pdf”");
        Assert.Equal(UserState.None, item.UserState);

        Assert.True(BillUpload.IsUploadId(item.Sources[0].GmailId));
        Assert.Null(item.AttachmentRetryAfterUtc);
        Assert.False(SyncEngine.CanReread(item));
        Assert.Null(await rig.Engine.ReprocessAsync("alice", item.Id, default));

        // Nothing went to Google, and bob sees nothing
        Assert.Empty(rig.Google.TokenRequests);
        Assert.Equal(0, rig.Google.FullFetches);
        Assert.Empty(await rig.Store.GetItemsAsync("bob", default));
        Assert.False(await rig.Service.DeleteItemAsync("bob", item.Id, default));
        Assert.Single(rig.Store.Items.Values);
    }

    [Fact]
    public async Task The_same_file_uploaded_twice_is_filed_once()
    {
        var rig = new CompanionRig();
        var pdf = PdfInvoiceTests.KuuraInvoice(rig.Now.Date);

        var first = await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", pdf, default);
        var again = await rig.Engine.ImportUploadAsync("alice", "renamed copy.pdf", pdf, default);

        Assert.Equal(UploadOutcome.Filed, first.Outcome);
        Assert.Equal(UploadOutcome.AlreadyFiled, again.Outcome);
        Assert.Equal(first.ItemId, again.ItemId);
        Assert.Single(rig.Store.Items.Values);

        // Another person uploading the same file gets their own record
        Assert.Equal(UploadOutcome.Filed, (await rig.Engine.ImportUploadAsync("bob", "lasku.pdf", pdf, default)).Outcome);
        Assert.Single(await rig.Store.GetItemsAsync("bob", default));
    }

    [Fact]
    public async Task A_deleted_upload_can_be_uploaded_again()
    {
        var rig = new CompanionRig();
        var pdf = PdfInvoiceTests.KuuraInvoice(rig.Now.Date);
        var first = await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", pdf, default);

        Assert.True(await rig.Service.DeleteItemAsync("alice", first.ItemId!, default));

        var again = await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", pdf, default);
        Assert.Equal(UploadOutcome.Filed, again.Outcome);
        Assert.Single(rig.Store.Items.Values);
    }

    [Fact]
    public async Task An_upload_left_unfinished_by_a_restart_can_be_uploaded_again()
    {
        var rig = new CompanionRig();
        var pdf = PdfInvoiceTests.KuuraInvoice(rig.Now.Date);
        var id = BillUpload.IdFor(pdf);
        rig.Store.Messages[("alice", id)] = new ProcessedMessage("alice", id, rig.Now, false, FinKind.NotFinancial, null, "processing", rig.Now);

        // Still being read: not read twice
        Assert.Equal(UploadOutcome.AlreadyFiled, (await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", pdf, default)).Outcome);
        Assert.Empty(rig.Store.Items);

        // Stuck for longer than any read takes: read again
        rig.Now = rig.Now.AddMinutes(11);
        Assert.Equal(UploadOutcome.Filed, (await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", pdf, default)).Outcome);
        Assert.Single(rig.Store.Items.Values);
    }

    [Fact]
    public async Task Only_real_pdfs_within_the_size_limit_are_read()
    {
        var rig = new CompanionRig();
        var pdf = PdfInvoiceTests.KuuraInvoice(rig.Now.Date);

        Assert.Equal(UploadOutcome.NotAPdf, (await rig.Engine.ImportUploadAsync("alice", "lasku.txt", pdf, default)).Outcome);
        Assert.Equal(UploadOutcome.NotAPdf, (await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", "<html>not a pdf</html>"u8.ToArray(), default)).Outcome);
        Assert.Equal(UploadOutcome.Empty, (await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", [], default)).Outcome);

        var huge = new byte[BillUpload.MaxBytes + 1];
        "%PDF-1.7"u8.CopyTo(huge);
        Assert.Equal(UploadOutcome.TooLarge, (await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", huge, default)).Outcome);

        Assert.Empty(rig.Store.Items);
        Assert.Empty(rig.Store.Messages);
    }

    [Fact]
    public async Task An_uploaded_payslip_goes_to_salary_and_waits_for_review()
    {
        var rig = new CompanionRig();
        var pdf = GmailSyncTests.Pdf(Mails.Payslip(rig.Now.Date.AddDays(8)));

        var result = await rig.Engine.ImportUploadAsync("alice", "palkkalaskelma-syyskuu.pdf", pdf, default);

        Assert.Equal(UploadOutcome.Filed, result.Outcome);
        var item = rig.Store.Items[result.ItemId!];
        Assert.Equal(FinKind.Payslip, item.Kind);
        Assert.True(item.IsIncome);
        Assert.Equal(2412.40m, item.Net);
        Assert.False(ItemRules.CountsAsOpenObligation(item));
        Assert.True(ItemRules.StatusOf(item, rig.Now.Date).NeedsReview);
    }

    [Fact]
    public async Task An_uploaded_tax_decision_is_filed_under_taxes_without_an_invented_due_date()
    {
        var rig = new CompanionRig();
        var pdf = GmailSyncTests.Pdf("Verohallinto\nTax decision 2025\nResidual tax 312,00 EUR\nTest Customer, Oulu");

        var result = await rig.Engine.ImportUploadAsync("alice", "verotuspaatos-2025.pdf", pdf, default);

        Assert.Equal(UploadOutcome.Filed, result.Outcome);
        var item = rig.Store.Items[result.ItemId!];
        Assert.Equal(FinKind.TaxNotice, item.Kind);
        Assert.Equal(Categories.Taxes, item.Category);
        Assert.Null(item.DueDate);
        Assert.Null(item.Reference);
        Assert.True(ItemRules.StatusOf(item, rig.Now.Date).NeedsReview);
    }

    [Fact]
    public async Task A_pdf_without_financial_details_saves_nothing()
    {
        var rig = new CompanionRig();
        var pdf = GmailSyncTests.Pdf("Blueberry pie\nMix the flour, butter and sugar.\nBake for forty minutes in a hot oven.");

        var result = await rig.Engine.ImportUploadAsync("alice", "document.pdf", pdf, default);

        Assert.Equal(UploadOutcome.NotFinancial, result.Outcome);
        Assert.Null(result.ItemId);
        Assert.Empty(rig.Store.Items);
    }

    [Fact]
    public async Task A_pdf_whose_text_cant_be_read_is_not_saved_as_a_guessed_bill()
    {
        var rig = new CompanionRig();

        // Named like a bill, so a mail attachment like this would become a "document to check"
        var locked = await rig.Engine.ImportUploadAsync("alice", "lasku-locked.pdf", PdfInvoiceTests.LockedPdf(), default);
        var scan = await rig.Engine.ImportUploadAsync("alice", "lasku-scan.pdf", PdfInvoiceTests.LaidOutPdf((40, 800, "x 12")), default);
        var damaged = await rig.Engine.ImportUploadAsync("alice", "lasku-damaged.pdf", "%PDF-1.7\n1 0 obj << broken"u8.ToArray(), default);

        Assert.Equal((UploadOutcome.Unreadable, AttachmentStatus.PasswordProtected), (locked.Outcome, locked.Unread));
        Assert.Equal((UploadOutcome.Unreadable, AttachmentStatus.ScannedNotRead), (scan.Outcome, scan.Unread));
        Assert.Equal(UploadOutcome.Unreadable, damaged.Outcome);
        Assert.Contains(damaged.Unread, new AttachmentStatus?[] { AttachmentStatus.Damaged, AttachmentStatus.ScannedNotRead });
        Assert.Empty(rig.Store.Items);

        // Not remembered as finished, so the same file can be tried again (after turning on OCR, say)
        Assert.Equal(UploadOutcome.Unreadable, (await rig.Engine.ImportUploadAsync("alice", "lasku-locked.pdf", PdfInvoiceTests.LockedPdf(), default)).Outcome);
    }

    [Fact]
    public async Task A_failure_while_reading_saves_nothing_and_the_file_can_be_uploaded_again()
    {
        var reader = new FailOnceReader();
        var rig = new CompanionRig(reader: reader);
        var pdf = PdfInvoiceTests.KuuraInvoice(rig.Now.Date);

        Assert.Equal(UploadOutcome.Failed, (await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", pdf, default)).Outcome);
        Assert.Empty(rig.Store.Items);
        Assert.DoesNotContain(rig.Logs, l => l.Contains("Kuura") || l.Contains("58,40") || l.Contains("lasku.pdf"));

        Assert.Equal(UploadOutcome.Filed, (await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", pdf, default)).Outcome);
        Assert.Single(rig.Store.Items);
    }

    [Fact]
    public async Task Another_copy_of_a_bill_already_saved_adds_to_that_record()
    {
        var rig = new CompanionRig();
        var pdf = PdfInvoiceTests.KuuraInvoice(rig.Now.Date);
        var first = await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", pdf, default);

        // A different file (one byte more) of the same invoice
        var copy = await rig.Engine.ImportUploadAsync("alice", "lasku (1).pdf", [.. pdf, (byte)'\n'], default);

        Assert.Equal(UploadOutcome.Filed, copy.Outcome);
        Assert.Equal(0, copy.NewItems);
        Assert.Equal(first.ItemId, copy.ItemId);
        Assert.Single(rig.Store.Items);
    }

    private sealed class FailOnceReader : IAttachmentReader
    {
        private bool _failed;

        public Task<AttachmentReadResult> ReadAsync(string fileName, string mimeType, byte[] data, CancellationToken ct)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("reader crashed");
            }

            return new PdfAttachmentReader().ReadAsync(fileName, mimeType, data, ct);
        }
    }

    [Fact]
    public void File_names_are_cleaned_before_they_are_shown()
    {
        Assert.Equal("lasku.pdf", BillUpload.CleanFileName(@"C:\Users\someone\lasku.pdf"));
        Assert.Equal("lasku.pdf", BillUpload.CleanFileName("../../lasku.pdf"));
        Assert.Equal("document.pdf", BillUpload.CleanFileName("   "));
        Assert.Equal(BillUpload.IdFor([1, 2, 3]), BillUpload.IdFor([1, 2, 3]));
        Assert.NotEqual(BillUpload.IdFor([1, 2, 3]), BillUpload.IdFor([1, 2, 4]));
    }
}
